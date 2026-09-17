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

namespace RepairRequest.IntegrationTests.Approvals;

/// <summary>
/// S1-007R routing against LocalDB (ST-RR-003; DEC-PRE-S1-007R-01..10; DEC-PRE-S1-008-01): Submit then routing in its own
/// transaction, route precedence, eligibility with Site scope, segregation of duties, approved failure data, Admin
/// Retry of legacy and failed requests, the routing-issue list, concurrent retries and database-level enforcement.
/// </summary>
[Collection(PersistenceDatabaseCollection.Name)]
public sealed class ApprovalRoutingTests : IAsyncLifetime
{
    private const string ValidHash = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";
    private const string ContinuationReason = "Separate fault used by routing tests";
    private const int CheckOrForeignKeyViolation = 547;
    private const int UniqueIndexViolation = 2601;
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly DateTime Start = new(2026, 9, 20, 8, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan ConcurrencyTimeout = TimeSpan.FromSeconds(30);

    private readonly AuthenticationTestHost _host = new();

    public ApprovalRoutingTests(PersistenceDatabaseFixture database)
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

    private async Task<ScopeWorld> WorldAsync()
    {
        var world = await WithDbAsync(ScopeWorld.CreateAsync);
        await using var scope = _host.CreateScope();
        var seeder = scope.ServiceProvider.GetRequiredService<RequestLookupSeeder>();
        await seeder.SeedTenantAsync(world.TenantId, None);
        await seeder.SeedTenantAsync(world.OtherTenantId, None);
        return world;
    }

    private async Task<CurrentUser> UserAsync(Guid tenantId, Site[] sites, params string[] roles)
    {
        var user = await _host.CreateUserInTenantAsync(tenantId, roles);
        if (sites.Length > 0)
        {
            await WithDbAsync(db => ScopeWorld.AssignSitesAsync(db, tenantId, user.Id, sites));
        }

        return new CurrentUser(user.Id, tenantId, roles);
    }

    private Task<CurrentUser> AdministratorAsync(Guid tenantId) => UserAsync(tenantId, [], RoleCodes.Administrator);

    private async Task<Guid> CreateRouteAsync(CurrentUser admin, Site? site, Guid? approverUserId = null, string category = "ELECTRICAL")
    {
        await using var scope = _host.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<ApprovalRouteService>().CreateAsync(
            new CommandContext(admin, Guid.NewGuid()),
            new ApprovalRouteFields(category, site?.Id, RoleCodes.Approver, approverUserId),
            None);
        Assert.True(result.Succeeded, string.Join(";", result.Error?.Errors.SelectMany(error => error.Value) ?? []));
        return result.Value!.Id;
    }

    private async Task<CommandResult<RepairRequestDraftDto>> SubmitNewRequestAsync(CurrentUser owner, Site site, string category = "ELECTRICAL")
    {
        Guid requestId;
        await using (var createScope = _host.CreateScope())
        {
            var created = await createScope.ServiceProvider.GetRequiredService<RepairRequestDraftService>().CreateAsync(
                new CommandContext(owner, Guid.NewGuid()),
                new RepairRequestDraftFields(site.Id, null, category, "HIGH", owner.UserId, "Pump leaking", Start, Start.AddHours(2)),
                None);
            Assert.True(created.Succeeded, string.Join(";", created.Error?.Errors.Keys ?? []));
            requestId = created.Value!.Id;
        }

        await AttachCleanPhotoAsync(owner, requestId);

        await using var submitScope = _host.CreateScope();
        var rowVersion = (await submitScope.ServiceProvider.GetRequiredService<IRepairRequestDraftStore>().GetAsync(owner, requestId, None))!.RowVersion;
        return await submitScope.ServiceProvider.GetRequiredService<RepairRequestSubmitService>()
            .SubmitAsync(new CommandContext(owner, Guid.NewGuid()), requestId, rowVersion, ContinuationReason, None);
    }

    /// <summary>A SUBMITTED request that predates routing: no approval row and no routing audit (DEC-PRE-S1-007R-08).</summary>
    private Task<(Guid Id, byte[] RowVersion)> SeedLegacySubmittedAsync(CurrentUser owner, Site site, string requestNo, string category = "ELECTRICAL") =>
        WithDbAsync(async db =>
        {
            var request = RepairRequestAggregate.CreateDraft(owner.TenantId, owner.UserId);
            request.EditDraft(site.Id, null, category, "HIGH", owner.UserId, "Legacy request", Start, Start.AddHours(2));
            request.Submit(requestNo, owner.UserId, new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc), null);
            db.RepairRequests.Add(request);
            await db.SaveChangesAsync();
            return (request.Id, request.RowVersion);
        });

    private Task AttachCleanPhotoAsync(CurrentUser owner, Guid requestId) =>
        WithDbAsync(async db =>
        {
            var file = new FileAsset(owner.TenantId, "evidence.png", "image/png", 1024, ValidHash, $"{owner.TenantId:N}/{Guid.NewGuid():N}", owner.UserId, DateTime.UtcNow);
            file.MarkClean();
            db.FileAssets.Add(file);
            db.RepairRequestAttachments.Add(new RepairRequestAttachment(requestId, file.Id, AttachmentFileRules.AccessScopeCode));
            await db.SaveChangesAsync();
        });

    private async Task<CommandResult<RoutingResultDto>> RetryAsync(CurrentUser admin, Guid requestId, byte[] rowVersion)
    {
        await using var scope = _host.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<RepairRequestRoutingService>()
            .RetryAsync(new CommandContext(admin, Guid.NewGuid()), requestId, rowVersion, None);
    }

