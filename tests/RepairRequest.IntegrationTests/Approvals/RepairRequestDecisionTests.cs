using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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

namespace RepairRequest.IntegrationTests.Approvals;

/// <summary>
/// S1-008 Approve/Reject against LocalDB: atomic decision (request state + approval step + audit), exactly one winner among
/// competing decisions, full rollback on a genuine failure, the independent self-decision rule, scope loss, and decided
/// steps that routing can never touch again (ST-RR-004/005; DEC-PRE-S1-008-01/03).
/// </summary>
[Collection(PersistenceDatabaseCollection.Name)]
public sealed class RepairRequestDecisionTests : IAsyncLifetime
{
    private const string ValidHash = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";
    private const string ContinuationReason = "Separate fault used by decision tests";
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly DateTime Start = new(2026, 9, 20, 8, 0, 0, DateTimeKind.Utc);

    private readonly AuthenticationTestHost _host = new();

    public RepairRequestDecisionTests(PersistenceDatabaseFixture database)
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

    private sealed record Scenario(ScopeWorld World, CurrentUser Admin, CurrentUser Requester, CurrentUser Approver, Guid RequestId, byte[] RowVersion);

    private async Task<CurrentUser> UserAsync(Guid tenantId, Site[] sites, params string[] roles)
    {
        var user = await _host.CreateUserInTenantAsync(tenantId, roles);
        if (sites.Length > 0)
        {
            await WithDbAsync(db => ScopeWorld.AssignSitesAsync(db, tenantId, user.Id, sites));
        }

        return new CurrentUser(user.Id, tenantId, roles);
    }

    /// <summary>A request routed to UNDER_REVIEW with exactly one assigned approver (S1-007R routing through the real services).</summary>
    private async Task<Scenario> UnderReviewAsync(params string[] requesterRoles)
    {
        var world = await WithDbAsync(ScopeWorld.CreateAsync);
        await using (var seedScope = _host.CreateScope())
        {
            await seedScope.ServiceProvider.GetRequiredService<RequestLookupSeeder>().SeedTenantAsync(world.TenantId, None);
        }

        var admin = await UserAsync(world.TenantId, [], RoleCodes.Administrator);
        var requester = await UserAsync(world.TenantId, [world.SiteA1], requesterRoles.Length == 0 ? [RoleCodes.Requester] : requesterRoles);
        var approver = await UserAsync(world.TenantId, [world.SiteA1], RoleCodes.Approver);

        await using (var routeScope = _host.CreateScope())
        {
            var route = await routeScope.ServiceProvider.GetRequiredService<ApprovalRouteService>().CreateAsync(
                new CommandContext(admin, Guid.NewGuid()), new ApprovalRouteFields("ELECTRICAL", world.SiteA1.Id, RoleCodes.Approver, null), None);
            Assert.True(route.Succeeded);
        }

        var submitted = await SubmitAsync(requester, world.SiteA1);
        Assert.Equal(RepairRequestStatus.UnderReview, submitted.Status);
        return new Scenario(world, admin, requester, approver, submitted.Id, submitted.RowVersion);
    }

    private async Task<RepairRequestDraftDto> SubmitAsync(CurrentUser owner, Site site)
    {
        Guid requestId;
        await using (var createScope = _host.CreateScope())
        {
            var created = await createScope.ServiceProvider.GetRequiredService<RepairRequestDraftService>().CreateAsync(
                new CommandContext(owner, Guid.NewGuid()),
                new RepairRequestDraftFields(site.Id, null, "ELECTRICAL", "HIGH", owner.UserId, "Pump leaking", Start, Start.AddHours(2)),
                None);
            Assert.True(created.Succeeded);
            requestId = created.Value!.Id;
        }

        await WithDbAsync(async db =>
        {
            var file = new FileAsset(owner.TenantId, "evidence.png", "image/png", 1024, ValidHash, $"{owner.TenantId:N}/{Guid.NewGuid():N}", owner.UserId, DateTime.UtcNow);
            file.MarkClean();
            db.FileAssets.Add(file);
            db.RepairRequestAttachments.Add(new RepairRequestAttachment(requestId, file.Id, AttachmentFileRules.AccessScopeCode));
            await db.SaveChangesAsync();
        });

        await using var submitScope = _host.CreateScope();
        var rowVersion = (await submitScope.ServiceProvider.GetRequiredService<IRepairRequestDraftStore>().GetAsync(owner, requestId, None))!.RowVersion;
        var result = await submitScope.ServiceProvider.GetRequiredService<RepairRequestSubmitService>()
            .SubmitAsync(new CommandContext(owner, Guid.NewGuid()), requestId, rowVersion, ContinuationReason, None);
        Assert.True(result.Succeeded);
        return result.Value!;
    }

