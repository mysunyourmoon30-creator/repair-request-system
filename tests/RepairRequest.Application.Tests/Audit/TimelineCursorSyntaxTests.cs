using System.Buffers.Text;
using RepairRequest.Application.Audit;

namespace RepairRequest.Application.Tests.Audit;

/// <summary>Step 3 of the validation order (docs/15 §5): cursor syntax only — Base64url, no padding, 57 bytes, at most 128 characters.</summary>
public class TimelineCursorSyntaxTests
{
    private static string Cursor(int bytes) => Base64Url.EncodeToString(new byte[bytes]);

    [Fact]
    public void ACursorOf57Bytes_Decodes_To76Characters()
    {
        var text = Cursor(TimelineCursorFormat.TotalLength);

        Assert.Equal(76, text.Length);
        Assert.True(TimelineCursorSyntax.TryDecode(text, out var bytes));
        Assert.Equal(TimelineCursorFormat.TotalLength, bytes.Length);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(56)]
    [InlineData(58)]
    [InlineData(64)]
    public void AnyOtherDecodedLength_IsRejected(int length)
    {
        Assert.False(TimelineCursorSyntax.TryDecode(Cursor(length), out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("!!!!")]
    [InlineData("AAAA AAAA")]
    public void NullEmptyOrNonBase64Url_IsRejected(string? text)
    {
        Assert.False(TimelineCursorSyntax.TryDecode(text, out _));
    }

    [Fact]
    public void StandardBase64WithPlusSlashOrPadding_IsRejected()
    {
        var bytes = new byte[TimelineCursorFormat.TotalLength];
        Array.Fill(bytes, (byte)0xFB); // encodes with '+' / '/' in standard Base64
        var standard = Convert.ToBase64String(bytes);

        Assert.Contains('+', standard);
        Assert.False(TimelineCursorSyntax.TryDecode(standard, out _));
        Assert.False(TimelineCursorSyntax.TryDecode(Cursor(TimelineCursorFormat.TotalLength) + "=", out _));
    }

    [Fact]
    public void TextLongerThan128Characters_IsRejected_EvenIfItWouldDecode()
    {
        var longText = new string('A', TimelineCursorFormat.MaxTextLength + 4);

        Assert.False(TimelineCursorSyntax.TryDecode(longText, out _));
    }
}