    private Task<List<RepairRequestApproval>> ApprovalsAsync(Guid requestId) =>
        WithDbAsync(db => db.RepairRequestApprovals.AsNoTracking().Where(approval => approval.RepairRequestId == requestId).ToListAsync());

    private Task<List<AuditHistory>> AuditsAsync(Guid requestId, string actionCode) =>
        WithDbAsync(db => db.AuditHistory.AsNoTracking()
            .Where(audit => audit.EntityId == requestId && audit.ActionCode == actionCode)
            .ToListAsync());

    private Task<RepairRequestAggregate> StoredAsync(Guid requestId) =>
        WithDbAsync(db => db.RepairRequests.AsNoTracking().SingleAsync(request => request.Id == requestId));

    // ---------------- Routing after Submit (DEC-PRE-S1-007R-01/02) ----------------

    [Fact]
    public async Task Submit_WithRoleOnlyTenantDefaultRoute_AndOneEligibleApprover_AssignsIt_AndMovesToUnderReview()
    {
        var world = await WorldAsync();
        var admin = await AdministratorAsync(world.TenantId);
        var requester = await UserAsync(world.TenantId, [world.SiteA1], RoleCodes.Requester);
        var approver = await UserAsync(world.TenantId, [world.SiteA1], RoleCodes.Approver);
        await UserAsync(world.TenantId, [world.SiteA2], RoleCodes.Approver);
        await UserAsync(world.TenantId, [world.SiteA1], RoleCodes.Administrator);
        var routeId = await CreateRouteAsync(admin, null);

        var result = await SubmitNewRequestAsync(requester, world.SiteA1);

        Assert.True(result.Succeeded);
        Assert.Equal(RepairRequestStatus.UnderReview, result.Value!.Status);
        var stored = await StoredAsync(result.Value.Id);
        Assert.Equal(RepairRequestStatus.UnderReview, stored.Status);
        Assert.Equal(result.Value.RowVersion, stored.RowVersion);
        Assert.NotNull(stored.RequestNo);
        Assert.NotNull(stored.SubmittedAt);

        var approval = Assert.Single(await ApprovalsAsync(result.Value.Id));
        Assert.Equal(routeId, approval.ApprovalRouteId);
        Assert.Equal((short)1, approval.ApprovalStepNo);
        Assert.Equal(approver.UserId, approval.AssignedApproverId);
        Assert.Equal(ApprovalStatus.Pending, approval.Status);
        Assert.NotNull(approval.RoutedAt);
        Assert.Null(approval.RoutingFailureCode);

        var routed = Assert.Single(await AuditsAsync(result.Value.Id, RepairRequestRoutingAudit.RoutedAction));
        Assert.Equal("UNDER_REVIEW", routed.ToState);
        Assert.Contains("\"trigger\":\"SUBMIT\"", routed.NewValueJson);

        // ST-RR-003 actor is System; the requester performed the Submit and only initiated routing.
        Assert.Equal(SystemActors.ApprovalRouting, routed.ActorId);
        Assert.Contains($"\"initiatedBy\":\"{requester.UserId}\"", routed.NewValueJson);
        Assert.Equal(requester.UserId, Assert.Single(await AuditsAsync(result.Value.Id, RepairRequestAudit.SubmittedAction)).ActorId);
        Assert.Empty(await AuditsAsync(result.Value.Id, RepairRequestRoutingAudit.RoutingFailedAction));
    }

    [Fact]
    public async Task Submit_WithoutRoute_StaysSubmitted_WritesNoApprovalRow_AndAuditsRouteNotFound()
    {
        var world = await WorldAsync();
        var requester = await UserAsync(world.TenantId, [world.SiteA1], RoleCodes.Requester);
        await UserAsync(world.TenantId, [world.SiteA1], RoleCodes.Approver);

        var result = await SubmitNewRequestAsync(requester, world.SiteA1);

        Assert.True(result.Succeeded);
        Assert.Equal(RepairRequestStatus.Submitted, result.Value!.Status);
        Assert.Equal(RepairRequestStatus.Submitted, (await StoredAsync(result.Value.Id)).Status);
        Assert.Empty(await ApprovalsAsync(result.Value.Id));

        var failed = Assert.Single(await AuditsAsync(result.Value.Id, RepairRequestRoutingAudit.RoutingFailedAction));
        Assert.Equal(RoutingFailureCodes.RouteNotFound, RepairRequestRoutingAudit.ReadFailureCode(failed.NewValueJson));
    }

    [Fact]
    public async Task Submit_RouteResolvedWithTwoEligibleApprovers_WritesAFailureRowWithoutApprover_AndStaysSubmitted()
    {
        var world = await WorldAsync();
        var admin = await AdministratorAsync(world.TenantId);
        var requester = await UserAsync(world.TenantId, [world.SiteA1], RoleCodes.Requester);
        await UserAsync(world.TenantId, [world.SiteA1], RoleCodes.Approver);
        await UserAsync(world.TenantId, [world.SiteA1], RoleCodes.Approver, RoleCodes.Supervisor);
        var routeId = await CreateRouteAsync(admin, world.SiteA1);

        var result = await SubmitNewRequestAsync(requester, world.SiteA1);

        Assert.Equal(RepairRequestStatus.Submitted, result.Value!.Status);
        var approval = Assert.Single(await ApprovalsAsync(result.Value.Id));
        Assert.Equal(routeId, approval.ApprovalRouteId);
        Assert.Null(approval.AssignedApproverId);
        Assert.Equal(RoutingFailureCodes.ApproverAmbiguous, approval.RoutingFailureCode);
        Assert.Null(approval.RoutedAt);
    }