    private async Task<CommandResult<RepairRequestDraftDto>> DecideAsync(
        CurrentUser caller,
        Guid requestId,
        byte[] rowVersion,
        bool approve,
        string reason = "Covered by the service contract",
        AuthenticationTestHost? host = null)
    {
        await using var scope = (host ?? _host).CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<RepairRequestDecisionService>();
        var context = new CommandContext(caller, Guid.NewGuid());
        return approve
            ? await service.ApproveAsync(context, requestId, rowVersion, None)
            : await service.RejectAsync(context, requestId, rowVersion, reason, None);
    }

    private Task<RepairRequestAggregate> StoredAsync(Guid requestId) =>
        WithDbAsync(db => db.RepairRequests.AsNoTracking().SingleAsync(request => request.Id == requestId));

    private Task<RepairRequestApproval> StepAsync(Guid requestId) =>
        WithDbAsync(db => db.RepairRequestApprovals.AsNoTracking().SingleAsync(approval => approval.RepairRequestId == requestId));

    private Task<List<AuditHistory>> DecisionAuditsAsync(Guid requestId) =>
        WithDbAsync(db => db.AuditHistory.AsNoTracking()
            .Where(audit => audit.EntityId == requestId
                            && (audit.ActionCode == RepairRequestAudit.ApprovedAction || audit.ActionCode == RepairRequestAudit.RejectedAction))
            .ToListAsync());

    private async Task AssertUndecidedAsync(Scenario scenario)
    {
        var stored = await StoredAsync(scenario.RequestId);
        Assert.Equal(RepairRequestStatus.UnderReview, stored.Status);
        Assert.Null(stored.RejectReason);
        Assert.Equal(scenario.RowVersion, stored.RowVersion);
        var step = await StepAsync(scenario.RequestId);
        Assert.Equal(ApprovalStatus.Pending, step.Status);
        Assert.Null(step.DecidedAt);
        Assert.Null(step.DecisionReason);
        Assert.Empty(await DecisionAuditsAsync(scenario.RequestId));
    }

    // ---------------- Success ----------------

    [Fact]
    public async Task Approve_PersistsApproved_TheDecidedStep_AndOneAudit_InOneTransaction()
    {
        var scenario = await UnderReviewAsync();

        _host.Logs.Clear();
        var result = await DecideAsync(scenario.Approver, scenario.RequestId, scenario.RowVersion, approve: true);
        var commands = _host.Logs.ExecutedDbCommandCount;

        Assert.True(result.Succeeded);
        Assert.Equal(RepairRequestStatus.Approved, result.Value!.Status);
        Assert.NotEqual(scenario.RowVersion, result.Value.RowVersion);

        var stored = await StoredAsync(scenario.RequestId);
        Assert.Equal(RepairRequestStatus.Approved, stored.Status);
        Assert.Equal(result.Value.RowVersion, stored.RowVersion);
        Assert.NotNull(stored.RequestNo);
        Assert.NotNull(stored.SubmittedAt);
        Assert.Null(stored.RejectReason);

        var step = await StepAsync(scenario.RequestId);
        Assert.Equal(ApprovalStatus.Approved, step.Status);
        Assert.Equal(scenario.Approver.UserId, step.AssignedApproverId);
        Assert.NotNull(step.DecidedAt);
        Assert.Null(step.DecisionReason);

        var audit = Assert.Single(await DecisionAuditsAsync(scenario.RequestId));
        Assert.Equal(RepairRequestAudit.ApprovedAction, audit.ActionCode);
        Assert.Equal("UNDER_REVIEW", audit.FromState);
        Assert.Equal("APPROVED", audit.ToState);
        Assert.Equal(scenario.Approver.UserId, audit.ActorId);
        Assert.Equal(step.DecidedAt, audit.OccurredAt);

        // Scope check, lock, load, step lookup, and the save (updates + audit insert): bounded, no graphs.
        Assert.InRange(commands, 1, 8);
    }

    [Fact]
    public async Task Reject_PersistsRejected_TheReasonOnRequestAndStep_AndOneAudit()
    {
        var scenario = await UnderReviewAsync();

        var result = await DecideAsync(scenario.Approver, scenario.RequestId, scenario.RowVersion, approve: false, reason: "  Covered by the service contract ");

        Assert.True(result.Succeeded);
        var stored = await StoredAsync(scenario.RequestId);
        Assert.Equal(RepairRequestStatus.Rejected, stored.Status);
        Assert.Equal("Covered by the service contract", stored.RejectReason);
        var step = await StepAsync(scenario.RequestId);
        Assert.Equal(ApprovalStatus.Rejected, step.Status);
        Assert.Equal("Covered by the service contract", step.DecisionReason);
        var audit = Assert.Single(await DecisionAuditsAsync(scenario.RequestId));
        Assert.Equal(RepairRequestAudit.RejectedAction, audit.ActionCode);
        Assert.Equal("Covered by the service contract", audit.Reason);
        Assert.Equal(scenario.Approver.UserId, audit.ActorId);
    }

