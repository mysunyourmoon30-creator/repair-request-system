using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RepairRequest.Application.Approvals;
using RepairRequest.Application.Attachments;
using RepairRequest.Application.Common;
using RepairRequest.Application.RepairRequests;
using RepairRequest.Application.Security;
using RepairRequest.Application.Sla;
using RepairRequest.Domain.Auditing;
using RepairRequest.Domain.Files;
using RepairRequest.Domain.MasterData;
using RepairRequest.Domain.RepairRequests;
using RepairRequest.Domain.Security;
using RepairRequest.Domain.Sla;
using RepairRequest.Infrastructure.Persistence;
using RepairRequest.Infrastructure.RepairRequests;
using RepairRequest.IntegrationTests.Authentication;
using RepairRequest.IntegrationTests.Authorization;
using RepairRequest.IntegrationTests.Persistence;
using RepairRequestAggregate = RepairRequest.Domain.RepairRequests.RepairRequest;

namespace RepairRequest.IntegrationTests.Sla;

/// <summary>
/// SLA start and stop against LocalDB through the real lifecycle services and stores (`docs/16` D5-D7; EV-SLA-001/005): the start
/// is part of the first Submit's single transaction, the four stop events stop at most once with the first reason kept, and every
/// failure rolls the SLA change back with the lifecycle change.
/// </summary>
[Collection(PersistenceDatabaseCollection.Name)]
public sealed class SlaLifecycleTests : IAsyncLifetime
{
    private const string ValidHash = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";
    private const string ContinuationReason = "Separate fault used by SLA tests";
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly DateTime Start = new(2026, 9, 20, 8, 0, 0, DateTimeKind.Utc);

    private readonly AuthenticationTestHost _host = new();

    public SlaLifecycleTests(PersistenceDatabaseFixture database)
    {
        _ = database;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private async Task<T> WithDbAsync<T>(Func<RepairRequestDbContext, Task<T>> action)
    {
        await using var scope = _host.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<RepairRequestDbContext>());
    }

    private async Task WithDbAsync(Func<RepairRequestDbContext, Task> action)
    {
        await using var scope = _host.CreateScope();
        await action(scope.ServiceProvider.GetRequiredService<RepairRequestDbContext>());
    }

    private sealed record World(ScopeWorld Scope, CurrentUser Admin, CurrentUser Requester, CurrentUser Approver);

    private async Task<CurrentUser> UserAsync(Guid tenantId, Site[] sites, params string[] roles)
    {
        var user = await _host.CreateUserInTenantAsync(tenantId, roles);
        if (sites.Length > 0)
        {
            await WithDbAsync(db => ScopeWorld.AssignSitesAsync(db, tenantId, user.Id, sites));
        }

        return new CurrentUser(user.Id, tenantId, roles);
    }

    private async Task<World> WorldAsync(bool withRoute = true)
    {
        var scope = await WithDbAsync(ScopeWorld.CreateAsync);
        await using (var seedScope = _host.CreateScope())
        {
            await seedScope.ServiceProvider.GetRequiredService<RequestLookupSeeder>().SeedTenantAsync(scope.TenantId, None);
        }

        var admin = await UserAsync(scope.TenantId, [], RoleCodes.Administrator);
        var requester = await UserAsync(scope.TenantId, [scope.SiteA1], RoleCodes.Requester);
        var approver = await UserAsync(scope.TenantId, [scope.SiteA1], RoleCodes.Approver);
        if (withRoute)
        {
            await using var routeScope = _host.CreateScope();
            var route = await routeScope.ServiceProvider.GetRequiredService<ApprovalRouteService>().CreateAsync(
                new CommandContext(admin, Guid.NewGuid()), new ApprovalRouteFields("ELECTRICAL", scope.SiteA1.Id, RoleCodes.Approver, null), None);
            Assert.True(route.Succeeded);
        }

        return new World(scope, admin, requester, approver);
    }

    private async Task<RepairRequestDraftDto> DraftAsync(CurrentUser owner, Site site)
    {
        await using var scope = _host.CreateScope();
        var created = await scope.ServiceProvider.GetRequiredService<RepairRequestDraftService>().CreateAsync(
            new CommandContext(owner, Guid.NewGuid()),
            new RepairRequestDraftFields(site.Id, null, "ELECTRICAL", "HIGH", owner.UserId, "Pump leaking", Start, Start.AddHours(2)),
            None);
        Assert.True(created.Succeeded);
        return created.Value!;
    }

