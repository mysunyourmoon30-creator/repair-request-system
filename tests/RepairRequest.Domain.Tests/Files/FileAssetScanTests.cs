using RepairRequest.Domain.Common;
using RepairRequest.Domain.Files;

namespace RepairRequest.Domain.Tests.Files;

/// <summary>FAS-008 scan result transitions: only a PENDING file receives a result (RR-ARCH-001 section 11).</summary>
public class FileAssetScanTests
{
    private static FileAsset NewFile() =>
        new(Guid.NewGuid(), "photo.png", "image/png", 128, new string('a', 64), "key", Guid.NewGuid(), DateTime.UtcNow);

    [Fact]
    public void NewFile_StartsPending()
    {
        Assert.Equal(MalwareScanStatus.Pending, NewFile().MalwareScanStatus);
    }

    [Fact]
    public void MarkClean_FromPending_BecomesClean()
    {
        var file = NewFile();

        file.MarkClean();

        Assert.Equal(MalwareScanStatus.Clean, file.MalwareScanStatus);
    }

    [Fact]
    public void MarkFailed_FromPending_BecomesFailed()
    {
        var file = NewFile();

        file.MarkFailed();

        Assert.Equal(MalwareScanStatus.Failed, file.MalwareScanStatus);
    }

    [Fact]
    public void ScanResult_CannotBeChangedOnceSet()
    {
        var clean = NewFile();
        clean.MarkClean();
        var failed = NewFile();
        failed.MarkFailed();

        Assert.Throws<DomainRuleViolationException>(clean.MarkFailed);
        Assert.Throws<DomainRuleViolationException>(failed.MarkClean);
        Assert.Equal(MalwareScanStatus.Clean, clean.MalwareScanStatus);
        Assert.Equal(MalwareScanStatus.Failed, failed.MalwareScanStatus);
    }
}
