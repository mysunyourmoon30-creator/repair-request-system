using System.Data.Common;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using RepairRequest.Application.Attachments;
using RepairRequest.Application.Common;
using RepairRequest.Application.Security;
using RepairRequest.Application.WorkEvidence;
using RepairRequest.Application.WorkOrders;
using RepairRequest.Domain.Auditing;
using RepairRequest.Domain.Files;
using RepairRequest.Domain.WorkEvidence;
using RepairRequest.Domain.WorkOrders;
using RepairRequest.Infrastructure.Persistence;
using RepairRequest.IntegrationTests.Authentication;
using RepairRequest.IntegrationTests.Persistence;

namespace RepairRequest.IntegrationTests.WorkEvidence;

/// <summary>
/// Work Order evidence against LocalDB through the real services and stores (`docs/16` D3, OD1..OD3; `docs/13` §4.28): upload, scan results,
/// the CLEAN guard and the immutable binding inside Work Summary Submit, Resubmit rounds, races between upload / scan / Submit / Submit,
/// rollback after binding and blob-versus-database compensation.
/// </summary>
[Collection(PersistenceDatabaseCollection.Name)]
public sealed class WorkEvidenceLifecycleTests : IAsyncLifetime
{
    private static readonly CancellationToken None = CancellationToken.None;

    private readonly InMemoryEvidenceStorage _storage = new();
    private readonly AuthenticationTestHost _host;

    public WorkEvidenceLifecycleTests(PersistenceDatabaseFixture database)
    {
        _ = database;
        _host = NewHost(_storage);
    }