    private async Task<RepairRequestDraftDto> EvidencedDraftAsync(CurrentUser owner, Site site)
    {
        var draft = await DraftAsync(owner, site);
        await WithDbAsync(async db =>
        {
            var file = new FileAsset(owner.TenantId, "evidence.png", "image/png", 1024, ValidHash, $"{owner.TenantId:N}/{Guid.NewGuid():N}", owner.UserId, DateTime.UtcNow);
            file.MarkClean();
            db.FileAssets.Add(file);
            db.RepairRequestAttachments.Add(new RepairRequestAttachment(draft.Id, file.Id, AttachmentFileRules.AccessScopeCode));
            await db.SaveChangesAsync();
        });
        return draft;
    }

    private async Task<CommandResult<RepairRequestDraftDto>> SubmitCoreAsync(CurrentUser owner, Guid requestId, byte[] rowVersion, Guid? correlationId = null)
    {
        await using var scope = _host.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<RepairRequestSubmitService>()
            .SubmitAsync(new CommandContext(owner, correlationId ?? Guid.NewGuid()), requestId, rowVersion, ContinuationReason, None);
    }

    private async Task<RepairRequestDraftDto> SubmitAsync(CurrentUser owner, Site site, Guid? correlationId = null)
    {
        var draft = await EvidencedDraftAsync(owner, site);
        var result = await SubmitCoreAsync(owner, draft.Id, draft.RowVersion, correlationId);
        Assert.True(result.Succeeded);
        return result.Value!;
    }

    private async Task<CommandResult<RepairRequestDraftDto>> CancelAsync(CurrentUser caller, Guid requestId, byte[] rowVersion, Guid? correlationId = null)
    {
        await using var scope = _host.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<RepairRequestCancelService>()
            .CancelAsync(new CommandContext(caller, correlationId ?? Guid.NewGuid()), requestId, rowVersion, "No longer needed", None);
    }

    private async Task<CommandResult<RepairRequestDraftDto>> DecideAsync(CurrentUser approver, Guid requestId, byte[] rowVersion, string kind, Guid? correlationId = null)
    {
        await using var scope = _host.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<RepairRequestDecisionService>();
        var context = new CommandContext(approver, correlationId ?? Guid.NewGuid());
        return kind switch
        {
            "approve" => await service.ApproveAsync(context, requestId, rowVersion, None),
            "reject" => await service.RejectAsync(context, requestId, rowVersion, "Covered by the service contract", None),
            _ => await service.ReturnForCorrectionAsync(context, requestId, rowVersion, "Photo does not show the fault", None)
        };
    }

    private Task<SlaRecord?> RecordAsync(Guid requestId) =>
        WithDbAsync(db => db.SlaRecords.AsNoTracking().SingleOrDefaultAsync(record => record.RepairRequestId == requestId));

    private Task<List<AuditHistory>> SlaAuditsAsync(Guid requestId, string? action = null) =>
        WithDbAsync(async db =>
        {
            var recordIds = await db.SlaRecords.Where(record => record.RepairRequestId == requestId).Select(record => record.Id).ToListAsync();
            return await db.AuditHistory.AsNoTracking()
                .Where(audit => audit.EntityType == SlaAudit.EntityType && recordIds.Contains(audit.EntityId) && (action == null || audit.ActionCode == action))
                .OrderBy(audit => audit.OccurredAt)
                .ToListAsync();
        });

    private Task<List<AuditHistory>> RequestAuditsAsync(Guid requestId, string action) =>
        WithDbAsync(db => db.AuditHistory.AsNoTracking().Where(audit => audit.EntityId == requestId && audit.ActionCode == action).ToListAsync());

    private Task<RepairRequestAggregate> StoredAsync(Guid requestId) =>
        WithDbAsync(db => db.RepairRequests.AsNoTracking().SingleAsync(request => request.Id == requestId));

    // ---------------- Start ----------------