    // ---------------- Concurrency ----------------

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task ConcurrentDecisions_OfOneRequest_ExactlyOneWins_WithOneDecisionAndOneAudit(bool firstApproves, bool secondApproves)
    {
        var scenario = await UnderReviewAsync();

        var results = await Task.WhenAll(
            Task.Run(() => DecideAsync(scenario.Approver, scenario.RequestId, scenario.RowVersion, firstApproves)),
            Task.Run(() => DecideAsync(scenario.Approver, scenario.RequestId, scenario.RowVersion, secondApproves)));

        var winner = Assert.Single(results, result => result.Succeeded);
        var loser = Assert.Single(results, result => !result.Succeeded);
        Assert.Equal(CommandFailure.ConcurrencyConflict, loser.Error!.Failure);

        var stored = await StoredAsync(scenario.RequestId);
        var step = await StepAsync(scenario.RequestId);
        var audit = Assert.Single(await DecisionAuditsAsync(scenario.RequestId));
        Assert.Equal(winner.Value!.Status, stored.Status);
        if (stored.Status == RepairRequestStatus.Approved)
        {
            Assert.Equal(ApprovalStatus.Approved, step.Status);
            Assert.Equal(RepairRequestAudit.ApprovedAction, audit.ActionCode);
            Assert.Null(stored.RejectReason);
        }
        else
        {
            Assert.Equal(RepairRequestStatus.Rejected, stored.Status);
            Assert.Equal(ApprovalStatus.Rejected, step.Status);
            Assert.Equal(RepairRequestAudit.RejectedAction, audit.ActionCode);
            Assert.NotNull(stored.RejectReason);
        }
    }

    // ---------------- Transaction ----------------

    [Fact]
    public async Task GenuineDatabaseFailure_RollsBackTheWholeDecision_AndReleasesTheLock()
    {
        var scenario = await UnderReviewAsync();
        await using var failingHost = new AuthenticationTestHost(services =>
            services.ConfigureDbContext<RepairRequestDbContext>(options => options.AddInterceptors(new FailDecisionSaveInterceptor())));

        var thrown = await Assert.ThrowsAsync<DbUpdateException>(() =>
            DecideAsync(scenario.Approver, scenario.RequestId, scenario.RowVersion, approve: false, host: failingHost));

        Assert.Equal(50000, Assert.IsType<SqlException>(thrown.InnerException).Number);
        await AssertUndecidedAsync(scenario);
        var resource = RepairRequestRoutingLock.Resource(scenario.RequestId);
        Assert.Equal(1, await WithDbAsync(db => db.Database
            .SqlQuery<int>($"SELECT APPLOCK_TEST('public', {resource}, 'Exclusive', 'Session') AS [Value]")
            .SingleAsync()));

        Assert.True((await DecideAsync(scenario.Approver, scenario.RequestId, scenario.RowVersion, approve: true)).Succeeded);
    }

    // ---------------- Authorization independent of routing ----------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreatorHoldingApprover_WithTheStepForcedToThem_IsAccessDenied(bool approve)
    {
        var scenario = await UnderReviewAsync(RoleCodes.Requester, RoleCodes.Approver);
        var creatorId = scenario.Requester.UserId;
        var requestId = scenario.RequestId;

        // Routing never assigns the creator; force it to prove Approve/Reject re-check the rule themselves.
        await WithDbAsync(db => db.Database.ExecuteSqlAsync($"""
            UPDATE [repair_request_approval] SET [assigned_approver_id] = {creatorId} WHERE [repair_request_id] = {requestId}
            """));

        var result = await DecideAsync(scenario.Requester, scenario.RequestId, scenario.RowVersion, approve);

        Assert.Equal(CommandFailure.AccessDenied, result.Error?.Failure);
        Assert.Equal(RepairRequestStatus.UnderReview, (await StoredAsync(scenario.RequestId)).Status);
        Assert.Equal(ApprovalStatus.Pending, (await StepAsync(scenario.RequestId)).Status);
        Assert.Empty(await DecisionAuditsAsync(scenario.RequestId));
    }

