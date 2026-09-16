using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using RepairRequest.Application.Approvals;
using RepairRequest.Application.Attachments;
using RepairRequest.Application.Common;
using RepairRequest.Application.RepairRequests;
using RepairRequest.Application.Security;
using RepairRequest.Domain.Approvals;
using RepairRequest.Domain.Auditing;
using RepairRequest.Domain.Files;
using RepairRequest.Domain.MasterData;
using RepairRequest.Domain.RepairRequests;
using RepairRequest.Domain.Security;
using RepairRequest.Infrastructure.Approvals;
using RepairRequest.Infrastructure.Persistence;
using RepairRequest.Infrastructure.RepairRequests;
using RepairRequest.IntegrationTests.Authentication;
using RepairRequest.IntegrationTests.Authorization;
using RepairRequest.IntegrationTests.Persistence;
using RepairRequestAggregate = RepairRequest.Domain.RepairRequests.RepairRequest;

namespace RepairRequest.IntegrationTests.RepairRequests;

/// <summary>
/// S1-009 Cancel against LocalDB: every allowed source state, approval rows left unchanged (DEC-PRE-S1-009-01), terminal
/// effects on routing and decisions, exactly one winner against Approve / Reject / Cancel / Admin Retry, full rollback,
/// and owner + scope enforcement (ST-RR-007; UC-RR-004).
/// </summary>
[Collection(PersistenceDatabaseCollection.Name)]
public sealed class RepairRequestCancelTests : IAsyncLifetime
{
    private const string ValidHash = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";
    private const string ContinuationReason = "Separate fault used by cancel tests";
    private const string Reason = "No longer needed";
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly DateTime Start = new(2026, 9, 20, 8, 0, 0, DateTimeKind.Utc);

    private readonly AuthenticationTestHost _host = new();

    public RepairRequestCancelTests(PersistenceDatabaseFixture database)
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

