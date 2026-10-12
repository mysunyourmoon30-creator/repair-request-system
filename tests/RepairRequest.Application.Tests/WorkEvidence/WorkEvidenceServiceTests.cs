using System.Text.Json;
using RepairRequest.Application.Attachments;
using RepairRequest.Application.Common;
using RepairRequest.Application.Security;
using RepairRequest.Application.Tests.Attachments;
using RepairRequest.Application.Tests.MasterData;
using RepairRequest.Application.WorkEvidence;
using RepairRequest.Domain.Files;
using RepairRequest.Domain.Security;
using RepairRequest.Domain.WorkEvidence;
using RepairRequest.Domain.WorkOrders;

namespace RepairRequest.Application.Tests.WorkEvidence;

/// <summary>
/// WEV-API-001/003 service rules (`docs/16` OD1..OD3; `docs/13` §4.28): upload order 404 → 409 state → 422 fields → 409 round, private opaque
/// storage and compensation, CLEAN-only audience-checked download. Scope, locking and races against Submit are covered by the SQL Server tests.
/// </summary>
public class WorkEvidenceServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 9, 30, 0, 456, TimeSpan.Zero);

    private readonly FakeWorkEvidenceStore _store = new();
    private readonly FakeFileStorage _storage = new();
    private readonly WorkEvidenceService _service;
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _workOrderId = Guid.NewGuid();
    private readonly Guid _visitId = Guid.NewGuid();
    private readonly Guid _technicianId = Guid.NewGuid();

    public WorkEvidenceServiceTests()
    {
        _service = new WorkEvidenceService(_store, _storage, new FixedClock(Now));
        _store.Target = new WorkEvidenceUploadTarget(_tenantId, _workOrderId, WorkOrderStatus.InProgress, _visitId, VisitHasWorkSummary: false);
    }

    private CommandContext Technician() => new(new CurrentUser(_technicianId, _tenantId, [RoleCodes.Technician]), Guid.NewGuid());

    private static WorkEvidenceFile File(string? name, string? contentType, byte[] content, long? declaredLength = null) =>
        new(name, contentType, declaredLength ?? content.Length, () => new MemoryStream(content, writable: false));

    private Task<CommandResult<WorkEvidenceItemDto>> UploadAsync(string? type, WorkEvidenceFile file, CommandContext? context = null) =>
        _service.UploadAsync(context ?? Technician(), _workOrderId, type, file, CancellationToken.None);

    private void AssertNothingWritten()
    {
        Assert.Empty(_store.Files);
        Assert.Empty(_store.Uploads);
        Assert.Empty(_store.Audits);
        Assert.Empty(_storage.Objects);
    }

    // ---------------- upload: success ----------------

    [Fact]
    public async Task Upload_StoresAPendingFile_UnderAnOpaqueKey_BindsItToTheRoundVisit_AndAuditsOnce()
    {
        var content = TestFiles.Png();
        var context = Technician();

        var result = await UploadAsync("before_work", File("../../Pump Before.png", "image/png", content), context);

        Assert.True(result.Succeeded);
        var item = result.Value!;
        Assert.Equal(WorkEvidenceType.BeforeWork, item.EvidenceType);
        Assert.Equal("Pump Before.png", item.FileName);
        Assert.Equal("image/png", item.MimeType);
        Assert.Equal(content.Length, item.SizeBytes);
        Assert.Equal(MalwareScanStatus.Pending, item.ScanStatus);
        Assert.False(item.Bound);
        Assert.Null(item.WorkSummaryId);
        Assert.Equal(_visitId, item.ServiceVisitId);

        var file = Assert.Single(_store.Files);
        Assert.Equal(MalwareScanStatus.Pending, file.MalwareScanStatus);
        Assert.Equal(_tenantId, file.TenantId);
        Assert.Equal(_technicianId, file.UploadedBy);
        Assert.StartsWith($"{_tenantId:N}/", file.StorageReference, StringComparison.Ordinal);
        Assert.DoesNotContain("Pump", file.StorageReference, StringComparison.Ordinal);
        Assert.Equal(content, _storage.Objects[file.StorageReference]);

        var upload = Assert.Single(_store.Uploads);
        Assert.Equal(item.EvidenceId, upload.Id);
        Assert.Equal(file.Id, upload.FileAssetId);
        Assert.Equal(_workOrderId, upload.WorkOrderId);
        Assert.Equal(_visitId, upload.ServiceVisitId);
        Assert.Equal(Now.UtcDateTime.AddTicks(-(Now.UtcDateTime.Ticks % TimeSpan.TicksPerMillisecond)), upload.CreatedAt);

        var audit = Assert.Single(_store.Audits);
        Assert.Equal(WorkEvidenceAudit.UploadedAction, audit.ActionCode);
        Assert.Equal(WorkEvidenceAudit.EntityType, audit.EntityType);
        Assert.Equal(upload.Id, audit.EntityId);
        Assert.Equal(_technicianId, audit.ActorId);
        Assert.Equal(context.CorrelationId, audit.CorrelationId);
        var values = JsonDocument.Parse(audit.NewValueJson!).RootElement;
        Assert.Equal("BEFORE_WORK", values.GetProperty("evidenceType").GetString());
        Assert.Equal("PENDING", values.GetProperty("malwareScanStatus").GetString());
        Assert.DoesNotContain(file.StorageReference, audit.NewValueJson!, StringComparison.Ordinal);
        Assert.DoesNotContain(file.ContentHash, audit.NewValueJson!, StringComparison.Ordinal);
        Assert.Equal(1, _store.LockCalls);
    }

    [Theory]
    [InlineData("BEFORE_WORK", WorkEvidenceType.BeforeWork)]
    [InlineData("AFTER_WORK", WorkEvidenceType.AfterWork)]
    [InlineData("OTHER", WorkEvidenceType.Other)]
    public async Task Upload_AcceptsEachCatalogType(string code, WorkEvidenceType expected)
    {
        var result = await UploadAsync(code, File("a.pdf", "application/pdf", TestFiles.Pdf()));

        Assert.True(result.Succeeded);
        Assert.Equal(expected, result.Value!.EvidenceType);
    }

    [Theory]
    [InlineData("a.jpg", "image/jpeg")]
    [InlineData("a.JPEG", "image/jpeg")]
    [InlineData("a.png", "image/png; charset=binary")]
    public async Task Upload_AppliesTheSameFilePolicyAsAttachments(string name, string contentType)
    {
        var content = name.EndsWith("png", StringComparison.OrdinalIgnoreCase) ? TestFiles.Png() : TestFiles.Jpeg();

        Assert.True((await UploadAsync("OTHER", File(name, contentType, content))).Succeeded);
    }

    // ---------------- upload: order of checks ----------------

    [Fact]
    public async Task Upload_WithoutATarget_IsNotFound_AndTouchesNothing()
    {
        _store.Target = null;

        var result = await UploadAsync("OTHER", File("a.png", "image/png", TestFiles.Png()));

        Assert.Equal(CommandFailure.NotFound, result.Error?.Failure);
        AssertNothingWritten();
        Assert.Equal(0, _store.LockCalls);
    }

    [Theory]
    [InlineData(WorkOrderStatus.Open)]
    [InlineData(WorkOrderStatus.Scheduled)]
    [InlineData(WorkOrderStatus.AwaitingSupervisorReview)]
    [InlineData(WorkOrderStatus.AwaitingCustomerAcceptance)]
    [InlineData(WorkOrderStatus.Completed)]
    [InlineData(WorkOrderStatus.CorrectiveActionRequired)]
    [InlineData(WorkOrderStatus.CorrectivePlanPending)]
    [InlineData(WorkOrderStatus.CorrectivePlanApproved)]
    [InlineData(WorkOrderStatus.Closed)]
    [InlineData(WorkOrderStatus.Cancelled)]
    public async Task Upload_IsOnlyAllowedWhileTheWorkOrderIsInProgress(WorkOrderStatus status)
    {
        _store.Target = new WorkEvidenceUploadTarget(_tenantId, _workOrderId, status, _visitId, false);

        var result = await UploadAsync("OTHER", File("a.png", "image/png", TestFiles.Png()));

        Assert.Equal(CommandFailure.StateConflict, result.Error?.Failure);
        AssertNothingWritten();
    }

    [Fact]
    public async Task Upload_ForAVisitWhoseWorkSummaryWasSubmitted_IsAStateConflict_BecauseTheSetIsFrozen()
    {
        _store.Target = new WorkEvidenceUploadTarget(_tenantId, _workOrderId, WorkOrderStatus.InProgress, _visitId, VisitHasWorkSummary: true);

        var result = await UploadAsync("OTHER", File("a.png", "image/png", TestFiles.Png()));

        Assert.Equal(CommandFailure.StateConflict, result.Error?.Failure);
        Assert.Contains("frozen", result.Error!.Message, StringComparison.OrdinalIgnoreCase);
        AssertNothingWritten();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("DURING_WORK")]
    [InlineData("PHOTO")]
    public async Task Upload_WithAnEvidenceTypeOutsideTheCatalog_Is422OnEvidenceType_AndWritesNothing(string? type)
    {
        var result = await UploadAsync(type, File("a.png", "image/png", TestFiles.Png()));

        Assert.Equal(CommandFailure.ValidationFailed, result.Error?.Failure);
        Assert.True(result.Error!.Errors.ContainsKey(WorkEvidenceFields.EvidenceType));
        AssertNothingWritten();
    }

    [Theory]
    [InlineData(null, "image/png")]
    [InlineData("a.gif", "image/gif")]
    [InlineData("a.png", "image/jpeg")]
    [InlineData("a.exe", "application/octet-stream")]
    [InlineData("...", "image/png")]
    [InlineData("../..", "image/png")]
    public async Task Upload_WithAnUnsupportedOrMismatchedFile_Is422OnFile_AndWritesNothing(string? name, string? contentType)
    {
        var result = await UploadAsync("OTHER", File(name, contentType, TestFiles.Png()));

        Assert.Equal(CommandFailure.ValidationFailed, result.Error?.Failure);
        Assert.True(result.Error!.Errors.ContainsKey(WorkEvidenceFields.File));
        AssertNothingWritten();
    }

    [Fact]
    public async Task Upload_ReportsTheEvidenceTypeAndFileErrorsTogether()
    {
        var result = await UploadAsync("NOPE", File("a.gif", "image/gif", TestFiles.Gif()));

        Assert.Equal(CommandFailure.ValidationFailed, result.Error?.Failure);
        Assert.True(result.Error!.Errors.ContainsKey(WorkEvidenceFields.EvidenceType));
        Assert.True(result.Error.Errors.ContainsKey(WorkEvidenceFields.File));
    }

    [Fact]
    public async Task Upload_OfAnEmptyOrOversizedFile_Is422()
    {
        Assert.Equal(
            CommandFailure.ValidationFailed,
            (await UploadAsync("OTHER", File("a.png", "image/png", [], declaredLength: 0))).Error?.Failure);
        Assert.Equal(
            CommandFailure.ValidationFailed,
            (await UploadAsync("OTHER", File("a.png", "image/png", TestFiles.Png(), declaredLength: AttachmentFileRules.MaxSizeBytes + 1))).Error?.Failure);
        AssertNothingWritten();
    }

    [Fact]
    public async Task Upload_WhoseContentSignatureDoesNotMatch_Is422_AndStoresNothing()
    {
        var result = await UploadAsync("OTHER", File("a.png", "image/png", TestFiles.Gif()));

        Assert.Equal(CommandFailure.ValidationFailed, result.Error?.Failure);
        Assert.Empty(_storage.Objects);
        AssertNothingWritten();
    }

    // ---------------- upload: no per-round attempt count ----------------

    [Fact]
    public async Task Upload_HasNoPerRoundCountOrLifetimeAttemptLimit_SoARoundCanNeverBeBlockedByFailedOrUnselectedUploads()
    {
        // The only business bound is ten BOUND items at Submit. Uploading carries no count: no delete/withdraw exists in V1, so any count of
        // PENDING, FAILED or unselected uploads would be a permanent block. Far more than the former 30 attempts all succeed.
        for (var i = 0; i < 45; i++)
        {
            var result = await UploadAsync("OTHER", File($"a{i}.png", "image/png", TestFiles.Png()));
            Assert.True(result.Succeeded, $"upload {i}");
        }

        Assert.Equal(45, _store.Uploads.Count);
        Assert.Equal(45, _store.Files.Count);
    }

    [Fact]
    public void TheServiceDeclaresNoUploadCountConstant_AndTheStorePortHasNoRoundCountOperation()
    {
        Assert.DoesNotContain(
            typeof(WorkEvidenceService).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static),
            field => field.Name.Contains("Upload", StringComparison.OrdinalIgnoreCase) || field.Name.Contains("Max", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            typeof(IWorkEvidenceStore).GetMethods(),
            method => method.Name.Contains("Count", StringComparison.OrdinalIgnoreCase));
    }

    // ---------------- upload: storage and database failures ----------------

    [Fact]
    public async Task Upload_WhenTheStorageFails_ThrowsWithNoMetadata()
    {
        _storage.FailSave = true;

        await Assert.ThrowsAsync<IOException>(() => UploadAsync("OTHER", File("a.png", "image/png", TestFiles.Png())));

        AssertNothingWritten();
    }

    [Fact]
    public async Task Upload_WhenTheDatabaseSaveFails_DeletesTheBinary_AndCommitsNothing()
    {
        _store.FailSave = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => UploadAsync("OTHER", File("a.png", "image/png", TestFiles.Png())));

        Assert.Empty(_store.Files);
        Assert.Empty(_store.Uploads);
        Assert.Empty(_store.Audits);
        Assert.Empty(_storage.Objects);
        Assert.Single(_storage.Deleted);
    }

    [Fact]
    public async Task Upload_WhenTheEvidenceLockIsNotGranted_IsAConcurrencyConflict_AndDeletesTheBinary()
    {
        _store.LockGranted = false;

        var result = await UploadAsync("OTHER", File("a.png", "image/png", TestFiles.Png()));

        Assert.Equal(CommandFailure.ConcurrencyConflict, result.Error?.Failure);
        AssertNothingWritten();
        Assert.Single(_storage.Deleted);
    }

    // ---------------- download ----------------

    private WorkEvidenceDownloadInfo Info(ReportAudience audience, bool bound, MalwareScanStatus status, string storageKey = "tenant/key") =>
        new(_tenantId, _workOrderId, Guid.NewGuid(), audience, bound, "photo.png", "image/png", 8, storageKey, status);

    private Task<CommandResult<WorkEvidenceDownload>> DownloadAsync(CommandContext? context = null) =>
        _service.DownloadAsync(context ?? Technician(), _workOrderId, Guid.NewGuid(), CancellationToken.None);

    [Fact]
    public async Task Download_OfAMissingOrInvisibleItem_IsNotFound()
    {
        _store.DownloadInfo = null;

        Assert.Equal(CommandFailure.NotFound, (await DownloadAsync()).Error?.Failure);
        Assert.Empty(_store.Audits);
    }

    [Theory]
    [InlineData(MalwareScanStatus.Pending)]
    [InlineData(MalwareScanStatus.Failed)]
    public async Task Download_OfANonCleanFile_IsAStateConflict_WithoutTouchingTheStorage_ForEveryAudience(MalwareScanStatus status)
    {
        foreach (var audience in new[] { ReportAudience.Technician, ReportAudience.TeamLeadSupervisor, ReportAudience.AcceptanceContact })
        {
            _store.DownloadInfo = Info(audience, bound: true, status);

            var result = await DownloadAsync();

            Assert.Equal(CommandFailure.StateConflict, result.Error?.Failure);
            Assert.DoesNotContain("tenant/key", result.Error!.Message, StringComparison.Ordinal);
        }

        Assert.Equal(0, _storage.OpenCount);
        Assert.Empty(_store.Audits);
    }

    [Theory]
    [InlineData(ReportAudience.TeamLeadSupervisor)]
    [InlineData(ReportAudience.AcceptanceContact)]
    public async Task Download_OfAnUnboundItem_IsNotFound_ExceptForTheAssignedTechnician(ReportAudience audience)
    {
        _store.DownloadInfo = Info(audience, bound: false, MalwareScanStatus.Clean);

        Assert.Equal(CommandFailure.NotFound, (await DownloadAsync()).Error?.Failure);
        Assert.Equal(0, _storage.OpenCount);
    }

    [Theory]
    [InlineData(ReportAudience.Technician, false)]
    [InlineData(ReportAudience.Technician, true)]
    [InlineData(ReportAudience.TeamLeadSupervisor, true)]
    [InlineData(ReportAudience.AcceptanceContact, true)]
    public async Task Download_OfACleanItem_StreamsTheContent_AndAudits(ReportAudience audience, bool bound)
    {
        var content = TestFiles.Png();
        _storage.Objects["tenant/key"] = content;
        _store.DownloadInfo = Info(audience, bound, MalwareScanStatus.Clean);
        var context = Technician();

        var result = await DownloadAsync(context);

        Assert.True(result.Succeeded);
        await using var stream = result.Value!.Content;
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        Assert.Equal(content, buffer.ToArray());
        Assert.Equal("photo.png", result.Value.FileName);
        Assert.Equal("image/png", result.Value.MimeType);

        var audit = Assert.Single(_store.Audits);
        Assert.Equal(WorkEvidenceAudit.DownloadedAction, audit.ActionCode);
        Assert.Equal(context.CorrelationId, audit.CorrelationId);
        Assert.DoesNotContain("tenant/key", audit.NewValueJson!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Download_WhenTheAuditSaveFails_DisposesTheStream_AndPropagates()
    {
        _storage.Objects["tenant/key"] = TestFiles.Png();
        _store.DownloadInfo = Info(ReportAudience.Technician, bound: true, MalwareScanStatus.Clean);
        _store.FailSave = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => DownloadAsync());
    }
}