    [Fact]
    public async Task FirstSubmit_StartsExactlyOneRecord_AtTheSubmittedAt_WithPolicyDueAndRisk_AndOneSystemStartAudit()
    {
        var world = await WorldAsync();
        var correlationId = Guid.NewGuid();

        var submitted = await SubmitAsync(world.Requester, world.Scope.SiteA1, correlationId);

        var stored = await StoredAsync(submitted.Id);
        var record = await RecordAsync(submitted.Id);
        Assert.NotNull(record);
        Assert.Equal(world.Scope.TenantId, record.TenantId);
        Assert.Equal("SLA-004/v1", record.SlaPolicyVersion);
        Assert.Equal(stored.SubmittedAt, record.StartedAt);
        Assert.Equal(record.StartedAt.AddHours(24), record.OriginalDueAt);
        Assert.Equal(record.OriginalDueAt.AddHours(-4), record.RiskAt);
        Assert.Null(record.StoppedAt);
        Assert.Null(record.StopReason);

        var started = Assert.Single(await SlaAuditsAsync(submitted.Id));
        Assert.Equal(SlaAudit.StartedAction, started.ActionCode);
        Assert.Equal(record.Id, started.EntityId);
        Assert.Equal(world.Scope.TenantId, started.TenantId);
        Assert.Equal(SystemActors.SlaTracking, started.ActorId);
        Assert.Equal(correlationId, started.CorrelationId);
        Assert.Equal(record.StartedAt, started.OccurredAt);

        // The initiating user context is preserved by the triggering lifecycle row sharing the correlation id.
        var submit = Assert.Single(await RequestAuditsAsync(submitted.Id, RepairRequestAudit.SubmittedAction));
        Assert.Equal(world.Requester.UserId, submit.ActorId);
        Assert.Equal(correlationId, submit.CorrelationId);

        var values = JsonDocument.Parse(started.NewValueJson!).RootElement;
        Assert.Equal(submitted.Id, values.GetProperty("repairRequestId").GetGuid());
        Assert.Equal("SLA-004/v1", values.GetProperty("policyVersion").GetString());
        Assert.Equal(record.StartedAt, values.GetProperty("startedAt").GetDateTime().ToUniversalTime());
        Assert.Equal(record.OriginalDueAt, values.GetProperty("dueAt").GetDateTime().ToUniversalTime());
        Assert.Equal(record.RiskAt, values.GetProperty("riskAt").GetDateTime().ToUniversalTime());
    }

    [Fact]
    public async Task ADraftThatIsNeverSubmitted_HasNoSlaRecordAndNoSlaAudit()
    {
        var world = await WorldAsync();
        var draft = await DraftAsync(world.Requester, world.Scope.SiteA1);

        Assert.Null(await RecordAsync(draft.Id));
        Assert.Empty(await SlaAuditsAsync(draft.Id));
    }

    [Fact]
    public async Task ReturnThenResubmit_KeepsTheSingleOriginalRecord_AndStartsNothingNew()
    {
        var world = await WorldAsync();
        var submitted = await SubmitAsync(world.Requester, world.Scope.SiteA1);
        var original = (await RecordAsync(submitted.Id))!;
        Assert.Equal(RepairRequestStatus.UnderReview, submitted.Status);

        _host.Clock.Advance(TimeSpan.FromHours(2));
        var returned = await DecideAsync(world.Approver, submitted.Id, submitted.RowVersion, "return");
        Assert.True(returned.Succeeded);
        Assert.Equal(RepairRequestStatus.Draft, returned.Value!.Status);

        // Return neither stops nor restarts: the same active record continues.
        var afterReturn = (await RecordAsync(submitted.Id))!;
        Assert.Equal(original.Id, afterReturn.Id);
        Assert.Null(afterReturn.StoppedAt);

        _host.Clock.Advance(TimeSpan.FromHours(1));
        var resubmitted = await SubmitCoreAsync(world.Requester, submitted.Id, returned.Value.RowVersion);
        Assert.True(resubmitted.Succeeded);

        var record = (await RecordAsync(submitted.Id))!;
        Assert.Equal(original.Id, record.Id);
        Assert.Equal(original.StartedAt, record.StartedAt);
        Assert.Equal(original.OriginalDueAt, record.OriginalDueAt);
        Assert.Equal(original.RiskAt, record.RiskAt);
        Assert.Null(record.StoppedAt);
        Assert.Equal(1, await WithDbAsync(db => db.SlaRecords.CountAsync(item => item.RepairRequestId == submitted.Id)));
        Assert.Equal(SlaAudit.StartedAction, Assert.Single(await SlaAuditsAsync(submitted.Id)).ActionCode);
    }

    [Fact]
    public async Task ApproveDoesNotStopTheSla()
    {
        var world = await WorldAsync();
        var submitted = await SubmitAsync(world.Requester, world.Scope.SiteA1);

        var approved = await DecideAsync(world.Approver, submitted.Id, submitted.RowVersion, "approve");

        Assert.True(approved.Succeeded);
        var record = (await RecordAsync(submitted.Id))!;
        Assert.Null(record.StoppedAt);
        Assert.Null(record.StopReason);
        Assert.Equal(SlaAudit.StartedAction, Assert.Single(await SlaAuditsAsync(submitted.Id)).ActionCode);
    }

