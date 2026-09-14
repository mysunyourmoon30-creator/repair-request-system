using RepairRequest.Application.Attachments;
using RepairRequest.Application.Common;
using RepairRequest.Application.Tests.MasterData;
using RepairRequest.Domain.Files;

namespace RepairRequest.Application.Tests.Attachments;

/// <summary>Decision F2: Clean -> CLEAN, Infected -> FAILED (audited, only while PENDING); unavailable or failing scanner -> stays PENDING.</summary>
public class FileScanServiceTests
{
    private readonly FakeAttachmentStore _store = new();
    private readonly FakeFileStorage _storage = new();
    private readonly FakeMalwareScanner _scanner = new();
    private readonly FileScanService _service;
    private readonly Guid _tenantId = Guid.NewGuid();

    public FileScanServiceTests()
    {
        _service = new FileScanService(_store, _storage, _scanner, new FixedClock(DateTimeOffset.UtcNow));
    }

    private FileAsset SeedPending(byte[] content)
    {
        var key = AttachmentFileRules.NewStorageKey(_tenantId);
        _storage.Objects[key] = content;
        return _store.SeedFile(_tenantId, Guid.NewGuid(), key);
    }

    [Fact]
    public async Task CleanScan_MarksClean_AndAuditsAsSystem()
    {
        var file = SeedPending(TestFiles.Png());

        var result = await _service.ScanAsync(file.Id, CancellationToken.None);

        Assert.Equal(FileScanResult.Clean, result);
        Assert.Equal(MalwareScanStatus.Clean, file.MalwareScanStatus);
        var audit = Assert.Single(_store.Audits);
        Assert.Equal(AttachmentAudit.FileAssetEntityType, audit.EntityType);
        Assert.Equal(file.Id, audit.EntityId);
        Assert.Equal(AttachmentAudit.ScanCleanAction, audit.ActionCode);
        Assert.Equal("PENDING", audit.FromState);
        Assert.Equal("CLEAN", audit.ToState);
        Assert.Equal(SystemActors.MalwareScanner, audit.ActorId);
    }

    [Fact]
    public async Task InfectedScan_MarksFailed_AndAudits()
    {
        var file = SeedPending(TestFiles.InfectedPng());

        var result = await _service.ScanAsync(file.Id, CancellationToken.None);

        Assert.Equal(FileScanResult.Failed, result);
        Assert.Equal(MalwareScanStatus.Failed, file.MalwareScanStatus);
        Assert.Equal(AttachmentAudit.ScanFailedAction, Assert.Single(_store.Audits).ActionCode);
    }

    [Fact]
    public async Task UnavailableScanner_LeavesFilePending_WithoutAudit()
    {
        var file = SeedPending(TestFiles.Png());
        _scanner.FixedOutcome = MalwareScanOutcome.Unavailable;

        var result = await _service.ScanAsync(file.Id, CancellationToken.None);

        Assert.Equal(FileScanResult.StillPending, result);
        Assert.Equal(MalwareScanStatus.Pending, file.MalwareScanStatus);
        Assert.Empty(_store.Audits);
    }

    [Fact]
    public async Task FailingScanner_LeavesFilePending_WithoutAudit()
    {
        var file = SeedPending(TestFiles.Png());
        _scanner.Throw = true;

        var result = await _service.ScanAsync(file.Id, CancellationToken.None);

        Assert.Equal(FileScanResult.StillPending, result);
        Assert.Equal(MalwareScanStatus.Pending, file.MalwareScanStatus);
        Assert.Empty(_store.Audits);
    }

    [Fact]
    public async Task MissingBinary_LeavesFilePending()
    {
        var file = _store.SeedFile(_tenantId, Guid.NewGuid(), AttachmentFileRules.NewStorageKey(_tenantId));

        Assert.Equal(FileScanResult.StillPending, await _service.ScanAsync(file.Id, CancellationToken.None));
        Assert.Equal(0, _scanner.Calls);
    }

    [Fact]
    public async Task AlreadyScannedOrUnknownFile_IsNotScannedAgain()
    {
        var clean = _store.SeedFile(_tenantId, Guid.NewGuid(), AttachmentFileRules.NewStorageKey(_tenantId), MalwareScanStatus.Clean);

        Assert.Equal(FileScanResult.NotPending, await _service.ScanAsync(clean.Id, CancellationToken.None));
        Assert.Equal(FileScanResult.NotPending, await _service.ScanAsync(Guid.NewGuid(), CancellationToken.None));
        Assert.Equal(0, _scanner.Calls);
    }

    [Fact]
    public async Task ConcurrentCompletion_IsNotAppliedTwice()
    {
        var file = SeedPending(TestFiles.Png());
        _store.LoseScanRace = true;

        var result = await _service.ScanAsync(file.Id, CancellationToken.None);

        Assert.Equal(FileScanResult.NotPending, result);
        Assert.Equal(MalwareScanStatus.Pending, file.MalwareScanStatus);
        Assert.Empty(_store.Audits);
    }
}