    [Fact]
    public async Task SiteRoute_TakesPrecedence_AndItsSpecificApproverWithoutSiteScope_IsNotEligible_NoFallback()
    {
        var world = await WorldAsync();
        var admin = await AdministratorAsync(world.TenantId);
        var requester = await UserAsync(world.TenantId, [world.SiteA1], RoleCodes.Requester);
        var defaultApprover = await UserAsync(world.TenantId, [world.SiteA1], RoleCodes.Approver);
        var unscopedApprover = await UserAsync(world.TenantId, [world.SiteA2], RoleCodes.Approver);
        await CreateRouteAsync(admin, null, defaultApprover.UserId);
        var siteRouteId = await CreateRouteAsync(admin, world.SiteA1, unscopedApprover.UserId);

        var result = await SubmitNewRequestAsync(requester, world.SiteA1);

        Assert.Equal(RepairRequestStatus.Submitted, result.Value!.Status);
        var approval = Assert.Single(await ApprovalsAsync(result.Value.Id));
        Assert.Equal(siteRouteId, approval.ApprovalRouteId);
        Assert.Equal(RoutingFailureCodes.ApproverNotEligible, approval.RoutingFailureCode);
    }

    // ---------------- Segregation of duties (DEC-PRE-S1-008-01) + retry of a failure row ----------------

    [Fact]
    public async Task RequesterHoldingApprover_IsNeverAssignedTheirOwnRequest_AndRetryAssignsAnotherApprover()
    {
        var world = await WorldAsync();
        var admin = await AdministratorAsync(world.TenantId);
        var requesterApprover = await UserAsync(world.TenantId, [world.SiteA1], RoleCodes.Requester, RoleCodes.Approver);
        await CreateRouteAsync(admin, world.SiteA1);

        var submitted = await SubmitNewRequestAsync(requesterApprover, world.SiteA1);

        Assert.Equal(RepairRequestStatus.Submitted, submitted.Value!.Status);
        var failure = Assert.Single(await ApprovalsAsync(submitted.Value.Id));
        Assert.Equal(RoutingFailureCodes.ApproverNotFound, failure.RoutingFailureCode);

        var otherApprover = await UserAsync(world.TenantId, [world.SiteA1], RoleCodes.Approver);
        var retried = await RetryAsync(admin, submitted.Value.Id, submitted.Value.RowVersion);

        Assert.True(retried.Succeeded);
        Assert.Equal(RepairRequestStatus.UnderReview, retried.Value!.Status);
        var assigned = Assert.Single(await ApprovalsAsync(submitted.Value.Id));
        Assert.Equal(failure.Id, assigned.Id);
        Assert.Equal(otherApprover.UserId, assigned.AssignedApproverId);
        Assert.NotEqual(requesterApprover.UserId, assigned.AssignedApproverId);
        Assert.Null(assigned.RoutingFailureCode);
        var retryAudit = Assert.Single(await AuditsAsync(submitted.Value.Id, RepairRequestRoutingAudit.RoutedAction));
        Assert.Contains("\"trigger\":\"ADMIN_RETRY\"", retryAudit.NewValueJson);
        Assert.Equal(SystemActors.ApprovalRouting, retryAudit.ActorId);
        Assert.NotEqual(admin.UserId, retryAudit.ActorId);
        Assert.Contains($"\"initiatedBy\":\"{admin.UserId}\"", retryAudit.NewValueJson);
    }

    // ---------------- Admin Retry + routing-issue list (DEC-PRE-S1-007R-07/08/10) ----------------

