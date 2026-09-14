using System.Text.RegularExpressions;
using RepairRequest.Application.Attachments;

namespace RepairRequest.Application.Tests.Attachments;

/// <summary>File policy: PDF/JPG/JPEG/PNG, matching content type and signature, sanitized display names, opaque keys.</summary>
public class AttachmentFileRulesTests
{
    [Theory]
    [InlineData("photo.png", "photo.png")]
    [InlineData("../../evil.png", "evil.png")]
    [InlineData(@"C:\Users\x\evil.png", "evil.png")]
    [InlineData("  report.pdf.  ", "report.pdf")]
    [InlineData("in\u0000vo\u0007ice.pdf", "invoice.pdf")]
    [InlineData("a<b>:c|d?e*f\".png", "abcdef.png")]
    [InlineData("invoice\u202Egnp.exe", "invoicegnp.exe")]
    [InlineData("line\r\nbreak.jpg", "linebreak.jpg")]
    [InlineData("ใบแจ้งซ่อม.jpeg", "ใบแจ้งซ่อม.jpeg")]
    public void SanitizeFileName_KeepsOnlyASafeLastSegment(string input, string expected)
    {
        Assert.Equal(expected, AttachmentFileRules.SanitizeFileName(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".png")]
    [InlineData("../")]
    [InlineData("\u0000\u0001")]
    public void SanitizeFileName_WithNothingUsable_ReturnsNull(string? input)
    {
        Assert.Null(AttachmentFileRules.SanitizeFileName(input));
    }

    [Fact]
    public void SanitizeFileName_TooLong_IsShortenedKeepingExtension()
    {
        var sanitized = AttachmentFileRules.SanitizeFileName(new string('n', 400) + ".png");

        Assert.Equal(255, sanitized!.Length);
        Assert.EndsWith(".png", sanitized, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("a.PNG", "image/png", "image/png")]
    [InlineData("a.jpg", "image/jpeg", "image/jpeg")]
    [InlineData("a.JPEG", "IMAGE/JPEG; charset=binary", "image/jpeg")]
    [InlineData("a.pdf", "application/pdf", "application/pdf")]
    public void TypeFor_AllowedExtensionWithMatchingContentType_ReturnsCanonicalType(string name, string contentType, string canonical)
    {
        Assert.Equal(canonical, AttachmentFileRules.TypeFor(name, contentType)!.CanonicalMimeType);
    }

    [Theory]
    [InlineData("a.png", "image/jpeg")]
    [InlineData("a.pdf", "image/png")]
    [InlineData("a.gif", "image/gif")]
    [InlineData("a.exe", "application/octet-stream")]
    [InlineData("a.png.exe", "image/png")]
    [InlineData("a.svg", "image/svg+xml")]
    [InlineData("a.pdf", null)]
    public void TypeFor_DisallowedOrMismatchedType_ReturnsNull(string name, string? contentType)
    {
        Assert.Null(AttachmentFileRules.TypeFor(name, contentType));
    }

    [Fact]
    public async Task HasSignature_ChecksLeadingBytes_AndRewinds()
    {
        var png = AttachmentFileRules.TypeFor("a.png", "image/png")!;
        var pdf = AttachmentFileRules.TypeFor("a.pdf", "application/pdf")!;
        var jpeg = AttachmentFileRules.TypeFor("a.jpg", "image/jpeg")!;
        await using var pngContent = new MemoryStream(TestFiles.Png());

        Assert.True(await AttachmentFileRules.HasSignatureAsync(pngContent, png, CancellationToken.None));
        Assert.Equal(0, pngContent.Position);
        Assert.False(await AttachmentFileRules.HasSignatureAsync(pngContent, pdf, CancellationToken.None));
        Assert.True(await AttachmentFileRules.HasSignatureAsync(new MemoryStream(TestFiles.Jpeg()), jpeg, CancellationToken.None));
        Assert.True(await AttachmentFileRules.HasSignatureAsync(new MemoryStream(TestFiles.Pdf()), pdf, CancellationToken.None));
        Assert.False(await AttachmentFileRules.HasSignatureAsync(new MemoryStream([0x89, 0x50]), png, CancellationToken.None));
        Assert.False(await AttachmentFileRules.HasSignatureAsync(new MemoryStream(TestFiles.Gif()), png, CancellationToken.None));
    }

    [Fact]
    public void NewStorageKey_IsOpaqueTenantPartitionedAndUnique()
    {
        var tenantId = Guid.NewGuid();

        var first = AttachmentFileRules.NewStorageKey(tenantId);
        var second = AttachmentFileRules.NewStorageKey(tenantId);

        Assert.Matches(new Regex("^[0-9a-f]{32}/[0-9a-f]{32}$"), first);
        Assert.StartsWith(tenantId.ToString("N"), first, StringComparison.Ordinal);
        Assert.NotEqual(first, second);
    }
}
