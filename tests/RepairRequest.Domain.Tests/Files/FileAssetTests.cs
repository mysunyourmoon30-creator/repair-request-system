using RepairRequest.Domain.Files;

namespace RepairRequest.Domain.Tests.Files;

public class FileAssetTests
{
    private const string ValidHash = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";

    private static FileAsset Create(
        string contentHash = ValidHash,
        long sizeBytes = 1024,
        DateTimeKind uploadedAtKind = DateTimeKind.Utc) =>
        new(
            Guid.NewGuid(),
            "evidence.pdf",
            "application/pdf",
            sizeBytes,
            contentHash,
            "private/tenant/file-001",
            Guid.NewGuid(),
            new DateTime(2026, 9, 13, 8, 0, 0, uploadedAtKind));

    [Fact]
    public void NewUpload_StartsPendingScan()
    {
        Assert.Equal(MalwareScanStatus.Pending, Create().MalwareScanStatus);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("zz86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08")]
    public void InvalidSha256_IsRejected(string contentHash)
    {
        Assert.Throws<ArgumentException>(() => Create(contentHash: contentHash));
    }

    [Fact]
    public void NegativeSize_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(sizeBytes: -1));
    }

    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public void NonUtcUploadTime_IsRejected(DateTimeKind kind)
    {
        Assert.Throws<ArgumentException>(() => Create(uploadedAtKind: kind));
    }
}