    [Fact]
    public async Task RoutingIssues_ListOnlyUnroutedAndFailedSubmittedRequests_OfTheTenant_AndRetryRoutesALegacyRequest()
    {
        var world = await WorldAsync();
        var admin = await AdministratorAsync(world.TenantId);
        var otherAdmin = await AdministratorAsync(world.OtherTenantId);
        var requester = await UserAsync(world.TenantId, [world.SiteA1], RoleCodes.Requester);
        var otherRequester = await UserAsync(world.OtherTenantId, [world.OtherTenantSite], RoleCodes.Requester);
        await UserAsync(world.TenantId, [world.SiteA1], RoleCodes.Approver);
        await CreateRouteAsync(admin, world.SiteA1, category: "MECHANICAL");

        var routed = await SubmitNewRequestAsync(requester, world.SiteA1, "MECHANICAL");
        var failed = await SubmitNewRequestAsync(requester, world.SiteA1, "PLUMBING");
        var legacy = await SeedLegacySubmittedAsync(requester, world.SiteA1, "RR-2026-900001");
        await SeedLegacySubmittedAsync(otherRequester, world.OtherTenantSite, "RR-2026-900001");
        Assert.Equal(RepairRequestStatus.UnderReview, routed.Value!.Status);
        Assert.Equal(RepairRequestStatus.Submitted, failed.Value!.Status);

        await using (var scope = _host.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<RepairRequestRoutingService>();
            _host.Logs.Clear();
            var page = await service.ListIssuesAsync(admin, new PageRequest(1, 50), None);
            var commands = _host.Logs.ExecutedDbCommandCount;

            Assert.Equal(2, page.TotalCount);
            Assert.Equal([legacy.Id, failed.Value.Id], page.Items.Select(item => item.RepairRequestId));
            Assert.Null(page.Items[0].LastRoutingFailureCode);
            Assert.Equal(RoutingFailureCodes.RouteNotFound, page.Items[1].LastRoutingFailureCode);
            Assert.Equal("RR-2026-900001", page.Items[0].RequestNo);
            Assert.InRange(commands, 1, 2);

            var otherTenantPage = await service.ListIssuesAsync(otherAdmin, new PageRequest(1, 50), None);
            Assert.Single(otherTenantPage.Items);
            Assert.DoesNotContain(otherTenantPage.Items, item => item.RepairRequestId == legacy.Id);

            var businessPage = await service.ListIssuesAsync(requester, new PageRequest(1, 50), None);
            Assert.Empty(businessPage.Items);
        }

        Assert.Equal(CommandFailure.NotFound, (await RetryAsync(otherAdmin, legacy.Id, legacy.RowVersion)).Error?.Failure);
        Assert.Equal(CommandFailure.ConcurrencyConflict, (await RetryAsync(admin, legacy.Id, [0, 0, 0, 0, 0, 0, 0, 1])).Error?.Failure);

        await CreateRouteAsync(admin, null, category: "ELECTRICAL");
        var retried = await RetryAsync(admin, legacy.Id, legacy.RowVersion);

        Assert.True(retried.Succeeded);
        Assert.Equal(RepairRequestStatus.UnderReview, retried.Value!.Status);
        Assert.Equal("RR-2026-900001", (await StoredAsync(legacy.Id)).RequestNo);
        Assert.Equal(CommandFailure.StateConflict, (await RetryAsync(admin, legacy.Id, retried.Value.RowVersion)).Error?.Failure);

        await using var verify = _host.CreateScope();
        var remaining = await verify.ServiceProvider.GetRequiredService<RepairRequestRoutingService>().ListIssuesAsync(admin, new PageRequest(1, 50), None);
        Assert.Equal([failed.Value.Id], remaining.Items.Select(item => item.RepairRequestId));
    }