    private async Task<World> WorldAsync(bool withRoute)
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
            await CreateRouteAsync(admin, scope.SiteA1);
        }

        return new World(scope, admin, requester, approver);
    }

    private async Task CreateRouteAsync(CurrentUser admin, Site site)
    {
        await using var routeScope = _host.CreateScope();
        var route = await routeScope.ServiceProvider.GetRequiredService<ApprovalRouteService>().CreateAsync(
            new CommandContext(admin, Guid.NewGuid()), new ApprovalRouteFields("ELECTRICAL", site.Id, RoleCodes.Approver, null), None);
        Assert.True(route.Succeeded);
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

    private async Task<RepairRequestDraftDto> SubmitAsync(CurrentUser owner, Site site)
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

        await using var scope = _host.CreateScope();
        var rowVersion = (await scope.ServiceProvider.GetRequiredService<IRepairRequestDraftStore>().GetAsync(owner, draft.Id, None))!.RowVersion;
        var result = await scope.ServiceProvider.GetRequiredService<RepairRequestSubmitService>()
            .SubmitAsync(new CommandContext(owner, Guid.NewGuid()), draft.Id, rowVersion, ContinuationReason, None);
        Assert.True(result.Succeeded);
        return result.Value!;
    }

    private async Task<CommandResult<RepairRequestDraftDto>> CancelAsync(CurrentUser caller, Guid requestId, byte[] rowVersion, string reason = Reason, AuthenticationTestHost? host = null)
    {
        await using var scope = (host ?? _host).CreateScope();
        return await scope.ServiceProvider.GetRequiredService<RepairRequestCancelService>()
            .CancelAsync(new CommandContext(caller, Guid.NewGuid()), requestId, rowVersion, reason, None);
    }

    private async Task<CommandResult<RepairRequestDraftDto>> DecideAsync(CurrentUser approver, Guid requestId, byte[] rowVersion, bool approve)
    {
        await using var scope = _host.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<RepairRequestDecisionService>();
        var context = new CommandContext(approver, Guid.NewGuid());
        return approve
            ? await service.ApproveAsync(context, requestId, rowVersion, None)
            : await service.RejectAsync(context, requestId, rowVersion, "Covered by the service contract", None);
    }

    private async Task<CommandResult<RoutingResultDto>> RetryAsync(CurrentUser admin, Guid requestId, byte[] rowVersion)
    {
        await using var scope = _host.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<RepairRequestRoutingService>()
            .RetryAsync(new CommandContext(admin, Guid.NewGuid()), requestId, rowVersion, None);
    }

    private Task<RepairRequestAggregate> StoredAsync(Guid requestId) =>
        WithDbAsync(db => db.RepairRequests.AsNoTracking().SingleAsync(request => request.Id == requestId));

    private Task<RepairRequestApproval?> StepAsync(Guid requestId) =>
        WithDbAsync(db => db.RepairRequestApprovals.AsNoTracking().SingleOrDefaultAsync(approval => approval.RepairRequestId == requestId));

    private Task<List<AuditHistory>> AuditsAsync(Guid requestId, params string[] actions) =>
        WithDbAsync(db => db.AuditHistory.AsNoTracking()
            .Where(audit => audit.EntityId == requestId && actions.Contains(audit.ActionCode))
            .ToListAsync());

    // ---------------- Allowed source states ----------------

    [Fact]
    public async Task Cancel_Draft_PersistsCancelled_TheReason_AndOneAuditFromDraft()
    {
        var world = await WorldAsync(withRoute: false);
        var draft = await DraftAsync(world.Requester, world.Scope.SiteA1);

        _host.Logs.Clear();
        var result = await CancelAsync(world.Requester, draft.Id, draft.RowVersion, reason: "  No longer needed ");
        var commands = _host.Logs.ExecutedDbCommandCount;

        Assert.True(result.Succeeded);
        Assert.Equal(RepairRequestStatus.Cancelled, result.Value!.Status);
        Assert.NotEqual(draft.RowVersion, result.Value.RowVersion);

        var stored = await StoredAsync(draft.Id);
        Assert.Equal(RepairRequestStatus.Cancelled, stored.Status);
        Assert.Equal(Reason, stored.CancelReason);
        Assert.Null(stored.RequestNo);
        Assert.Equal(result.Value.RowVersion, stored.RowVersion);

        var audit = Assert.Single(await AuditsAsync(draft.Id, RepairRequestAudit.CancelledAction));
        Assert.Equal("DRAFT", audit.FromState);
        Assert.Equal("CANCELLED", audit.ToState);
        Assert.Equal(Reason, audit.Reason);
        Assert.Equal(world.Requester.UserId, audit.ActorId);

        // Owner EXISTS, lock, load, and the save (update + audit insert): bounded.
        Assert.InRange(commands, 1, 6);
    }

    [Fact]
    public async Task Cancel_SubmittedWithARoutingFailure_KeepsRoutingEvidence_LeavesTheIssueList_AndRetryIsStateConflict()
    {
        var world = await WorldAsync(withRoute: false);
        var submitted = await SubmitAsync(world.Requester, world.Scope.SiteA1);
        Assert.Equal(RepairRequestStatus.Submitted, submitted.Status);

        var result = await CancelAsync(world.Requester, submitted.Id, submitted.RowVersion);

        Assert.True(result.Succeeded);
        var stored = await StoredAsync(submitted.Id);
        Assert.Equal(RepairRequestStatus.Cancelled, stored.Status);
        Assert.Equal(submitted.RequestNo, stored.RequestNo);
        Assert.Equal(submitted.SubmittedAt, stored.SubmittedAt);
        Assert.Single(await AuditsAsync(submitted.Id, RepairRequestRoutingAudit.RoutingFailedAction));
        Assert.Equal("SUBMITTED", Assert.Single(await AuditsAsync(submitted.Id, RepairRequestAudit.CancelledAction)).FromState);

        await using (var scope = _host.CreateScope())
        {
            var issues = await scope.ServiceProvider.GetRequiredService<RepairRequestRoutingService>().ListIssuesAsync(world.Admin, new PageRequest(1, 50), None);
            Assert.DoesNotContain(issues.Items, item => item.RepairRequestId == submitted.Id);
        }

        Assert.Equal(CommandFailure.StateConflict, (await RetryAsync(world.Admin, submitted.Id, result.Value!.RowVersion)).Error?.Failure);
        Assert.Null(await StepAsync(submitted.Id));
    }

    [Fact]
    public async Task Cancel_UnderReview_LeavesThePendingApprovalRowUnchanged_AndApproveIsStateConflict()
    {
        var world = await WorldAsync(withRoute: true);
        var submitted = await SubmitAsync(world.Requester, world.Scope.SiteA1);
        Assert.Equal(RepairRequestStatus.UnderReview, submitted.Status);
        var before = (await StepAsync(submitted.Id))!;

        var result = await CancelAsync(world.Requester, submitted.Id, submitted.RowVersion);

        Assert.True(result.Succeeded);
        var after = (await StepAsync(submitted.Id))!;
        Assert.Equal(ApprovalStatus.Pending, after.Status);
        Assert.Equal(before.AssignedApproverId, after.AssignedApproverId);
        Assert.Equal(before.RowVersion, after.RowVersion);
        Assert.Null(after.DecidedAt);
        Assert.Equal("UNDER_REVIEW", Assert.Single(await AuditsAsync(submitted.Id, RepairRequestAudit.CancelledAction)).FromState);

        Assert.Equal(CommandFailure.StateConflict, (await DecideAsync(world.Approver, submitted.Id, result.Value!.RowVersion, approve: true)).Error?.Failure);
        Assert.Equal(CommandFailure.StateConflict, (await DecideAsync(world.Approver, submitted.Id, result.Value.RowVersion, approve: false)).Error?.Failure);
        Assert.Equal(before.RowVersion, (await StepAsync(submitted.Id))!.RowVersion);
    }

    [Fact]
    public async Task Cancel_Approved_LeavesTheDecidedApprovalRowUnchanged()
    {
        var world = await WorldAsync(withRoute: true);
        var submitted = await SubmitAsync(world.Requester, world.Scope.SiteA1);
        var approved = await DecideAsync(world.Approver, submitted.Id, submitted.RowVersion, approve: true);
        Assert.True(approved.Succeeded);
        var before = (await StepAsync(submitted.Id))!;

        var result = await CancelAsync(world.Requester, submitted.Id, approved.Value!.RowVersion);

        Assert.True(result.Succeeded);
        Assert.Equal(RepairRequestStatus.Cancelled, (await StoredAsync(submitted.Id)).Status);
        var after = (await StepAsync(submitted.Id))!;
        Assert.Equal(ApprovalStatus.Approved, after.Status);
        Assert.Equal(before.DecidedAt, after.DecidedAt);
        Assert.Equal(before.RowVersion, after.RowVersion);
        Assert.Equal("APPROVED", Assert.Single(await AuditsAsync(submitted.Id, RepairRequestAudit.CancelledAction)).FromState);
    }

    // ---------------- Forbidden source states ----------------

    [Fact]
    public async Task RejectedCancelledAndConvertedRequests_AreStateConflict_AndUnchanged()
    {
        var world = await WorldAsync(withRoute: true);
        var rejected = await SubmitAsync(world.Requester, world.Scope.SiteA1);
        var rejection = await DecideAsync(world.Approver, rejected.Id, rejected.RowVersion, approve: false);
        Assert.True(rejection.Succeeded);

        var rejectedStep = (await StepAsync(rejected.Id))!;

        var cancelled = await SubmitAsync(world.Requester, world.Scope.SiteA1);
        var first = await CancelAsync(world.Requester, cancelled.Id, cancelled.RowVersion);
        Assert.True(first.Succeeded);

        var converted = await DraftAsync(world.Requester, world.Scope.SiteA1);
        var convertedId = converted.Id;
        await WithDbAsync(db => db.Database.ExecuteSqlAsync($"UPDATE [repair_request] SET [status] = 'CONVERTED' WHERE [repair_request_id] = {convertedId}"));
        var convertedVersion = (await StoredAsync(convertedId)).RowVersion;

        Assert.Equal(CommandFailure.StateConflict, (await CancelAsync(world.Requester, rejected.Id, rejection.Value!.RowVersion)).Error?.Failure);
        Assert.Equal(CommandFailure.StateConflict, (await CancelAsync(world.Requester, cancelled.Id, first.Value!.RowVersion, reason: "Again")).Error?.Failure);
        Assert.Equal(CommandFailure.StateConflict, (await CancelAsync(world.Requester, convertedId, convertedVersion)).Error?.Failure);

        Assert.Equal(RepairRequestStatus.Rejected, (await StoredAsync(rejected.Id)).Status);
        Assert.Null((await StoredAsync(rejected.Id)).CancelReason);
        var rejectedStepAfter = (await StepAsync(rejected.Id))!;
        Assert.Equal(ApprovalStatus.Rejected, rejectedStepAfter.Status);
        Assert.Equal(rejectedStep.DecidedAt, rejectedStepAfter.DecidedAt);
        Assert.Equal(rejectedStep.RowVersion, rejectedStepAfter.RowVersion);
        Assert.Equal(Reason, (await StoredAsync(cancelled.Id)).CancelReason);
        Assert.Equal(RepairRequestStatus.Converted, (await StoredAsync(convertedId)).Status);
        Assert.Empty(await AuditsAsync(rejected.Id, RepairRequestAudit.CancelledAction));
        Assert.Single(await AuditsAsync(cancelled.Id, RepairRequestAudit.CancelledAction));
        Assert.Empty(await AuditsAsync(convertedId, RepairRequestAudit.CancelledAction));
    }

    // ---------------- Ownership and scope ----------------

    [Fact]
    public async Task AnotherRequester_AnOwnerWhoLostSiteScope_AndAnotherTenant_AreNotFound()
    {
        var world = await WorldAsync(withRoute: false);
        var submitted = await SubmitAsync(world.Requester, world.Scope.SiteA1);
        var otherRequester = await UserAsync(world.Scope.TenantId, [world.Scope.SiteA1], RoleCodes.Requester);
        var multiRole = await UserAsync(world.Scope.TenantId, [world.Scope.SiteA1], RoleCodes.Requester, RoleCodes.Supervisor);
        var foreign = await UserAsync(world.Scope.OtherTenantId, [world.Scope.OtherTenantSite], RoleCodes.Requester);

        Assert.Equal(CommandFailure.NotFound, (await CancelAsync(otherRequester, submitted.Id, submitted.RowVersion)).Error?.Failure);
        Assert.Equal(CommandFailure.NotFound, (await CancelAsync(multiRole, submitted.Id, submitted.RowVersion)).Error?.Failure);
        Assert.Equal(CommandFailure.NotFound, (await CancelAsync(foreign, submitted.Id, submitted.RowVersion)).Error?.Failure);

        var ownerId = world.Requester.UserId;
        await WithDbAsync(db => db.Database.ExecuteSqlAsync($"DELETE FROM [user_site_scope] WHERE [user_id] = {ownerId}"));
        Assert.Equal(CommandFailure.NotFound, (await CancelAsync(world.Requester, submitted.Id, submitted.RowVersion)).Error?.Failure);

        Assert.Equal(RepairRequestStatus.Submitted, (await StoredAsync(submitted.Id)).Status);
        Assert.Empty(await AuditsAsync(submitted.Id, RepairRequestAudit.CancelledAction));
    }

    // ---------------- Concurrency ----------------

    [Theory]
    [InlineData("approve")]
    [InlineData("reject")]
    [InlineData("cancel")]
    public async Task CancelRacingAnotherCommandOnUnderReview_ExactlyOneWins(string competitor)
    {
        var world = await WorldAsync(withRoute: true);
        var submitted = await SubmitAsync(world.Requester, world.Scope.SiteA1);
        Assert.Equal(RepairRequestStatus.UnderReview, submitted.Status);

        Task<CommandResult<RepairRequestDraftDto>> Competitor() => competitor switch
        {
            "approve" => DecideAsync(world.Approver, submitted.Id, submitted.RowVersion, approve: true),
            "reject" => DecideAsync(world.Approver, submitted.Id, submitted.RowVersion, approve: false),
            _ => CancelAsync(world.Requester, submitted.Id, submitted.RowVersion, reason: "Duplicate click")
        };

        var results = await Task.WhenAll(
            Task.Run(() => CancelAsync(world.Requester, submitted.Id, submitted.RowVersion)),
            Task.Run(Competitor));

        var winner = Assert.Single(results, result => result.Succeeded);
        Assert.Equal(CommandFailure.ConcurrencyConflict, Assert.Single(results, result => !result.Succeeded).Error!.Failure);

        var stored = await StoredAsync(submitted.Id);
        Assert.Equal(winner.Value!.Status, stored.Status);
        var transitions = await AuditsAsync(submitted.Id, RepairRequestAudit.CancelledAction, RepairRequestAudit.ApprovedAction, RepairRequestAudit.RejectedAction);
        Assert.Single(transitions);
        var step = (await StepAsync(submitted.Id))!;
        Assert.Equal(
            stored.Status switch
            {
                RepairRequestStatus.Approved => ApprovalStatus.Approved,
                RepairRequestStatus.Rejected => ApprovalStatus.Rejected,
                _ => ApprovalStatus.Pending
            },
            step.Status);
    }

    [Fact]
    public async Task CancelRacingAdminRetryRouting_ExactlyOneWins()
    {
        var world = await WorldAsync(withRoute: false);
        var submitted = await SubmitAsync(world.Requester, world.Scope.SiteA1);
        Assert.Equal(RepairRequestStatus.Submitted, submitted.Status);
        await CreateRouteAsync(world.Admin, world.Scope.SiteA1);

        var cancelTask = Task.Run(() => CancelAsync(world.Requester, submitted.Id, submitted.RowVersion));
        var retryTask = Task.Run(() => RetryAsync(world.Admin, submitted.Id, submitted.RowVersion));
        await Task.WhenAll(cancelTask, retryTask);
        var cancel = await cancelTask;
        var retry = await retryTask;

        Assert.NotEqual(cancel.Succeeded, retry.Succeeded);
        var stored = await StoredAsync(submitted.Id);
        if (cancel.Succeeded)
        {
            Assert.Equal(RepairRequestStatus.Cancelled, stored.Status);
            Assert.Contains(retry.Error!.Failure, new[] { CommandFailure.ConcurrencyConflict, CommandFailure.StateConflict });
            Assert.Null(await StepAsync(submitted.Id));
            Assert.Empty(await AuditsAsync(submitted.Id, RepairRequestRoutingAudit.RoutedAction));
        }
        else
        {
            Assert.Equal(RepairRequestStatus.UnderReview, stored.Status);
            Assert.Equal(CommandFailure.ConcurrencyConflict, cancel.Error!.Failure);
            Assert.Empty(await AuditsAsync(submitted.Id, RepairRequestAudit.CancelledAction));
        }
    }

    [Fact]
    public async Task CancelCommittedFirst_ThenPostSubmitRouting_IsConflict_AndWritesNoAssignment()
    {
        var world = await WorldAsync(withRoute: false);
        var submitted = await SubmitAsync(world.Requester, world.Scope.SiteA1);
        Assert.Equal(RepairRequestStatus.Submitted, submitted.Status);
        await CreateRouteAsync(world.Admin, world.Scope.SiteA1);
        var stepBefore = await StepAsync(submitted.Id);

        Assert.True((await CancelAsync(world.Requester, submitted.Id, submitted.RowVersion)).Succeeded);

        await using (var scope = _host.CreateScope())
        {
            var routing = await scope.ServiceProvider.GetRequiredService<RepairRequestRoutingService>()
                .RouteSubmittedAsync(new CommandContext(world.Requester, Guid.NewGuid()), submitted.Id, submitted.RowVersion, None);
            Assert.Equal(CommandFailure.ConcurrencyConflict, routing.Error?.Failure);
        }

        Assert.Equal(RepairRequestStatus.Cancelled, (await StoredAsync(submitted.Id)).Status);
        Assert.Equal(stepBefore?.RowVersion, (await StepAsync(submitted.Id))?.RowVersion);
        Assert.Empty(await AuditsAsync(submitted.Id, RepairRequestRoutingAudit.RoutedAction));
        Assert.Single(await AuditsAsync(submitted.Id, RepairRequestAudit.CancelledAction));
    }

    [Fact]
    public async Task RoutingSuccessCommittedFirst_ThenCancelWithTheSubmittedETag_IsConflict()
    {
        var world = await WorldAsync(withRoute: false);
        var submitted = await SubmitAsync(world.Requester, world.Scope.SiteA1);
        await CreateRouteAsync(world.Admin, world.Scope.SiteA1);

        await using (var scope = _host.CreateScope())
        {
            var routing = await scope.ServiceProvider.GetRequiredService<RepairRequestRoutingService>()
                .RouteSubmittedAsync(new CommandContext(world.Requester, Guid.NewGuid()), submitted.Id, submitted.RowVersion, None);
            Assert.True(routing.Succeeded);
            Assert.Equal(RepairRequestStatus.UnderReview, routing.Value!.Status);
        }

        var cancel = await CancelAsync(world.Requester, submitted.Id, submitted.RowVersion);

        Assert.Equal(CommandFailure.ConcurrencyConflict, cancel.Error?.Failure);
        var stored = await StoredAsync(submitted.Id);
        Assert.Equal(RepairRequestStatus.UnderReview, stored.Status);
        Assert.Null(stored.CancelReason);
        var step = (await StepAsync(submitted.Id))!;
        Assert.Equal(ApprovalStatus.Pending, step.Status);
        Assert.Equal(world.Approver.UserId, step.AssignedApproverId);
        Assert.Empty(await AuditsAsync(submitted.Id, RepairRequestAudit.CancelledAction));
    }

    [Fact]
    public async Task CancelWhileTheReviewLockIsHeld_IsConcurrencyConflict_AndWritesNothing()
    {
        var world = await WorldAsync(withRoute: true);
        var submitted = await SubmitAsync(world.Requester, world.Scope.SiteA1);
        await using var impatientHost = new AuthenticationTestHost(services =>
            services.AddSingleton(new RepairRequestRoutingLockOptions { TimeoutMilliseconds = 200 }));

        await using (await HeldReviewLock.AcquireAsync(_host, RepairRequestRoutingLock.Resource(submitted.Id)))
        {
            var blocked = await CancelAsync(world.Requester, submitted.Id, submitted.RowVersion, host: impatientHost);
            Assert.Equal(CommandFailure.ConcurrencyConflict, blocked.Error?.Failure);
        }

        var stored = await StoredAsync(submitted.Id);
        Assert.Equal(RepairRequestStatus.UnderReview, stored.Status);
        Assert.Null(stored.CancelReason);
        Assert.Equal(submitted.RowVersion, stored.RowVersion);
        Assert.Empty(await AuditsAsync(submitted.Id, RepairRequestAudit.CancelledAction));

        Assert.True((await CancelAsync(world.Requester, submitted.Id, submitted.RowVersion, host: impatientHost)).Succeeded);
    }

    /// <summary>An in-flight competitor: a separate session holding one request's review-workflow lock until disposed.</summary>
    private sealed class HeldReviewLock : IAsyncDisposable
    {
        private readonly AsyncServiceScope _scope;
        private readonly IDbContextTransaction _transaction;

        private HeldReviewLock(AsyncServiceScope scope, IDbContextTransaction transaction)
        {
            _scope = scope;
            _transaction = transaction;
        }

        public static async Task<HeldReviewLock> AcquireAsync(AuthenticationTestHost host, string resource)
        {
            var scope = host.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<RepairRequestDbContext>();
            var transaction = await db.Database.BeginTransactionAsync();
            var result = (await db.Database.SqlQuery<int>($"""
                DECLARE @lock_result int;
                EXEC @lock_result = sp_getapplock @Resource = {resource}, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 5000;
                SELECT @lock_result AS [Value];
                """).ToListAsync()).Single();
            Assert.True(result >= 0, $"The test could not take the review lock ({result}).");
            return new HeldReviewLock(scope, transaction);
        }

        public async ValueTask DisposeAsync()
        {
            await _transaction.RollbackAsync();
            await _transaction.DisposeAsync();
            await _scope.DisposeAsync();
        }
    }

    // ---------------- Transaction ----------------

    [Fact]
    public async Task GenuineDatabaseFailure_RollsBackTheCancel_AndReleasesTheLock()
    {
        var world = await WorldAsync(withRoute: true);
        var submitted = await SubmitAsync(world.Requester, world.Scope.SiteA1);
        await using var failingHost = new AuthenticationTestHost(services =>
            services.ConfigureDbContext<RepairRequestDbContext>(options => options.AddInterceptors(new FailCancelSaveInterceptor())));

        var thrown = await Assert.ThrowsAsync<DbUpdateException>(() => CancelAsync(world.Requester, submitted.Id, submitted.RowVersion, host: failingHost));

        Assert.Equal(50000, Assert.IsType<SqlException>(thrown.InnerException).Number);
        var stored = await StoredAsync(submitted.Id);
        Assert.Equal(RepairRequestStatus.UnderReview, stored.Status);
        Assert.Null(stored.CancelReason);
        Assert.Equal(submitted.RowVersion, stored.RowVersion);
        Assert.Empty(await AuditsAsync(submitted.Id, RepairRequestAudit.CancelledAction));
        var resource = RepairRequestRoutingLock.Resource(submitted.Id);
        Assert.Equal(1, await WithDbAsync(db => db.Database
            .SqlQuery<int>($"SELECT APPLOCK_TEST('public', {resource}, 'Exclusive', 'Session') AS [Value]")
            .SingleAsync()));

        Assert.True((await CancelAsync(world.Requester, submitted.Id, submitted.RowVersion)).Succeeded);
    }

    /// <summary>Turns the cancel save batch (it inserts the cancel audit) into a genuine SQL Server error.</summary>
    private sealed class FailCancelSaveInterceptor : DbCommandInterceptor
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