    [Fact]
    public async Task ConcurrentSubmitsOfTheSameDraft_ProduceExactlyOneRecordAndOneStartAudit()
    {
        var world = await WorldAsync();
        var draft = await EvidencedDraftAsync(world.Requester, world.Scope.SiteA1);

        var results = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(_ => Task.Run(() => SubmitCoreAsync(world.Requester, draft.Id, draft.RowVersion))));

        Assert.Equal(1, results.Count(result => result.Succeeded));
        Assert.All(results.Where(result => !result.Succeeded), result =>
            Assert.True(result.Error?.Failure is CommandFailure.ConcurrencyConflict or CommandFailure.StateConflict, result.Error?.Failure.ToString()));
        Assert.Equal(1, await WithDbAsync(db => db.SlaRecords.CountAsync(item => item.RepairRequestId == draft.Id)));
        Assert.Equal(SlaAudit.StartedAction, Assert.Single(await SlaAuditsAsync(draft.Id)).ActionCode);
        Assert.Single(await RequestAuditsAsync(draft.Id, RepairRequestAudit.SubmittedAction));
    }

    [Fact]
    public async Task AFailureAfterTheSlaIsStaged_RollsBackTheSlaRecordItsAuditTheTransitionAndTheRequestNumber()
    {
        var world = await WorldAsync();
        var draft = await EvidencedDraftAsync(world.Requester, world.Scope.SiteA1);
        var context = new CommandContext(world.Requester, Guid.NewGuid());
        var at = new DateTime(2026, 3, 1, 8, 0, 0, DateTimeKind.Utc);

        await using (var scope = _host.CreateScope())
        {
            var request = (await scope.ServiceProvider.GetRequiredService<IRepairRequestDraftStore>().FindOwnAsync(world.Requester, draft.Id, None))!;
            var sla = scope.ServiceProvider.GetRequiredService<ISlaRecordStore>();
            var query = new DuplicateQuery(request.TenantId, request.Id, request.SiteId!.Value, request.RequestCategoryCode!, request.EquipmentId, at.AddHours(-24));

            await Assert.ThrowsAsync<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<IRepairRequestSubmitStore>().SubmitAsync(
                request, request.RowVersion, query, 2026, hasContinuationReason: false, (sequence, duplicateCount) =>
                {
                    request.Submit(RequestNumber.Format(2026, sequence!.Value), world.Requester.UserId, at, null);
                    SlaLifecycle.Start(sla, context, request.TenantId, request.Id, request.SubmittedAt!.Value);
                    throw new InvalidOperationException("Simulated failure after the SLA was staged");
                }, None));
        }

        Assert.Null(await RecordAsync(draft.Id));
        Assert.Empty(await SlaAuditsAsync(draft.Id));
        Assert.Equal(RepairRequestStatus.Draft, (await StoredAsync(draft.Id)).Status);
        Assert.Empty(await RequestAuditsAsync(draft.Id, RepairRequestAudit.SubmittedAction));
    }

    [Fact]
    public async Task AStaleSubmit_GetsAConflict_AndLeavesTheWinnersSingleRecordUntouched()
    {
        var world = await WorldAsync();
        var draft = await EvidencedDraftAsync(world.Requester, world.Scope.SiteA1);
        var at = new DateTime(2026, 3, 1, 8, 0, 0, DateTimeKind.Utc);

        async Task<RepairRequestSubmitResult> StoreSubmitAsync(RepairRequestAggregate request, IServiceProvider services, Guid correlation)
        {
            var context = new CommandContext(world.Requester, correlation);
            return await services.GetRequiredService<IRepairRequestSubmitStore>().SubmitAsync(
                request,
                request.RowVersion,
                new DuplicateQuery(request.TenantId, request.Id, request.SiteId!.Value, request.RequestCategoryCode!, request.EquipmentId, at.AddHours(-24)),
                2026,
                hasContinuationReason: false,
                (sequence, duplicateCount) =>
                {
                    request.Submit(RequestNumber.Format(2026, sequence!.Value), world.Requester.UserId, at, null);
                    SlaLifecycle.Start(services.GetRequiredService<ISlaRecordStore>(), context, request.TenantId, request.Id, request.SubmittedAt!.Value);
                    return RepairRequestAudit.Submitted(context, request, duplicateCount, at);
                },
                None);
        }

        // Both load the same Draft version; the second to save is stale.
        await using var firstScope = _host.CreateScope();
        await using var secondScope = _host.CreateScope();
        var first = (await firstScope.ServiceProvider.GetRequiredService<IRepairRequestDraftStore>().FindOwnAsync(world.Requester, draft.Id, None))!;
        var stale = (await secondScope.ServiceProvider.GetRequiredService<IRepairRequestDraftStore>().FindOwnAsync(world.Requester, draft.Id, None))!;
        var winnerCorrelation = Guid.NewGuid();

        var winner = await StoreSubmitAsync(first, firstScope.ServiceProvider, winnerCorrelation);
        var loser = await StoreSubmitAsync(stale, secondScope.ServiceProvider, Guid.NewGuid());

        Assert.Equal(RepairRequestSubmitStatus.Saved, winner.Status);
        Assert.Equal(RepairRequestSubmitStatus.ConcurrencyConflict, loser.Status);
        Assert.Equal(1, await WithDbAsync(db => db.SlaRecords.CountAsync(item => item.RepairRequestId == draft.Id)));
        var audit = Assert.Single(await SlaAuditsAsync(draft.Id));
        Assert.Equal(winnerCorrelation, audit.CorrelationId);
    }

    // ---------------- Stop through the lifecycle services ----------------

    [Fact]
    public async Task Reject_StopsTheRecordWithRequestRejected_AuditsOnce_AndAnotherRejectChangesNothing()
    {
        var world = await WorldAsync();
        var submitted = await SubmitAsync(world.Requester, world.Scope.SiteA1);
        var started = (await RecordAsync(submitted.Id))!;
        _host.Clock.Advance(TimeSpan.FromHours(3));
        var correlationId = Guid.NewGuid();

        var rejected = await DecideAsync(world.Approver, submitted.Id, submitted.RowVersion, "reject", correlationId);

        Assert.True(rejected.Succeeded);
        var record = (await RecordAsync(submitted.Id))!;
        Assert.Equal(started.Id, record.Id);
        Assert.Equal(SlaStopReason.RequestRejected, record.StopReason);
        Assert.Equal(started.StartedAt.AddHours(3), record.StoppedAt);
        Assert.Equal(started.OriginalDueAt, record.OriginalDueAt);
        Assert.Equal(started.RiskAt, record.RiskAt);

        var stopped = Assert.Single(await SlaAuditsAsync(submitted.Id, SlaAudit.StoppedAction));
        Assert.Equal(SystemActors.SlaTracking, stopped.ActorId);
        Assert.Equal(correlationId, stopped.CorrelationId);
        Assert.Equal(record.StoppedAt, stopped.OccurredAt);
        var values = JsonDocument.Parse(stopped.NewValueJson!).RootElement;
        Assert.Equal("REQUEST_REJECTED", values.GetProperty("stoppedReason").GetString());
        Assert.Equal(submitted.Id, values.GetProperty("repairRequestId").GetGuid());
        Assert.Equal(record.StoppedAt, values.GetProperty("stoppedAt").GetDateTime().ToUniversalTime());

        var lifecycle = Assert.Single(await RequestAuditsAsync(submitted.Id, RepairRequestAudit.RejectedAction));
        Assert.Equal(world.Approver.UserId, lifecycle.ActorId);
        Assert.Equal(correlationId, lifecycle.CorrelationId);

        // A repeated Reject is denied by the lifecycle guards and changes nothing.
        _host.Clock.Advance(TimeSpan.FromHours(1));
        var again = await DecideAsync(world.Approver, submitted.Id, rejected.Value!.RowVersion, "reject");
        Assert.Equal(CommandFailure.StateConflict, again.Error?.Failure);
        var unchanged = (await RecordAsync(submitted.Id))!;
        Assert.Equal(record.StoppedAt, unchanged.StoppedAt);
        Assert.Equal(SlaStopReason.RequestRejected, unchanged.StopReason);
        Assert.Single(await SlaAuditsAsync(submitted.Id, SlaAudit.StoppedAction));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancel_StopsTheRecordWithRequestCancelled_FromSubmittedUnderReviewAndApproved(bool approveFirst)
    {
        var world = await WorldAsync();
        var submitted = await SubmitAsync(world.Requester, world.Scope.SiteA1);
        var rowVersion = submitted.RowVersion;
        if (approveFirst)
        {
            var approved = await DecideAsync(world.Approver, submitted.Id, rowVersion, "approve");
            Assert.True(approved.Succeeded);
            rowVersion = approved.Value!.RowVersion;
        }

        _host.Clock.Advance(TimeSpan.FromMinutes(90));
        var correlationId = Guid.NewGuid();
        var cancelled = await CancelAsync(world.Requester, submitted.Id, rowVersion, correlationId);

        Assert.True(cancelled.Succeeded);
        var record = (await RecordAsync(submitted.Id))!;
        Assert.Equal(SlaStopReason.RequestCancelled, record.StopReason);
        Assert.Equal(record.StartedAt.AddMinutes(90), record.StoppedAt);
        var stopped = Assert.Single(await SlaAuditsAsync(submitted.Id, SlaAudit.StoppedAction));
        Assert.Equal(correlationId, stopped.CorrelationId);
        Assert.Equal(SystemActors.SlaTracking, stopped.ActorId);
        Assert.Contains("\"stoppedReason\":\"REQUEST_CANCELLED\"", stopped.NewValueJson);
    }

    [Fact]
    public async Task CancelOfAReturnedDraft_StopsTheStillActiveRecord()
    {
        var world = await WorldAsync();
        var submitted = await SubmitAsync(world.Requester, world.Scope.SiteA1);
        var returned = await DecideAsync(world.Approver, submitted.Id, submitted.RowVersion, "return");
        Assert.Equal(RepairRequestStatus.Draft, returned.Value!.Status);
        Assert.Null((await RecordAsync(submitted.Id))!.StoppedAt);

        var cancelled = await CancelAsync(world.Requester, submitted.Id, returned.Value.RowVersion);

        Assert.True(cancelled.Succeeded);
        Assert.Equal(SlaStopReason.RequestCancelled, (await RecordAsync(submitted.Id))!.StopReason);
        Assert.Single(await SlaAuditsAsync(submitted.Id, SlaAudit.StoppedAction));
    }

    [Fact]
    public async Task CancelOfANeverSubmittedDraft_SucceedsWithoutAnySlaRecordOrAudit()
    {
        var world = await WorldAsync();
        var draft = await DraftAsync(world.Requester, world.Scope.SiteA1);

        var cancelled = await CancelAsync(world.Requester, draft.Id, draft.RowVersion);

        Assert.True(cancelled.Succeeded);
        Assert.Null(await RecordAsync(draft.Id));
        Assert.Empty(await SlaAuditsAsync(draft.Id));
        Assert.Single(await RequestAuditsAsync(draft.Id, RepairRequestAudit.CancelledAction));
    }

    [Fact]
    public async Task ALegacyRequestWithoutARecord_CanStillBeCancelled_AndNothingIsBackfilled()
    {
        var world = await WorldAsync();
        var submitted = await SubmitAsync(world.Requester, world.Scope.SiteA1);

        // Model a request submitted before the SLA foundation: its record never existed.
        await WithDbAsync(db => db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM [sla_record] WHERE [repair_request_id] = {submitted.Id}"));
        Assert.Null(await RecordAsync(submitted.Id));

        var cancelled = await CancelAsync(world.Requester, submitted.Id, submitted.RowVersion);

        Assert.True(cancelled.Succeeded);
        Assert.Equal(RepairRequestStatus.Cancelled, (await StoredAsync(submitted.Id)).Status);
        Assert.Null(await RecordAsync(submitted.Id));
        Assert.Equal(
            0,
            await WithDbAsync(db => db.AuditHistory.CountAsync(audit =>
                audit.TenantId == world.Scope.TenantId && audit.EntityType == SlaAudit.EntityType && audit.ActionCode == SlaAudit.StoppedAction)));
    }

    [Fact]
    public async Task ARejectRacingACancel_HasOneLifecycleWinner_OneStop_AndTheFirstReasonKept()
    {
        var world = await WorldAsync();
        var submitted = await SubmitAsync(world.Requester, world.Scope.SiteA1);

        var results = await Task.WhenAll(
            Task.Run(() => DecideAsync(world.Approver, submitted.Id, submitted.RowVersion, "reject")),
            Task.Run(() => CancelAsync(world.Requester, submitted.Id, submitted.RowVersion)));

        Assert.Equal(1, results.Count(result => result.Succeeded));
        var record = (await RecordAsync(submitted.Id))!;
        var stoppedAudit = Assert.Single(await SlaAuditsAsync(submitted.Id, SlaAudit.StoppedAction));
        var stored = await StoredAsync(submitted.Id);
        var expected = stored.Status == RepairRequestStatus.Rejected ? SlaStopReason.RequestRejected : SlaStopReason.RequestCancelled;
        Assert.Equal(expected, record.StopReason);
        Assert.Contains(SlaStopReasonCodes.ToCode(expected), stoppedAudit.NewValueJson);
    }

    // ---------------- Store-level stop semantics, races and rollback ----------------

    /// <summary>UTC truncated to the datetime2(3) precision, as every lifecycle service does.</summary>
    private static DateTime TruncatedUtcNow()
    {
        var now = DateTime.UtcNow;
        return now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMillisecond));
    }

    private async Task<(Guid TenantId, Guid RequestId)> SeedActiveRecordAsync(DateTime? startedAt = null)
    {
        var world = await WorldAsync(withRoute: false);
        var requestId = await WithDbAsync(db => ScopeWorld.AddRequestAsync(db, world.Scope.TenantId, world.Requester.UserId, world.Scope.SiteA1));
        await WithDbAsync(async db =>
        {
            db.SlaRecords.Add(SlaRecord.Start(world.Scope.TenantId, requestId, startedAt ?? DateTime.UtcNow.AddHours(-1)));
            await db.SaveChangesAsync();
        });
        return (world.Scope.TenantId, requestId);
    }

    /// <summary>One stop attempt in its own scope and transaction: the SLA update, its audit row and the commit.</summary>
    private async Task<(bool Stopped, SlaStopReason Reason)> StopInTransactionAsync(Guid tenantId, Guid requestId, SlaStopReason reason, bool commit = true)
    {
        await using var scope = _host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RepairRequestDbContext>();
        var sla = scope.ServiceProvider.GetRequiredService<ISlaRecordStore>();
        var context = new CommandContext(new CurrentUser(Guid.NewGuid(), tenantId, [RoleCodes.Supervisor]), Guid.NewGuid());

        await using var transaction = await db.Database.BeginTransactionAsync(None);
        var before = db.ChangeTracker.Entries<AuditHistory>().Count();
        await SlaLifecycle.StopAsync(sla, audit => db.AuditHistory.Add(audit), context, tenantId, requestId, reason, TruncatedUtcNow(), None);
        var stopped = db.ChangeTracker.Entries<AuditHistory>().Count() > before;
        await db.SaveChangesAsync(None);

        if (commit)
        {
            await transaction.CommitAsync(None);
        }

        return (stopped, reason);
    }

    [Fact]
    public async Task Stop_FirstCallStops_ASecondCallWithTheSameOrAnotherReasonIsANoOp_AndTheFirstReasonAndTimeAreKept()
    {
        var (tenantId, requestId) = await SeedActiveRecordAsync();

        var first = await StopInTransactionAsync(tenantId, requestId, SlaStopReason.WoClosed);
        var afterFirst = (await RecordAsync(requestId))!;
        var second = await StopInTransactionAsync(tenantId, requestId, SlaStopReason.WoClosed);
        var third = await StopInTransactionAsync(tenantId, requestId, SlaStopReason.RequestCancelled);

        Assert.True(first.Stopped);
        Assert.False(second.Stopped);
        Assert.False(third.Stopped);
        var final = (await RecordAsync(requestId))!;
        Assert.Equal(SlaStopReason.WoClosed, final.StopReason);
        Assert.Equal(afterFirst.StoppedAt, final.StoppedAt);
        Assert.Single(await SlaAuditsAsync(requestId, SlaAudit.StoppedAction));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentStops_ExactlyOneWins_OneAuditRow_AndTheWinnersReasonAndTimeAreKept(bool differentReasons)
    {
        var (tenantId, requestId) = await SeedActiveRecordAsync();
        var reasons = Enum.GetValues<SlaStopReason>();

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(index =>
        {
            var reason = differentReasons ? reasons[index % reasons.Length] : SlaStopReason.RequestRejected;
            return Task.Run(() => StopInTransactionAsync(tenantId, requestId, reason));
        }));

        var winners = outcomes.Where(outcome => outcome.Stopped).ToList();
        var winner = Assert.Single(winners);
        var record = (await RecordAsync(requestId))!;
        Assert.Equal(winner.Reason, record.StopReason);
        Assert.NotNull(record.StoppedAt);

        var audit = Assert.Single(await SlaAuditsAsync(requestId, SlaAudit.StoppedAction));
        var values = JsonDocument.Parse(audit.NewValueJson!).RootElement;
        Assert.Equal(SlaStopReasonCodes.ToCode(winner.Reason), values.GetProperty("stoppedReason").GetString());
        Assert.Equal(record.StoppedAt, values.GetProperty("stoppedAt").GetDateTime().ToUniversalTime());
    }

    [Fact]
    public async Task AStopThatIsRolledBack_LeavesTheRecordActive_AndWritesNoAudit()
    {
        var (tenantId, requestId) = await SeedActiveRecordAsync();

        var attempt = await StopInTransactionAsync(tenantId, requestId, SlaStopReason.RequestRejected, commit: false);

        Assert.True(attempt.Stopped);
        var record = (await RecordAsync(requestId))!;
        Assert.Null(record.StoppedAt);
        Assert.Null(record.StopReason);
        Assert.Empty(await SlaAuditsAsync(requestId, SlaAudit.StoppedAction));

        // The rolled-back stop does not consume the one allowed stop.
        Assert.True((await StopInTransactionAsync(tenantId, requestId, SlaStopReason.WoCancelled)).Stopped);
        Assert.Equal(SlaStopReason.WoCancelled, (await RecordAsync(requestId))!.StopReason);
    }

    [Fact]
    public async Task AFailedLifecycleCommand_RollsBackTheSlaStopAndAuditWithTheRealStoreTransactions()
    {
        var (tenantId, requestId) = await SeedActiveRecordAsync();

        // The real stores dispose their transaction without a commit for any non-success result (a lost RowVersion race) or exception.
        async Task<CommandResult<int>> StopThen(Func<CommandResult<int>> outcome, IServiceProvider services)
        {
            var db = services.GetRequiredService<RepairRequestDbContext>();
            var sla = services.GetRequiredService<ISlaRecordStore>();
            var context = new CommandContext(new CurrentUser(Guid.NewGuid(), tenantId, [RoleCodes.Supervisor]), Guid.NewGuid());
            await SlaLifecycle.StopAsync(sla, audit => db.AuditHistory.Add(audit), context, tenantId, requestId, SlaStopReason.RequestCancelled, TruncatedUtcNow(), None);
            return outcome();
        }

        await using (var scope = _host.CreateScope())
        {
            var conflict = await scope.ServiceProvider.GetRequiredService<IRepairRequestCancelStore>()
                .RunInTransactionAsync(() => StopThen(() => CommandError.ConcurrencyConflict, scope.ServiceProvider), None);
            Assert.Equal(CommandFailure.ConcurrencyConflict, conflict.Error?.Failure);
        }

        Assert.Null((await RecordAsync(requestId))!.StoppedAt);

        await using (var scope = _host.CreateScope())
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<IRepairRequestCancelStore>()
                .RunInTransactionAsync<int>(() => StopThen(() => throw new InvalidOperationException("Simulated failure"), scope.ServiceProvider), None));
        }

        Assert.Null((await RecordAsync(requestId))!.StoppedAt);
        Assert.Empty(await SlaAuditsAsync(requestId, SlaAudit.StoppedAction));

        // And the same store commits the stop when the command succeeds.
        await using (var scope = _host.CreateScope())
        {
            var success = await scope.ServiceProvider.GetRequiredService<IRepairRequestCancelStore>()
                .RunInTransactionAsync(async () =>
                {
                    var result = await StopThen(() => CommandResult<int>.Success(1), scope.ServiceProvider);
                    await scope.ServiceProvider.GetRequiredService<RepairRequestDbContext>().SaveChangesAsync(None);
                    return result;
                }, None);
            Assert.True(success.Succeeded);
        }

        Assert.Equal(SlaStopReason.RequestCancelled, (await RecordAsync(requestId))!.StopReason);
        Assert.Single(await SlaAuditsAsync(requestId, SlaAudit.StoppedAction));
    }

    [Fact]
    public async Task Stop_ForAnotherTenantOrARequestWithoutARecord_MatchesNothing()
    {
        var (tenantId, requestId) = await SeedActiveRecordAsync();
        await using var scope = _host.CreateScope();
        var sla = scope.ServiceProvider.GetRequiredService<ISlaRecordStore>();

        Assert.Null(await sla.StopAsync(Guid.NewGuid(), requestId, SlaStopReason.RequestRejected, DateTime.UtcNow, None));
        Assert.Null(await sla.StopAsync(tenantId, Guid.NewGuid(), SlaStopReason.RequestRejected, DateTime.UtcNow, None));

        Assert.Null((await RecordAsync(requestId))!.StoppedAt);
    }
}