    [Fact]
    public async Task ConcurrentAdminRetries_OfOneRequest_ExactlyOneRoutes_WithOneAssignmentAndOneAudit()
    {
        var world = await WorldAsync();
        var admin = await AdministratorAsync(world.TenantId);
        var requester = await UserAsync(world.TenantId, [world.SiteA1], RoleCodes.Requester);
        await UserAsync(world.TenantId, [world.SiteA1], RoleCodes.Approver);
        var legacy = await SeedLegacySubmittedAsync(requester, world.SiteA1, "RR-2026-900002");
        await CreateRouteAsync(admin, world.SiteA1);

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => RetryAsync(admin, legacy.Id, legacy.RowVersion))));

        var winner = Assert.Single(results, result => result.Succeeded);
        Assert.Equal(RepairRequestStatus.UnderReview, winner.Value!.Status);
        Assert.All(results.Where(result => !result.Succeeded), result =>
            Assert.Contains(result.Error!.Failure, new[] { CommandFailure.ConcurrencyConflict, CommandFailure.StateConflict }));
        Assert.NotNull(Assert.Single(await ApprovalsAsync(legacy.Id)).AssignedApproverId);
        Assert.Single(await AuditsAsync(legacy.Id, RepairRequestRoutingAudit.RoutedAction));
    }

    [Fact]
    public async Task SubmitRouting_RacingAdminRetry_ExactlyOneRoutes_WithOneAssignmentAndOneAudit()
    {
        var world = await WorldAsync();
        var admin = await AdministratorAsync(world.TenantId);
        var requester = await UserAsync(world.TenantId, [world.SiteA1], RoleCodes.Requester);
        var approver = await UserAsync(world.TenantId, [world.SiteA1], RoleCodes.Approver);
        var submitted = await SeedLegacySubmittedAsync(requester, world.SiteA1, "RR-2026-900004");
        await CreateRouteAsync(admin, world.SiteA1);

        async Task<CommandResult<RoutingResultDto>> RouteAfterSubmitAsync()
        {
            await using var scope = _host.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<RepairRequestRoutingService>()
                .RouteSubmittedAsync(new CommandContext(requester, Guid.NewGuid()), submitted.Id, submitted.RowVersion, None);
        }

        // DEC-PRE-S1-007R-01/07: the post-Submit routing and an Admin Retry of the same request, with the same row version.
        var results = await Task.WhenAll(
            Task.Run(RouteAfterSubmitAsync),
            Task.Run(() => RetryAsync(admin, submitted.Id, submitted.RowVersion)));

        var winner = Assert.Single(results, result => result.Succeeded);
        Assert.Equal(RepairRequestStatus.UnderReview, winner.Value!.Status);
        var loser = Assert.Single(results, result => !result.Succeeded);
        Assert.Contains(loser.Error!.Failure, new[] { CommandFailure.ConcurrencyConflict, CommandFailure.StateConflict });

        Assert.Equal(RepairRequestStatus.UnderReview, (await StoredAsync(submitted.Id)).Status);
        Assert.Equal(approver.UserId, Assert.Single(await ApprovalsAsync(submitted.Id)).AssignedApproverId);
        Assert.Single(await AuditsAsync(submitted.Id, RepairRequestRoutingAudit.RoutedAction));
        Assert.Empty(await AuditsAsync(submitted.Id, RepairRequestRoutingAudit.RoutingFailedAction));
    }

    [Fact]
    public async Task SpecificApproverWithoutApproverRole_AtRoutingTime_IsNotEligible_WritesFailureRow_AndStaysSubmitted()
    {
        var world = await WorldAsync();
        var admin = await AdministratorAsync(world.TenantId);
        var requester = await UserAsync(world.TenantId, [world.SiteA1], RoleCodes.Requester);
        var named = await UserAsync(world.TenantId, [world.SiteA1], RoleCodes.Approver, RoleCodes.Supervisor);
        var routeId = await CreateRouteAsync(admin, world.SiteA1, named.UserId);

        // The named approver keeps Site scope and another business role but loses APPROVER after configuration.
        var userId = named.UserId;
        var approverRole = RoleCodes.Approver;
        await WithDbAsync(db => db.Database.ExecuteSqlAsync($"""
            DELETE [ur] FROM [AspNetUserRoles] AS [ur]
            INNER JOIN [AspNetRoles] AS [r] ON [r].[Id] = [ur].[RoleId]
            WHERE [ur].[UserId] = {userId} AND [r].[Name] = {approverRole}
            """));

        var result = await SubmitNewRequestAsync(requester, world.SiteA1);

        Assert.True(result.Succeeded);
        Assert.Equal(RepairRequestStatus.Submitted, result.Value!.Status);
        Assert.Equal(RepairRequestStatus.Submitted, (await StoredAsync(result.Value.Id)).Status);
        var failure = Assert.Single(await ApprovalsAsync(result.Value.Id));
        Assert.Equal(routeId, failure.ApprovalRouteId);
        Assert.Null(failure.AssignedApproverId);
        Assert.Equal(RoutingFailureCodes.ApproverNotEligible, failure.RoutingFailureCode);
        Assert.Empty(await AuditsAsync(result.Value.Id, RepairRequestRoutingAudit.RoutedAction));
    }

    [Fact]
    public async Task Retry_AfterRouteDeactivated_RemovesStaleApproverFailureRow_ListShowsRouteNotFound_HistoryKeptInAudit()
    {
        var world = await WorldAsync();
        var admin = await AdministratorAsync(world.TenantId);
        var requester = await UserAsync(world.TenantId, [world.SiteA1], RoleCodes.Requester);
        var firstApprover = await UserAsync(world.TenantId, [world.SiteA1], RoleCodes.Approver);
        await UserAsync(world.TenantId, [world.SiteA1], RoleCodes.Approver);
        var siteRouteId = await CreateRouteAsync(admin, world.SiteA1);

        var submitted = await SubmitNewRequestAsync(requester, world.SiteA1);
        Assert.Equal(RoutingFailureCodes.ApproverAmbiguous, Assert.Single(await ApprovalsAsync(submitted.Value!.Id)).RoutingFailureCode);

        await using (var scope = _host.CreateScope())
        {
            var routeVersion = await scope.ServiceProvider.GetRequiredService<RepairRequestDbContext>().ApprovalRoutes
                .Where(route => route.Id == siteRouteId).Select(route => route.RowVersion).SingleAsync();
            var deactivated = await scope.ServiceProvider.GetRequiredService<ApprovalRouteService>()
                .DeactivateAsync(new CommandContext(admin, Guid.NewGuid()), siteRouteId, routeVersion, None);
            Assert.True(deactivated.Succeeded);
        }

        var retried = await RetryAsync(admin, submitted.Value.Id, submitted.Value.RowVersion);

        // Current state: a route-level failure has no approval row (DEC-PRE-S1-007R-09), so the stale row is gone.
        Assert.True(retried.Succeeded);
        Assert.Equal(RepairRequestStatus.Submitted, retried.Value!.Status);
        Assert.Equal(RoutingFailureCodes.RouteNotFound, retried.Value.RoutingFailureCode);
        Assert.Empty(await ApprovalsAsync(submitted.Value.Id));
        Assert.Equal(RepairRequestStatus.Submitted, (await StoredAsync(submitted.Value.Id)).Status);

        await using (var scope = _host.CreateScope())
        {
            var page = await scope.ServiceProvider.GetRequiredService<RepairRequestRoutingService>().ListIssuesAsync(admin, new PageRequest(1, 50), None);
            Assert.Equal(RoutingFailureCodes.RouteNotFound, Assert.Single(page.Items, item => item.RepairRequestId == submitted.Value.Id).LastRoutingFailureCode);
        }

        // History: both attempts remain in the append-only routing audit, including the earlier route and code.
        var failures = (await AuditsAsync(submitted.Value.Id, RepairRequestRoutingAudit.RoutingFailedAction))
            .OrderBy(audit => audit.OccurredAt).ThenBy(audit => audit.Id).ToList();
        Assert.Equal(2, failures.Count);
        Assert.Equal(
            [RoutingFailureCodes.ApproverAmbiguous, RoutingFailureCodes.RouteNotFound],
            failures.Select(audit => RepairRequestRoutingAudit.ReadFailureCode(audit.NewValueJson)).Order(StringComparer.Ordinal));
        Assert.Contains(failures, audit => audit.NewValueJson!.Contains(siteRouteId.ToString()));

        // Recovery still works from the current configuration.
        await CreateRouteAsync(admin, null, firstApprover.UserId);
        var routed = await RetryAsync(admin, submitted.Value.Id, submitted.Value.RowVersion);
        Assert.Equal(RepairRequestStatus.UnderReview, routed.Value!.Status);
        Assert.Equal(firstApprover.UserId, Assert.Single(await ApprovalsAsync(submitted.Value.Id)).AssignedApproverId);
    }

    // ---------------- Routing lock wait (DEC-PRE-S1-007R-07) ----------------

    [Fact]
    public async Task RetryRouting_WhenTheRoutingLockIsHeld_Returns409_WritesNothing_AndSucceedsAfterRelease()
    {
        var world = await WorldAsync();
        var admin = await AdministratorAsync(world.TenantId);
        var requester = await UserAsync(world.TenantId, [world.SiteA1], RoleCodes.Requester);
        var approver = await UserAsync(world.TenantId, [world.SiteA1], RoleCodes.Approver);
        var legacy = await SeedLegacySubmittedAsync(requester, world.SiteA1, "RR-2026-900005");
        await CreateRouteAsync(admin, world.SiteA1);
        await using var impatientHost = new AuthenticationTestHost(services =>
            services.AddSingleton(new RepairRequestRoutingLockOptions { TimeoutMilliseconds = 200 }));

        await using (await HeldRoutingLock.AcquireAsync(_host, RepairRequestRoutingLock.Resource(legacy.Id)))
        {
            await using var scope = impatientHost.CreateScope();
            var result = await scope.ServiceProvider.GetRequiredService<RepairRequestRoutingService>()
                .RetryAsync(new CommandContext(admin, Guid.NewGuid()), legacy.Id, legacy.RowVersion, None);

            // Deterministic concurrency conflict, not an uncontrolled failure.
            Assert.Equal(CommandFailure.ConcurrencyConflict, result.Error?.Failure);
        }

        // Nothing was written: state, Request No and the SLA start marker are untouched.
        var stored = await StoredAsync(legacy.Id);
        Assert.Equal(RepairRequestStatus.Submitted, stored.Status);
        Assert.Equal("RR-2026-900005", stored.RequestNo);
        Assert.NotNull(stored.SubmittedAt);
        Assert.Equal(legacy.RowVersion, stored.RowVersion);
        Assert.Empty(await ApprovalsAsync(legacy.Id));
        Assert.Empty(await AuditsAsync(legacy.Id, RepairRequestRoutingAudit.RoutedAction));
        Assert.Empty(await AuditsAsync(legacy.Id, RepairRequestRoutingAudit.RoutingFailedAction));

        // The competing operation released the lock normally, so a later retry succeeds.
        var retried = await RetryAsync(admin, legacy.Id, legacy.RowVersion);

        Assert.True(retried.Succeeded);
        Assert.Equal(RepairRequestStatus.UnderReview, retried.Value!.Status);
        Assert.Equal(approver.UserId, Assert.Single(await ApprovalsAsync(legacy.Id)).AssignedApproverId);
        Assert.Single(await AuditsAsync(legacy.Id, RepairRequestRoutingAudit.RoutedAction));
    }

    [Fact]
    public async Task RoutingLock_OfOneRequest_DoesNotBlockRoutingOfAnotherRequest()
    {
        var world = await WorldAsync();
        var admin = await AdministratorAsync(world.TenantId);
        var requester = await UserAsync(world.TenantId, [world.SiteA1], RoleCodes.Requester);
        await UserAsync(world.TenantId, [world.SiteA1], RoleCodes.Approver);
        var held = await SeedLegacySubmittedAsync(requester, world.SiteA1, "RR-2026-900006");
        var other = await SeedLegacySubmittedAsync(requester, world.SiteA1, "RR-2026-900007");
        await CreateRouteAsync(admin, world.SiteA1);
        await using var patientHost = new AuthenticationTestHost(services =>
            services.AddSingleton(new RepairRequestRoutingLockOptions { TimeoutMilliseconds = RepairRequestRoutingLockOptions.MaxTimeoutMilliseconds }));

        Task<CommandResult<RoutingResultDto>> blocked;
        await using (await HeldRoutingLock.AcquireAsync(_host, RepairRequestRoutingLock.Resource(held.Id)))
        {
            blocked = Task.Run(async () =>
            {
                await using var scope = patientHost.CreateScope();
                return await scope.ServiceProvider.GetRequiredService<RepairRequestRoutingService>()
                    .RetryAsync(new CommandContext(admin, Guid.NewGuid()), held.Id, held.RowVersion, None);
            });
            await WaitForRoutingLockWaitersAsync(1);

            // Another request uses another routing key and is never delayed by the held lock.
            var unrelated = await Task.Run(() => RetryAsync(admin, other.Id, other.RowVersion)).WaitAsync(ConcurrencyTimeout);

            Assert.True(unrelated.Succeeded);
            Assert.Equal(RepairRequestStatus.UnderReview, unrelated.Value!.Status);
            Assert.False(blocked.IsCompleted);
        }

        var released = await blocked.WaitAsync(ConcurrencyTimeout);

        Assert.True(released.Succeeded);
        Assert.Equal(RepairRequestStatus.UnderReview, released.Value!.Status);
        Assert.NotNull(Assert.Single(await ApprovalsAsync(held.Id)).AssignedApproverId);
    }

    [Fact]
    public async Task RetryRouting_GenuineDatabaseFailure_IsNotMappedToConcurrency_AndReleasesTheRoutingLock()
    {
        var world = await WorldAsync();
        var admin = await AdministratorAsync(world.TenantId);
        var requester = await UserAsync(world.TenantId, [world.SiteA1], RoleCodes.Requester);
        var approver = await UserAsync(world.TenantId, [world.SiteA1], RoleCodes.Approver);
        var legacy = await SeedLegacySubmittedAsync(requester, world.SiteA1, "RR-2026-900008");
        await CreateRouteAsync(admin, world.SiteA1);
        await using var failingHost = new AuthenticationTestHost(services =>
            services.ConfigureDbContext<RepairRequestDbContext>(options => options.AddInterceptors(new FailRoutingSaveInterceptor())));

        // A real SQL Server error inside the routing save: it propagates (a server error), it is never a concurrency conflict.
        DbUpdateException thrown;
        await using (var scope = failingHost.CreateScope())
        {
            thrown = await Assert.ThrowsAsync<DbUpdateException>(() => scope.ServiceProvider.GetRequiredService<RepairRequestRoutingService>()
                .RetryAsync(new CommandContext(admin, Guid.NewGuid()), legacy.Id, legacy.RowVersion, None));
        }

        Assert.Equal(50000, Assert.IsType<SqlException>(thrown.InnerException).Number);

        // The routing transaction rolled back: nothing persisted, and the routing lock is free again.
        var stored = await StoredAsync(legacy.Id);
        Assert.Equal(RepairRequestStatus.Submitted, stored.Status);
        Assert.Equal(legacy.RowVersion, stored.RowVersion);
        Assert.Empty(await ApprovalsAsync(legacy.Id));
        Assert.Empty(await AuditsAsync(legacy.Id, RepairRequestRoutingAudit.RoutedAction));
        var resource = RepairRequestRoutingLock.Resource(legacy.Id);
        Assert.Equal(1, await WithDbAsync(db => db.Database
            .SqlQuery<int>($"SELECT APPLOCK_TEST('public', {resource}, 'Exclusive', 'Session') AS [Value]")
            .SingleAsync()));

        var retried = await RetryAsync(admin, legacy.Id, legacy.RowVersion);
        Assert.Equal(RepairRequestStatus.UnderReview, retried.Value!.Status);
        Assert.Equal(approver.UserId, Assert.Single(await ApprovalsAsync(legacy.Id)).AssignedApproverId);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(30_000)]
    public void RoutingLockOptions_RejectUnboundedOrCommandTimeoutLengthWaits(int timeoutMilliseconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RepairRequestRoutingLockOptions { TimeoutMilliseconds = timeoutMilliseconds });
        Assert.Equal(5_000, new RepairRequestRoutingLockOptions().TimeoutMilliseconds);
    }

    /// <summary>Turns the routing save batch (it inserts the routing audit) into a genuine SQL Server error.</summary>
    private sealed class FailRoutingSaveInterceptor : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            FailRoutingSave(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            FailRoutingSave(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        private static void FailRoutingSave(DbCommand command)
        {
            if (command.CommandText.Contains("INSERT INTO [audit_history]", StringComparison.Ordinal))
            {
                command.CommandText = "RAISERROR(N'Simulated database failure', 16, 1);";
            }
        }
    }

    /// <summary>Waits (bounded) until at least <paramref name="expected"/> sessions of this database wait on an application lock.</summary>
    private async Task WaitForRoutingLockWaitersAsync(int expected)
    {
        var deadline = DateTime.UtcNow + ConcurrencyTimeout;
        while (true)
        {
            var waiting = await WithDbAsync(db => db.Database.SqlQuery<int>($"""
                SELECT COUNT(*) AS [Value] FROM sys.dm_tran_locks
                WHERE resource_type = 'APPLICATION' AND request_status = 'WAIT' AND resource_database_id = DB_ID()
                """).SingleAsync());

            if (waiting >= expected)
            {
                return;
            }

            Assert.True(DateTime.UtcNow < deadline, $"Expected {expected} routing-lock waiter(s); observed {waiting}.");
            await Task.Delay(TimeSpan.FromMilliseconds(20));
        }
    }

    /// <summary>An in-flight competitor: a separate session holding one request's routing lock until disposed.</summary>
    private sealed class HeldRoutingLock : IAsyncDisposable
    {
        private readonly AsyncServiceScope _scope;
        private readonly IDbContextTransaction _transaction;

        private HeldRoutingLock(AsyncServiceScope scope, IDbContextTransaction transaction)
        {
            _scope = scope;
            _transaction = transaction;
        }

        public static async Task<HeldRoutingLock> AcquireAsync(AuthenticationTestHost host, string resource)
        {
            var scope = host.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<RepairRequestDbContext>();
            var transaction = await db.Database.BeginTransactionAsync();
            var result = (await db.Database.SqlQuery<int>($"""
                DECLARE @lock_result int;
                EXEC @lock_result = sp_getapplock @Resource = {resource}, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 5000;
                SELECT @lock_result AS [Value];
                """).ToListAsync()).Single();
            Assert.True(result >= 0, $"The test could not take the routing lock ({result}).");
            return new HeldRoutingLock(scope, transaction);
        }

        public async ValueTask DisposeAsync()
        {
            await _transaction.RollbackAsync();
            await _transaction.DisposeAsync();
            await _scope.DisposeAsync();
        }
    }

    // ---------------- Database enforcement ----------------

    [Fact]
    public async Task Database_AllowsOneActiveRoutePerSiteAndPerTenantDefault_ButKeepsInactiveHistory()
    {
        var world = await WorldAsync();

        await WithDbAsync(async db =>
        {
            var first = ApprovalRoute.Create(world.TenantId, "ELECTRICAL", null);
            db.ApprovalRoutes.Add(first);
            await db.SaveChangesAsync();

            db.ApprovalRoutes.Add(ApprovalRoute.Create(world.TenantId, "ELECTRICAL", null));
            var duplicateDefault = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Equal(UniqueIndexViolation, ((SqlException)duplicateDefault.InnerException!).Number);
            Assert.Contains("UQ_approval_route_active_default", duplicateDefault.InnerException!.Message);
        });

        await WithDbAsync(async db =>
        {
            var existing = await db.ApprovalRoutes.SingleAsync(route => route.TenantId == world.TenantId && route.SiteId == null);
            existing.Deactivate();
            db.ApprovalRoutes.Add(ApprovalRoute.Create(world.TenantId, "ELECTRICAL", null));
            db.ApprovalRoutes.Add(ApprovalRoute.Create(world.TenantId, "ELECTRICAL", world.SiteA1.Id));
            await db.SaveChangesAsync();

            db.ApprovalRoutes.Add(ApprovalRoute.Create(world.TenantId, "ELECTRICAL", world.SiteA1.Id));
            var duplicateSite = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Contains("UQ_approval_route_active_site", duplicateSite.InnerException!.Message);
        });

        Assert.Equal(3, await WithDbAsync(db => db.ApprovalRoutes.CountAsync(route => route.TenantId == world.TenantId)));
    }

    [Fact]
    public async Task Database_RejectsInvalidStepsAndApprovalRows()
    {
        var world = await WorldAsync();
        var admin = await AdministratorAsync(world.TenantId);
        var requester = await UserAsync(world.TenantId, [world.SiteA1], RoleCodes.Requester);
        var otherTenantUser = await UserAsync(world.OtherTenantId, [], RoleCodes.Approver);
        await UserAsync(world.TenantId, [world.SiteA1], RoleCodes.Approver);
        var routeId = await CreateRouteAsync(admin, null);
        var legacy = await SeedLegacySubmittedAsync(requester, world.SiteA1, "RR-2026-900003");
        var tenantId = world.TenantId;

        async Task<SqlException> RejectedAsync(FormattableString sql) =>
            await Assert.ThrowsAsync<SqlException>(() => WithDbAsync(db => db.Database.ExecuteSqlAsync(sql)));

        var nonApproverRole = await RejectedAsync($"""
            INSERT INTO [approval_route_step] ([approval_route_id], [step_no], [tenant_id], [approver_role_code], [approver_user_id])
            VALUES ({routeId}, 2, {tenantId}, 'COORDINATOR', NULL)
            """);
        Assert.Equal(CheckOrForeignKeyViolation, nonApproverRole.Number);
        Assert.Contains("CK_approval_route_step_approver_role_code", nonApproverRole.Message);

        var crossTenantApprover = await RejectedAsync($"""
            INSERT INTO [approval_route_step] ([approval_route_id], [step_no], [tenant_id], [approver_role_code], [approver_user_id])
            VALUES ({routeId}, 3, {tenantId}, 'APPROVER', {otherTenantUser.UserId})
            """);
        Assert.Contains("FK_approval_route_step_approver_user", crossTenantApprover.Message);

        var neitherAssignedNorFailed = await RejectedAsync($"""
            INSERT INTO [repair_request_approval] ([approval_id], [tenant_id], [repair_request_id], [approval_route_id], [approval_step_no], [status])
            VALUES (NEWID(), {tenantId}, {legacy.Id}, {routeId}, 1, 'PENDING')
            """);
        Assert.Contains("CK_repair_request_approval_assignment", neitherAssignedNorFailed.Message);

        var assignedWithFailure = await RejectedAsync($"""
            INSERT INTO [repair_request_approval] ([approval_id], [tenant_id], [repair_request_id], [approval_route_id], [approval_step_no], [assigned_approver_id], [status], [routing_failure_code])
            VALUES (NEWID(), {tenantId}, {legacy.Id}, {routeId}, 1, {requester.UserId}, 'PENDING', 'APPROVER_NOT_FOUND')
            """);
        Assert.Contains("CK_repair_request_approval_routing_failure", assignedWithFailure.Message);

        // Tenant-composite route and step keys: another tenant's id can never be attached to this tenant's route or step.
        var otherTenantId = world.OtherTenantId;
        var crossTenantStep = await RejectedAsync($"""
            INSERT INTO [approval_route_step] ([approval_route_id], [step_no], [tenant_id], [approver_role_code], [approver_user_id])
            VALUES ({routeId}, 4, {otherTenantId}, 'APPROVER', NULL)
            """);
        Assert.Equal(CheckOrForeignKeyViolation, crossTenantStep.Number);
        Assert.Contains("FK_approval_route_step_approval_route", crossTenantStep.Message);

        var crossTenantApproval = await RejectedAsync($"""
            INSERT INTO [repair_request_approval] ([approval_id], [tenant_id], [repair_request_id], [approval_route_id], [approval_step_no], [status], [routing_failure_code])
            VALUES (NEWID(), {otherTenantId}, {legacy.Id}, {routeId}, 1, 'PENDING', 'APPROVER_NOT_FOUND')
            """);
        Assert.Equal(CheckOrForeignKeyViolation, crossTenantApproval.Number);
        Assert.Contains("FK_repair_request_approval_approval_route", crossTenantApproval.Message);

        Assert.True((await RetryAsync(admin, legacy.Id, legacy.RowVersion)).Succeeded);
        var secondAssignment = await RejectedAsync($"""
            INSERT INTO [repair_request_approval] ([approval_id], [tenant_id], [repair_request_id], [approval_route_id], [approval_step_no], [status], [routing_failure_code])
            VALUES (NEWID(), {tenantId}, {legacy.Id}, {routeId}, 1, 'PENDING', 'APPROVER_NOT_FOUND')
            """);
        Assert.Equal(UniqueIndexViolation, secondAssignment.Number);
        Assert.Contains("UQ_repair_request_approval_request_step", secondAssignment.Message);
    }
}