    private static AuthenticationTestHost NewHost(InMemoryEvidenceStorage storage, Action<IServiceCollection>? more = null) =>
        new(services =>
        {
            services.AddSingleton<IFileStorage>(storage);
            services.AddSingleton<IFileMalwareScanner, DeterministicEvidenceScanner>();
            more?.Invoke(services);
        });

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private async Task<T> WithDbAsync<T>(Func<RepairRequestDbContext, Task<T>> action, AuthenticationTestHost? host = null)
    {
        await using var scope = (host ?? _host).CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<RepairRequestDbContext>());
    }

    private async Task<CommandResult<WorkEvidenceItemDto>> UploadAsync(
        EvidenceWorld world, byte[]? content = null, string type = "AFTER_WORK", CurrentUser? user = null, AuthenticationTestHost? host = null)
    {
        await using var scope = (host ?? _host).CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WorkEvidenceService>().UploadAsync(
            EvidenceWorlds.Context(user ?? world.Technician), world.WorkOrderId, type, EvidenceTestFiles.File(content ?? EvidenceTestFiles.Png(Guid.NewGuid().ToString())), None);
    }

    private async Task<WorkEvidenceItemDto> UploadOkAsync(EvidenceWorld world, byte[]? content = null, string type = "AFTER_WORK")
    {
        var result = await UploadAsync(world, content, type);
        Assert.True(result.Succeeded, result.Error?.Message);
        return result.Value!;
    }

    private async Task<FileScanResult> ScanAsync(Guid evidenceId)
    {
        await using var scope = _host.CreateScope();
        var fileAssetId = await scope.ServiceProvider.GetRequiredService<RepairRequestDbContext>().WorkEvidenceUploads
            .Where(item => item.Id == evidenceId).Select(item => item.FileAssetId).SingleAsync();
        return await scope.ServiceProvider.GetRequiredService<FileScanService>().ScanAsync(fileAssetId, None);
    }

    private async Task<WorkEvidenceItemDto> CleanEvidenceAsync(EvidenceWorld world, string type = "AFTER_WORK")
    {
        var item = await UploadOkAsync(world, type: type);
        Assert.Equal(FileScanResult.Clean, await ScanAsync(item.EvidenceId));
        return item;
    }

    private async Task<CommandResult<Guid>> SubmitAsync(
        EvidenceWorld world, IReadOnlyList<Guid>? evidenceIds, CurrentUser? user = null, byte[]? rowVersion = null, AuthenticationTestHost? host = null)
    {
        var h = host ?? _host;
        await using var scope = h.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WorkSummaryService>().SubmitAsync(
            EvidenceWorlds.Context(user ?? world.Technician),
            world.WorkOrderId,
            rowVersion ?? await EvidenceWorlds.RowVersionAsync(h, world.WorkOrderId),
            "Replaced the worn bearing.",
            "REPAIRED",
            evidenceIds,
            None);
    }

    private Task<List<WorkSummaryEvidence>> BoundAsync(Guid workOrderId) =>
        WithDbAsync(db => (
            from binding in db.WorkSummaryEvidence
            join summary in db.WorkSummaries on binding.WorkSummaryId equals summary.Id
            where summary.WorkOrderId == workOrderId
            orderby binding.SlotNo
            select binding).AsNoTracking().ToListAsync());

    private Task<List<WorkSummary>> SummariesAsync(Guid workOrderId) =>
        WithDbAsync(db => db.WorkSummaries.AsNoTracking().Where(summary => summary.WorkOrderId == workOrderId).OrderBy(summary => summary.Id).ToListAsync());

    private Task<WorkOrderStatus> StatusAsync(Guid workOrderId) =>
        WithDbAsync(db => db.WorkOrders.AsNoTracking().Where(item => item.Id == workOrderId).Select(item => item.Status).SingleAsync());

    private Task<MalwareScanStatus> ScanStatusAsync(Guid evidenceId) =>
        WithDbAsync(db => (
            from upload in db.WorkEvidenceUploads
            join file in db.FileAssets on upload.FileAssetId equals file.Id
            where upload.Id == evidenceId
            select file.MalwareScanStatus).SingleAsync());

    private Task SetScanStatusAsync(Guid evidenceId, string status) =>
        WithDbAsync(db => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE [file_asset] SET [malware_scan_status] = {status} WHERE [file_asset_id] = (SELECT [file_asset_id] FROM [work_evidence_upload] WHERE [work_evidence_id] = {evidenceId})"));

    private Task<List<AuditHistory>> EvidenceAuditsAsync(Guid tenantId, string action) =>
        WithDbAsync(db => db.AuditHistory.AsNoTracking()
            .Where(audit => audit.TenantId == tenantId && audit.EntityType == WorkEvidenceAudit.EntityType && audit.ActionCode == action).ToListAsync());

    private static string[] EvidenceMessages(CommandResult<Guid> result) => result.Error!.Errors[WorkEvidenceFields.EvidenceIds];

    // ---------------- upload and scan ----------------

    [Fact]
    public async Task Upload_StoresAPendingFile_AuditsIt_AndAnotherTechnicianOrANonTechnicianCannotUpload()
    {
        var world = await EvidenceWorlds.CreateAsync(_host);

        var item = await UploadOkAsync(world, type: "BEFORE_WORK");

        Assert.Equal(MalwareScanStatus.Pending, item.ScanStatus);
        Assert.Equal(world.VisitId, item.ServiceVisitId);
        Assert.Equal(1, _storage.ObjectCount);
        var audit = Assert.Single(await EvidenceAuditsAsync(world.Scope.TenantId, WorkEvidenceAudit.UploadedAction), row => row.EntityId == item.EvidenceId);
        Assert.Equal(world.Technician.UserId, audit.ActorId);

        foreach (var user in new[] { world.OtherTechnician, world.TeamLead, world.Supervisor, world.AcceptanceContact })
        {
            var denied = await UploadAsync(world, user: user);
            Assert.Equal(CommandFailure.NotFound, denied.Error?.Failure);
        }

        Assert.Equal(1, _storage.ObjectCount);
    }

    [Fact]
    public async Task ScanResults_CleanFailedMalwareUnavailableAndAThrowingScanner_NeverMakeAPendingFileUsable()
    {
        var world = await EvidenceWorlds.CreateAsync(_host);
        var clean = await UploadOkAsync(world);
        var infected = await UploadOkAsync(world, EvidenceTestFiles.Png(DeterministicEvidenceScanner.InfectedMarker));
        var unavailable = await UploadOkAsync(world, EvidenceTestFiles.Png(DeterministicEvidenceScanner.UnavailableMarker));
        var throwing = await UploadOkAsync(world, EvidenceTestFiles.Png(DeterministicEvidenceScanner.ThrowMarker));

        Assert.Equal(FileScanResult.Clean, await ScanAsync(clean.EvidenceId));
        Assert.Equal(FileScanResult.Failed, await ScanAsync(infected.EvidenceId));
        Assert.Equal(FileScanResult.StillPending, await ScanAsync(unavailable.EvidenceId));
        Assert.Equal(FileScanResult.StillPending, await ScanAsync(throwing.EvidenceId));

        Assert.Equal(MalwareScanStatus.Clean, await ScanStatusAsync(clean.EvidenceId));
        Assert.Equal(MalwareScanStatus.Failed, await ScanStatusAsync(infected.EvidenceId));
        Assert.Equal(MalwareScanStatus.Pending, await ScanStatusAsync(unavailable.EvidenceId));
        Assert.Equal(MalwareScanStatus.Pending, await ScanStatusAsync(throwing.EvidenceId));

        // Selecting any of the four resolves to the persisted scan status; only the CLEAN one can ever be used.
        await using var scope = _host.CreateScope();
        var rows = await scope.ServiceProvider.GetRequiredService<IWorkEvidenceStore>().LoadSelectionAsync(
            world.Scope.TenantId,
            world.WorkOrderId,
            world.VisitId,
            [clean.EvidenceId, infected.EvidenceId, unavailable.EvidenceId, throwing.EvidenceId],
            None);
        Assert.Equal(4, rows.Count);
        Assert.Equal([clean.EvidenceId], rows.Where(row => row.ScanStatus == MalwareScanStatus.Clean).Select(row => row.Upload.Id));
        Assert.All(rows, row => Assert.False(row.Bound));

        // The scan result is audited on the file (existing scan audit), not as a Work Order timeline event.
        Assert.Equal(2, await WithDbAsync(db => db.AuditHistory.CountAsync(audit => audit.TenantId == world.Scope.TenantId && audit.EntityType == "FILE_ASSET")));
    }

    [Fact]
    public async Task Upload_HasNoRoundCountOrAttemptLimit_FailedPendingAndUnselectedUploadsNeverBlockUploadingOrSubmit()
    {
        var world = await EvidenceWorlds.CreateAsync(_host);

        // Far beyond the former technical bound of 30 and beyond ten active items: every upload is accepted.
        var items = new List<WorkEvidenceItemDto>();
        for (var i = 0; i < 45; i++)
        {
            items.Add(await UploadOkAsync(world));
        }

        Assert.Equal(45, await WithDbAsync(db => db.WorkEvidenceUploads.CountAsync(item => item.WorkOrderId == world.WorkOrderId)));
        Assert.Equal(45, _storage.ObjectCount);

        // 40 become FAILED (a scanner or storage problem repeated many times), two stay PENDING, three are CLEAN.
        foreach (var failed in items.Take(40))
        {
            await SetScanStatusAsync(failed.EvidenceId, "FAILED");
        }

        foreach (var clean in items.Skip(42))
        {
            Assert.Equal(FileScanResult.Clean, await ScanAsync(clean.EvidenceId));
        }

        // The Visit is not blocked: another upload fits and the three CLEAN items are submitted.
        Assert.True((await UploadAsync(world)).Succeeded);
        var result = await SubmitAsync(world, items.Skip(42).Select(item => item.EvidenceId).ToList());

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Equal(3, (await BoundAsync(world.WorkOrderId)).Count);
    }

    // ---------------- the guard ----------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Submit_WithNoSelectedEvidence_Is422OnEvidenceIds_EvenWhenCleanUploadsExist_AndNothingChanges(bool cleanUploadsExist)
    {
        var world = await EvidenceWorlds.CreateAsync(_host);
        if (cleanUploadsExist)
        {
            await CleanEvidenceAsync(world);
            await CleanEvidenceAsync(world);
        }

        foreach (var ids in new IReadOnlyList<Guid>?[] { null, [] })
        {
            var result = await SubmitAsync(world, ids);

            Assert.Equal(CommandFailure.ValidationFailed, result.Error?.Failure);
            Assert.Equal([WorkEvidenceGuard.MissingMessage], EvidenceMessages(result));
        }

        Assert.Empty(await SummariesAsync(world.WorkOrderId));
        Assert.Empty(await BoundAsync(world.WorkOrderId));
        Assert.Equal(WorkOrderStatus.InProgress, await StatusAsync(world.WorkOrderId));
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("failed")]
    [InlineData("malware")]
    public async Task Submit_WithASelectedPendingFailedOrMalwareItem_Is422_WithTheGenericMessage_AndBindsNothing(string kind)
    {
        var world = await EvidenceWorlds.CreateAsync(_host);
        var clean = await CleanEvidenceAsync(world);
        var content = kind == "malware" ? EvidenceTestFiles.Png(DeterministicEvidenceScanner.InfectedMarker) : EvidenceTestFiles.Png(Guid.NewGuid().ToString());
        var item = await UploadOkAsync(world, content);
        if (kind == "malware")
        {
            Assert.Equal(FileScanResult.Failed, await ScanAsync(item.EvidenceId));
        }
        else if (kind == "failed")
        {
            await SetScanStatusAsync(item.EvidenceId, "FAILED");
        }

        var onlyBad = await SubmitAsync(world, [item.EvidenceId]);
        var mixed = await SubmitAsync(world, [clean.EvidenceId, item.EvidenceId]);

        foreach (var result in new[] { onlyBad, mixed })
        {
            Assert.Equal(CommandFailure.ValidationFailed, result.Error?.Failure);
            Assert.Equal([WorkEvidenceGuard.InvalidSetMessage], EvidenceMessages(result));
        }

        Assert.Empty(await SummariesAsync(world.WorkOrderId));
        Assert.Empty(await BoundAsync(world.WorkOrderId));
        Assert.Equal(WorkOrderStatus.InProgress, await StatusAsync(world.WorkOrderId));
    }

    [Fact]
    public async Task Submit_WithAnIdOfAnotherTenantOrWorkOrder_Is422_WithTheSameGenericMessage_AsAnUnknownId()
    {
        var world = await EvidenceWorlds.CreateAsync(_host);
        var own = await CleanEvidenceAsync(world);

        // Another tenant's Work Order, and another Work Order of the same tenant and Site, each with their own CLEAN evidence.
        var foreignTenant = await EvidenceWorlds.CreateAsync(_host);
        var foreignTenantEvidence = await CleanEvidenceAsync(foreignTenant);
        var (otherWorkOrderId, otherVisitId) = await EvidenceWorlds.AddOtherWorkOrderAsync(_host, world);
        var otherWorkOrderEvidence = await SeedCleanUploadAsync(world.Scope.TenantId, otherWorkOrderId, otherVisitId, world.Technician.UserId);

        var probes = new[] { Guid.NewGuid(), foreignTenantEvidence.EvidenceId, otherWorkOrderEvidence };
        var messages = new List<string[]>();
        foreach (var probe in probes)
        {
            var result = await SubmitAsync(world, [own.EvidenceId, probe]);

            Assert.Equal(CommandFailure.ValidationFailed, result.Error?.Failure);
            Assert.Equal([WorkEvidenceFields.EvidenceIds], result.Error!.Errors.Keys);
            messages.Add(EvidenceMessages(result));
        }

        // Identical for an unknown id, another tenant's id and another Work Order's id: no way to learn where an id lives.
        Assert.All(messages, message => Assert.Equal([WorkEvidenceGuard.InvalidSetMessage], message));
        Assert.Empty(await SummariesAsync(world.WorkOrderId));
        Assert.Empty(await BoundAsync(world.WorkOrderId));
        Assert.Empty(await BoundAsync(foreignTenant.WorkOrderId));
        Assert.Empty(await BoundAsync(otherWorkOrderId));
    }

    /// <summary>A CLEAN evidence upload written directly, for a Work Order/Visit the Technician cannot reach through the upload service.</summary>
    private Task<Guid> SeedCleanUploadAsync(Guid tenantId, Guid workOrderId, Guid visitId, Guid userId) =>
        WithDbAsync(async db =>
        {
            var file = new FileAsset(tenantId, "seeded.png", "image/png", 1024, new string('a', 64), $"{tenantId:N}/{Guid.NewGuid():N}", userId, DateTime.UtcNow);
            file.MarkClean();
            db.FileAssets.Add(file);
            await db.SaveChangesAsync();
            var upload = WorkEvidenceUpload.Create(tenantId, workOrderId, visitId, file.Id, WorkEvidenceType.Other, DateTime.UtcNow);
            db.WorkEvidenceUploads.Add(upload);
            await db.SaveChangesAsync();
            return upload.Id;
        });

    [Theory]
    [InlineData("BEFORE_WORK")]
    [InlineData("AFTER_WORK")]
    [InlineData("OTHER")]
    public async Task Submit_WithOneSelectedCleanItemOfAnyApprovedType_BindsExactlyThatItem_AndFreezesTheRound(string type)
    {
        var world = await EvidenceWorlds.CreateAsync(_host);
        var clean = await CleanEvidenceAsync(world, type);
        var unselectedClean = await CleanEvidenceAsync(world);
        var pending = await UploadOkAsync(world);
        var failed = await UploadOkAsync(world, EvidenceTestFiles.Png(DeterministicEvidenceScanner.InfectedMarker));
        Assert.Equal(FileScanResult.Failed, await ScanAsync(failed.EvidenceId));
        var correlation = Guid.NewGuid();

        var result = await SubmitWithCorrelationAsync(world, correlation, [clean.EvidenceId]);

        Assert.True(result.Succeeded, result.Error?.Message);
        var summary = Assert.Single(await SummariesAsync(world.WorkOrderId));
        var bound = Assert.Single(await BoundAsync(world.WorkOrderId));
        Assert.Equal(clean.EvidenceId, bound.Id);
        Assert.Equal(summary.Id, bound.WorkSummaryId);
        Assert.Equal(summary.WorkOrderId, bound.WorkOrderId);
        Assert.Equal(summary.ServiceVisitId, bound.ServiceVisitId);
        Assert.Equal(1, bound.SlotNo);
        Assert.Equal(type, WorkEvidenceTypeCodes.ToCode(bound.EvidenceType));
        Assert.Equal(WorkOrderStatus.AwaitingSupervisorReview, await StatusAsync(world.WorkOrderId));

        // The unselected CLEAN upload and the PENDING / FAILED candidates were neither counted nor bound.
        Assert.DoesNotContain(
            await BoundAsync(world.WorkOrderId),
            item => item.Id == unselectedClean.EvidenceId || item.Id == pending.EvidenceId || item.Id == failed.EvidenceId);

        var audit = Assert.Single(await EvidenceAuditsAsync(world.Scope.TenantId, WorkEvidenceAudit.BoundAction));
        Assert.Equal(clean.EvidenceId, audit.EntityId);
        Assert.Equal(correlation, audit.CorrelationId);
        Assert.Equal(world.Technician.UserId, audit.ActorId);

        // The round is now closed to uploads.
        Assert.Equal(CommandFailure.StateConflict, (await UploadAsync(world)).Error?.Failure);
    }

    private async Task<CommandResult<Guid>> SubmitWithCorrelationAsync(EvidenceWorld world, Guid correlation, IReadOnlyList<Guid> evidenceIds)
    {
        await using var scope = _host.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WorkSummaryService>().SubmitAsync(
            new CommandContext(world.Technician, correlation),
            world.WorkOrderId,
            await EvidenceWorlds.RowVersionAsync(_host, world.WorkOrderId),
            "Replaced the worn bearing.",
            "REPAIRED",
            evidenceIds,
            None);
    }

    [Fact]
    public async Task Submit_WithTenSelectedCleanItems_BindsAllTen_InRequestOrder_NotUploadOrder()
    {
        var world = await EvidenceWorlds.CreateAsync(_host);
        var items = new List<WorkEvidenceItemDto>();
        for (var i = 0; i < 10; i++)
        {
            items.Add(await CleanEvidenceAsync(world));
        }

        var requested = items.Select(item => item.EvidenceId).Reverse().ToList();
        Assert.True((await SubmitAsync(world, requested)).Succeeded);

        var bound = await BoundAsync(world.WorkOrderId);
        Assert.Equal(Enumerable.Range(1, 10), bound.Select(item => item.SlotNo));
        Assert.Equal(requested, bound.Select(item => item.Id));
    }

    [Fact]
    public async Task Submit_WithElevenOrDuplicateSelectedIds_Is422_AndBindsNothing_WhileTenOfElevenCleanUploadsCanBeSubmitted()
    {
        var world = await EvidenceWorlds.CreateAsync(_host);
        var items = new List<WorkEvidenceItemDto>();
        for (var i = 0; i < 11; i++)
        {
            items.Add(await CleanEvidenceAsync(world)); // eleven CLEAN uploads are allowed: only the bound set is limited to ten
        }

        var eleven = await SubmitAsync(world, items.Select(item => item.EvidenceId).ToList());
        Assert.Equal(CommandFailure.ValidationFailed, eleven.Error?.Failure);
        Assert.Equal([WorkEvidenceGuard.TooManyMessage], EvidenceMessages(eleven));

        var duplicate = await SubmitAsync(world, [items[0].EvidenceId, items[1].EvidenceId, items[0].EvidenceId]);
        Assert.Equal(CommandFailure.ValidationFailed, duplicate.Error?.Failure);
        Assert.Equal([WorkEvidenceGuard.DuplicateMessage], EvidenceMessages(duplicate));

        Assert.Empty(await SummariesAsync(world.WorkOrderId));
        Assert.Empty(await BoundAsync(world.WorkOrderId));

        var ten = await SubmitAsync(world, items.Take(10).Select(item => item.EvidenceId).ToList());
        Assert.True(ten.Succeeded, ten.Error?.Message);
        var bound = await BoundAsync(world.WorkOrderId);
        Assert.Equal(10, bound.Count);
        Assert.DoesNotContain(bound, item => item.Id == items[10].EvidenceId);
    }

    [Fact]
    public async Task Submit_WithAnAlreadyBoundSelectedItem_Is409StateConflict_AndBindsNothingMore()
    {
        var world = await EvidenceWorlds.CreateAsync(_host);
        var first = await CleanEvidenceAsync(world);
        Assert.True((await SubmitAsync(world, [first.EvidenceId])).Succeeded);

        // Reopen the same Visit's round without a new Visit (as a stale client or a stray state change could), then name the bound item again.
        await WithDbAsync(db => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [work_order] SET [status] = 'IN_PROGRESS' WHERE [work_order_id] = {world.WorkOrderId}"));
        var result = await SubmitAsync(world, [first.EvidenceId]);

        Assert.Equal(CommandFailure.StateConflict, result.Error?.Failure);
        Assert.Single(await SummariesAsync(world.WorkOrderId));
        Assert.Single(await BoundAsync(world.WorkOrderId));
    }

    [Fact]
    public async Task ASubmitThatFailsBeforeOrAfterTheGuard_BindsNothing_AndLeavesTheRoundOpenForTheNextTry()
    {
        var world = await EvidenceWorlds.CreateAsync(_host);
        var item = await CleanEvidenceAsync(world);
        List<Guid> ids = [item.EvidenceId];

        // Stale row version (409), not the owner (404) and invalid fields (422) all bind nothing.
        Assert.Equal(CommandFailure.ConcurrencyConflict, (await SubmitAsync(world, ids, rowVersion: [9, 9, 9, 9, 9, 9, 9, 9])).Error?.Failure);
        Assert.Equal(CommandFailure.NotFound, (await SubmitAsync(world, ids, user: world.OtherTechnician)).Error?.Failure);
        await using (var scope = _host.CreateScope())
        {
            var invalid = await scope.ServiceProvider.GetRequiredService<WorkSummaryService>().SubmitAsync(
                EvidenceWorlds.Context(world.Technician), world.WorkOrderId, await EvidenceWorlds.RowVersionAsync(_host, world.WorkOrderId), " ", "NOPE", ids, None);
            Assert.Equal(CommandFailure.ValidationFailed, invalid.Error?.Failure);
        }

        Assert.Empty(await BoundAsync(world.WorkOrderId));
        Assert.Empty(await SummariesAsync(world.WorkOrderId));

        Assert.True((await SubmitAsync(world, ids)).Succeeded);
        Assert.Equal(item.EvidenceId, Assert.Single(await BoundAsync(world.WorkOrderId)).Id);
    }

    // ---------------- immutable rounds ----------------

    [Fact]
    public async Task LaterUploadsAndScans_CannotChangeASubmittedRound_AndTheResubmitRoundBindsOnlyItsOwnSelectedSet()
    {
        var world = await EvidenceWorlds.CreateAsync(_host);
        var first = await CleanEvidenceAsync(world, "BEFORE_WORK");
        var leftoverPending = await UploadOkAsync(world);
        Assert.True((await SubmitAsync(world, [first.EvidenceId])).Succeeded);
        var roundOne = (await BoundAsync(world.WorkOrderId)).Select(item => (item.Id, item.SlotNo, item.BoundAt)).ToList();

        // After the Submit: the leftover PENDING candidate becomes CLEAN, and the round accepts no further upload.
        Assert.Equal(FileScanResult.Clean, await ScanAsync(leftoverPending.EvidenceId));
        Assert.Equal(roundOne, (await BoundAsync(world.WorkOrderId)).Select(item => (item.Id, item.SlotNo, item.BoundAt)).ToList());
        Assert.Equal(CommandFailure.StateConflict, (await UploadAsync(world)).Error?.Failure);

        // Round two: a corrective Visit with its own latest session; the Work Order is IN_PROGRESS again.
        var (secondVisit, _) = await EvidenceWorlds.AddSecondRoundAsync(_host, world);
        var secondRound = await CleanEvidenceAsync(world, "AFTER_WORK");
        var secondUnselected = await CleanEvidenceAsync(world, "OTHER");
        Assert.Equal(secondVisit, secondRound.ServiceVisitId);

        // The earlier round's items — bound or merely uploaded — are another Visit's evidence: the same generic 422, nothing bound.
        foreach (var foreignRound in new[] { first.EvidenceId, leftoverPending.EvidenceId })
        {
            var rejected = await SubmitAsync(world, [secondRound.EvidenceId, foreignRound]);
            Assert.Equal(CommandFailure.ValidationFailed, rejected.Error?.Failure);
            Assert.Equal([WorkEvidenceGuard.InvalidSetMessage], EvidenceMessages(rejected));
        }

        Assert.Single(await SummariesAsync(world.WorkOrderId));

        var resubmit = await SubmitAsync(world, [secondRound.EvidenceId]);

        Assert.True(resubmit.Succeeded, resubmit.Error?.Message);
        var summaries = await SummariesAsync(world.WorkOrderId);
        Assert.Equal(2, summaries.Count);
        var bound = await BoundAsync(world.WorkOrderId);
        Assert.Equal(2, bound.Count);
        var firstBinding = bound.Single(item => item.Id == first.EvidenceId);
        var secondBinding = bound.Single(item => item.Id == secondRound.EvidenceId);
        Assert.NotEqual(firstBinding.WorkSummaryId, secondBinding.WorkSummaryId);
        Assert.Equal(summaries.Single(summary => summary.ServiceVisitId == world.VisitId).Id, firstBinding.WorkSummaryId);
        Assert.Equal(summaries.Single(summary => summary.ServiceVisitId == secondVisit).Id, secondBinding.WorkSummaryId);

        // Neither the first round's leftover nor the second round's unselected upload was bound anywhere.
        Assert.DoesNotContain(bound, item => item.Id == leftoverPending.EvidenceId || item.Id == secondUnselected.EvidenceId);
        Assert.Equal(roundOne[0], (firstBinding.Id, firstBinding.SlotNo, firstBinding.BoundAt));
    }

    // ---------------- races ----------------

    [Fact]
    public async Task ConcurrentSubmits_WithTheSameIds_ProduceOneSummaryAndOneSetOfBindings_NeverDuplicates()
    {
        var world = await EvidenceWorlds.CreateAsync(_host);
        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            ids.Add((await CleanEvidenceAsync(world)).EvidenceId);
        }

        var rowVersion = await EvidenceWorlds.RowVersionAsync(_host, world.WorkOrderId);
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => SubmitAsync(world, ids, rowVersion: rowVersion))));

        Assert.Equal(1, results.Count(result => result.Succeeded));
        // A losing Submit is a genuine stale/concurrent conflict (409), never an evidence validation problem (422).
        Assert.All(results.Where(result => !result.Succeeded), result =>
            Assert.True(result.Error?.Failure is CommandFailure.ConcurrencyConflict or CommandFailure.StateConflict, result.Error?.Failure.ToString()));
        Assert.Single(await SummariesAsync(world.WorkOrderId));
        var bound = await BoundAsync(world.WorkOrderId);
        Assert.Equal(3, bound.Count);
        Assert.Equal(3, bound.Select(item => item.Id).Distinct().Count());
        Assert.Equal(3, (await EvidenceAuditsAsync(world.Scope.TenantId, WorkEvidenceAudit.BoundAction)).Count);
    }

    [Fact]
    public async Task UploadRacingSubmit_NeverBindsTheRacingUpload_AndNeverOpensAClosedRound()
    {
        for (var iteration = 0; iteration < 6; iteration++)
        {
            var world = await EvidenceWorlds.CreateAsync(_host);
            var clean = await CleanEvidenceAsync(world);

            var submit = Task.Run(() => SubmitAsync(world, [clean.EvidenceId]));
            var upload = Task.Run(() => UploadAsync(world));
            await Task.WhenAll(submit, upload);

            Assert.True((await submit).Succeeded, (await submit).Error?.Message);
            var uploadResult = await upload;
            Assert.True(
                uploadResult.Succeeded || uploadResult.Error?.Failure is CommandFailure.StateConflict or CommandFailure.ConcurrencyConflict,
                uploadResult.Error?.Failure.ToString());

            // The racing upload was never selected, so it can never be in the frozen set.
            var bound = Assert.Single(await BoundAsync(world.WorkOrderId));
            Assert.Equal(clean.EvidenceId, bound.Id);

            // And once the Submit is committed, the round accepts no further upload.
            Assert.Equal(CommandFailure.StateConflict, (await UploadAsync(world)).Error?.Failure);
        }
    }

    [Fact]
    public async Task ScanRacingSubmit_BindsASelectedFileOnlyIfItWasCleanAtTheTimeOfTheGuard_ElseBindsNothing()
    {
        for (var iteration = 0; iteration < 6; iteration++)
        {
            var world = await EvidenceWorlds.CreateAsync(_host);
            var clean = await CleanEvidenceAsync(world);
            var racing = await UploadOkAsync(world);

            var submit = Task.Run(() => SubmitAsync(world, [clean.EvidenceId, racing.EvidenceId]));
            var scan = Task.Run(() => ScanAsync(racing.EvidenceId));
            await Task.WhenAll(submit, scan);

            var result = await submit;
            var bound = await BoundAsync(world.WorkOrderId);
            if (result.Succeeded)
            {
                // The scan finished before the guard read the file: both items are CLEAN and frozen in request order.
                Assert.Equal([clean.EvidenceId, racing.EvidenceId], bound.Select(item => item.Id));
                foreach (var item in bound)
                {
                    Assert.Equal(MalwareScanStatus.Clean, await ScanStatusAsync(item.Id));
                }
            }
            else
            {
                // The file was still PENDING when the guard read it: the whole Submit is refused and nothing is bound.
                Assert.Equal(CommandFailure.ValidationFailed, result.Error?.Failure);
                Assert.Equal([WorkEvidenceGuard.InvalidSetMessage], EvidenceMessages(result));
                Assert.Empty(bound);
                Assert.Empty(await SummariesAsync(world.WorkOrderId));
            }
        }
    }

    // ---------------- rollback and compensation ----------------

    [Fact]
    public async Task ADatabaseFailureAfterTheBindingIsStaged_RollsBackTheSummaryTheBindingsTheAuditAndTheStatus()
    {
        var world = await EvidenceWorlds.CreateAsync(_host);
        List<Guid> ids = [(await CleanEvidenceAsync(world)).EvidenceId, (await CleanEvidenceAsync(world)).EvidenceId];

        await using var failingHost = NewHost(_storage, services =>
            services.ConfigureDbContext<RepairRequestDbContext>(options => options.AddInterceptors(new FailAuditInsertInterceptor())));
        await using (var scope = failingHost.CreateScope())
        {
            var thrown = await Assert.ThrowsAsync<DbUpdateException>(async () => await scope.ServiceProvider.GetRequiredService<WorkSummaryService>().SubmitAsync(
                EvidenceWorlds.Context(world.Technician),
                world.WorkOrderId,
                await EvidenceWorlds.RowVersionAsync(_host, world.WorkOrderId),
                "Replaced the worn bearing.",
                "REPAIRED",
                ids,
                None));
            Assert.Equal(50000, Assert.IsType<SqlException>(thrown.InnerException).Number);
        }

        Assert.Empty(await SummariesAsync(world.WorkOrderId));
        Assert.Empty(await BoundAsync(world.WorkOrderId));
        Assert.Equal(WorkOrderStatus.InProgress, await StatusAsync(world.WorkOrderId));
        Assert.Empty(await EvidenceAuditsAsync(world.Scope.TenantId, WorkEvidenceAudit.BoundAction));

        // The lock was released with the transaction: the same Submit now succeeds and binds both items.
        Assert.True((await SubmitAsync(world, ids)).Succeeded);
        Assert.Equal(2, (await BoundAsync(world.WorkOrderId)).Count);
    }

    [Fact]
    public async Task Upload_WhenTheStorageFails_LeavesNoMetadata()
    {
        var world = await EvidenceWorlds.CreateAsync(_host);
        _storage.FailSave = true;

        await Assert.ThrowsAsync<IOException>(() => UploadAsync(world));
        _storage.FailSave = false;

        Assert.Equal(0, await WithDbAsync(db => db.WorkEvidenceUploads.CountAsync(item => item.WorkOrderId == world.WorkOrderId)));
        Assert.Equal(0, _storage.ObjectCount);
        Assert.True((await UploadAsync(world)).Succeeded);
    }

    [Fact]
    public async Task Upload_WhenTheDatabaseFailsAfterTheBinaryIsStored_DeletesTheBinary_AndLeavesNoMetadata()
    {
        var world = await EvidenceWorlds.CreateAsync(_host);
        await using var failingHost = NewHost(_storage, services =>
            services.ConfigureDbContext<RepairRequestDbContext>(options => options.AddInterceptors(new FailAuditInsertInterceptor())));

        await Assert.ThrowsAsync<DbUpdateException>(() => UploadAsync(world, host: failingHost));

        Assert.Equal(0, _storage.ObjectCount);
        Assert.Single(_storage.Deleted);
        Assert.Equal(0, await WithDbAsync(db => db.WorkEvidenceUploads.CountAsync(item => item.WorkOrderId == world.WorkOrderId)));
        Assert.Equal(0, await WithDbAsync(db => db.FileAssets.CountAsync(file => file.TenantId == world.Scope.TenantId)));
    }

    [Fact]
    public async Task Upload_WhenTheEvidenceLockIsHeldElsewhere_IsAConcurrencyConflict_AndTheBinaryIsDeleted()
    {
        var world = await EvidenceWorlds.CreateAsync(_host);
        var item = await CleanEvidenceAsync(world);
        var storedBefore = _storage.ObjectCount;

        // Another transaction holds the Work Order's evidence lock (as a Submit in progress would).
        await using var holder = _host.CreateScope();
        var holderDb = holder.ServiceProvider.GetRequiredService<RepairRequestDbContext>();
        await using var holderTransaction = await holderDb.Database.BeginTransactionAsync(None);
        Assert.True(await holder.ServiceProvider.GetRequiredService<IWorkEvidenceStore>().TryLockWorkOrderAsync(world.WorkOrderId, None));

        var started = DateTime.UtcNow;
        var result = await UploadAsync(world);

        Assert.Equal(CommandFailure.ConcurrencyConflict, result.Error?.Failure);
        Assert.InRange(DateTime.UtcNow - started, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(20));
        Assert.Equal(storedBefore, _storage.ObjectCount);
        Assert.Equal(1, await WithDbAsync(db => db.WorkEvidenceUploads.CountAsync(row => row.WorkOrderId == world.WorkOrderId)));

        // A Submit of the same Work Order is refused the same way while the lock is held, and writes nothing.
        var submit = await SubmitAsync(world, [item.EvidenceId]);
        Assert.Equal(CommandFailure.ConcurrencyConflict, submit.Error?.Failure);
        Assert.Empty(await SummariesAsync(world.WorkOrderId));

        await holderTransaction.RollbackAsync(None);
        Assert.True((await UploadAsync(world)).Succeeded);
        Assert.True((await SubmitAsync(world, [item.EvidenceId])).Succeeded);
    }

    /// <summary>Turns the first audit insert of a save batch into a genuine SQL Server error.</summary>
    private sealed class FailAuditInsertInterceptor : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Fail(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Fail(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        private static void Fail(DbCommand command)
        {
            if (command.CommandText.Contains("INSERT INTO [audit_history]", StringComparison.Ordinal))
            {
                command.CommandText = "RAISERROR(N'Simulated database failure', 16, 1);";
            }
        }
    }
}