    [Fact]
    public async Task OtherApproverOfTheSite_AndAdministratorPlusApprover_AreAccessDenied()
    {
        var scenario = await UnderReviewAsync();
        var otherApprover = await UserAsync(scenario.World.TenantId, [scenario.World.SiteA1], RoleCodes.Approver);
        var adminApprover = await UserAsync(scenario.World.TenantId, [scenario.World.SiteA1], RoleCodes.Administrator, RoleCodes.Approver);

        Assert.Equal(CommandFailure.AccessDenied, (await DecideAsync(otherApprover, scenario.RequestId, scenario.RowVersion, approve: true)).Error?.Failure);
        Assert.Equal(CommandFailure.AccessDenied, (await DecideAsync(adminApprover, scenario.RequestId, scenario.RowVersion, approve: false)).Error?.Failure);
        await AssertUndecidedAsync(scenario);
    }

    [Fact]
    public async Task AssignedApproverWhoLostSiteScope_IsNotFound()
    {
        var scenario = await UnderReviewAsync();
        var approverId = scenario.Approver.UserId;
        await WithDbAsync(db => db.Database.ExecuteSqlAsync($"DELETE FROM [user_site_scope] WHERE [user_id] = {approverId}"));

        var result = await DecideAsync(scenario.Approver, scenario.RequestId, scenario.RowVersion, approve: true);

        Assert.Equal(CommandFailure.NotFound, result.Error?.Failure);
        await AssertUndecidedAsync(scenario);
    }

    [Fact]
    public async Task ApproverOfAnotherTenant_IsNotFound()
    {
        var scenario = await UnderReviewAsync();
        var foreign = await UserAsync(scenario.World.OtherTenantId, [scenario.World.OtherTenantSite], RoleCodes.Approver);

        Assert.Equal(CommandFailure.NotFound, (await DecideAsync(foreign, scenario.RequestId, scenario.RowVersion, approve: true)).Error?.Failure);
        await AssertUndecidedAsync(scenario);
    }

    // ---------------- State ----------------

    [Fact]
    public async Task SubmittedRequestWithoutAssignment_IsStateConflict()
    {
        var world = await WithDbAsync(ScopeWorld.CreateAsync);
        await using (var seedScope = _host.CreateScope())
        {
            await seedScope.ServiceProvider.GetRequiredService<RequestLookupSeeder>().SeedTenantAsync(world.TenantId, None);
        }

        var requester = await UserAsync(world.TenantId, [world.SiteA1], RoleCodes.Requester);
        var approver = await UserAsync(world.TenantId, [world.SiteA1], RoleCodes.Approver);
        var submitted = await SubmitAsync(requester, world.SiteA1);
        Assert.Equal(RepairRequestStatus.Submitted, submitted.Status);

        Assert.Equal(CommandFailure.StateConflict, (await DecideAsync(approver, submitted.Id, submitted.RowVersion, approve: true)).Error?.Failure);
        Assert.Equal(CommandFailure.StateConflict, (await DecideAsync(approver, submitted.Id, submitted.RowVersion, approve: false)).Error?.Failure);
        Assert.Equal(RepairRequestStatus.Submitted, (await StoredAsync(submitted.Id)).Status);
        Assert.Empty(await DecisionAuditsAsync(submitted.Id));
    }

    [Fact]
    public async Task DecidedRequest_CannotBeDecidedAgain_NorReroutedByAdminRetry()
    {
        var scenario = await UnderReviewAsync();
        var approved = await DecideAsync(scenario.Approver, scenario.RequestId, scenario.RowVersion, approve: true);
        Assert.True(approved.Succeeded);
        var decidedStep = await StepAsync(scenario.RequestId);

        Assert.Equal(CommandFailure.StateConflict, (await DecideAsync(scenario.Approver, scenario.RequestId, approved.Value!.RowVersion, approve: true)).Error?.Failure);
        Assert.Equal(CommandFailure.StateConflict, (await DecideAsync(scenario.Approver, scenario.RequestId, approved.Value.RowVersion, approve: false)).Error?.Failure);

        await using (var scope = _host.CreateScope())
        {
            var retry = await scope.ServiceProvider.GetRequiredService<RepairRequestRoutingService>()
                .RetryAsync(new CommandContext(scenario.Admin, Guid.NewGuid()), scenario.RequestId, approved.Value.RowVersion, None);
            Assert.Equal(CommandFailure.StateConflict, retry.Error?.Failure);
        }

        var step = await StepAsync(scenario.RequestId);
        Assert.Equal(ApprovalStatus.Approved, step.Status);
        Assert.Equal(decidedStep.DecidedAt, step.DecidedAt);
        Assert.Equal(decidedStep.RowVersion, step.RowVersion);
        Assert.Single(await DecisionAuditsAsync(scenario.RequestId));
    }

    /// <summary>Turns the decision save batch (it inserts the decision audit) into a genuine SQL Server error.</summary>
    private sealed class FailDecisionSaveInterceptor : DbCommandInterceptor
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
