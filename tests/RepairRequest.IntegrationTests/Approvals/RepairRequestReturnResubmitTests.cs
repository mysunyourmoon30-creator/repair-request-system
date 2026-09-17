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
/// S1-010 Return for Correction and Resubmit against LocalDB (ST-RR-006, ST-RR-002; DEC-PRE-S1-010-01..04): the returned
/// step stays as decision history and a resubmission is routed into the next approval cycle; Request No, submitted_by and
/// submitted_at never change and no Request No is consumed; the duplicate rule runs again without matching the request
/// itself; competing commands have exactly one winner; failures roll back completely.
/// </summary>
[Collection(PersistenceDatabaseCollection.Name)]
public sealed class RepairRequestReturnResubmitTests : IAsyncLifetime
{
    private const string ValidHash = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";
    private const string ReturnReason = "Photo does not show the fault";
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly DateTime Start = new(2026, 9, 20, 8, 0, 0, DateTimeKind.Utc);

    private readonly AuthenticationTestHost _host = new();

    public RepairRequestReturnResubmitTests(PersistenceDatabaseFixture database)
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

    private static RepairRequestDraftFields Fields(CurrentUser owner, Site site, string description = "Pump leaking") =>
        new(site.Id, null, "ELECTRICAL", "HIGH", owner.UserId, description, Start, Start.AddHours(2));

    private async Task<RepairRequestDraftDto> DraftWithPhotoAsync(CurrentUser owner, Site site)
    {
        RepairRequestDraftDto draft;
        await using (var scope = _host.CreateScope())
        {
            var created = await scope.ServiceProvider.GetRequiredService<RepairRequestDraftService>()
                .CreateAsync(new CommandContext(owner, Guid.NewGuid()), Fields(owner, site), None);
            Assert.True(created.Succeeded);
            draft = created.Value!;
        }

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

    private async Task<CommandResult<RepairRequestDraftDto>> SubmitAsync(CurrentUser owner, Guid requestId, byte[] rowVersion, string? reason = null)
    {
        await using var scope = _host.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<RepairRequestSubmitService>()
            .SubmitAsync(new CommandContext(owner, Guid.NewGuid()), requestId, rowVersion, reason, None);
    }

    private async Task<RepairRequestDraftDto> UnderReviewAsync(World world, string? continuationReason = null)
    {
        var draft = await DraftWithPhotoAsync(world.Requester, world.Scope.SiteA1);
        var submitted = await SubmitAsync(world.Requester, draft.Id, draft.RowVersion, continuationReason);
        Assert.True(submitted.Succeeded);
        Assert.Equal(RepairRequestStatus.UnderReview, submitted.Value!.Status);
        return submitted.Value;
    }

    private async Task<CommandResult<RepairRequestDraftDto>> ReturnAsync(CurrentUser approver, Guid requestId, byte[] rowVersion, string? reason = ReturnReason, AuthenticationTestHost? host = null)
    {
        await using var scope = (host ?? _host).CreateScope();
        return await scope.ServiceProvider.GetRequiredService<RepairRequestDecisionService>()
            .ReturnForCorrectionAsync(new CommandContext(approver, Guid.NewGuid()), requestId, rowVersion, reason, None);
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

    private async Task<CommandResult<RepairRequestDraftDto>> CancelAsync(CurrentUser owner, Guid requestId, byte[] rowVersion)
    {
        await using var scope = _host.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<RepairRequestCancelService>()
            .CancelAsync(new CommandContext(owner, Guid.NewGuid()), requestId, rowVersion, "No longer needed", None);
    }

    private Task<RepairRequestAggregate> StoredAsync(Guid requestId) =>
        WithDbAsync(db => db.RepairRequests.AsNoTracking().SingleAsync(request => request.Id == requestId));

    private Task<List<RepairRequestApproval>> StepsAsync(Guid requestId) =>
        WithDbAsync(db => db.RepairRequestApprovals.AsNoTracking()
            .Where(approval => approval.RepairRequestId == requestId)
            .OrderBy(approval => approval.ApprovalCycleNo)
            .ToListAsync());

    private Task<List<AuditHistory>> AuditsAsync(Guid requestId, params string[] actions) =>
        WithDbAsync(db => db.AuditHistory.AsNoTracking()
            .Where(audit => audit.EntityId == requestId && actions.Contains(audit.ActionCode))
            .OrderBy(audit => audit.OccurredAt)
            .ThenBy(audit => audit.Id)
            .ToListAsync());

    private Task<int> CounterAsync(Guid tenantId) =>
        WithDbAsync(db => db.Database
            .SqlQuery<int>($"SELECT ISNULL(SUM([last_value]), 0) AS [Value] FROM [request_no_counter] WHERE [tenant_id] = {tenantId}")
            .SingleAsync());

    // ---------------- Full loop ----------------

    [Fact]
    public async Task ReturnEditResubmit_RoutesTheNextCycle_KeepsHistoryRequestNoAndSubmitData_AndCanBeApproved()
    {
        var world = await WorldAsync();
        var underReview = await UnderReviewAsync(world);
        var firstStep = Assert.Single(await StepsAsync(underReview.Id));
        var counterBefore = await CounterAsync(world.Scope.TenantId);

        _host.Logs.Clear();
        var returned = await ReturnAsync(world.Approver, underReview.Id, underReview.RowVersion, "  " + ReturnReason + " ");
        var returnCommands = _host.Logs.ExecutedDbCommandCount;

        Assert.True(returned.Succeeded);
        Assert.Equal(RepairRequestStatus.Draft, returned.Value!.Status);
        Assert.Equal(underReview.RequestNo, returned.Value.RequestNo);
        Assert.Equal(underReview.SubmittedAt, returned.Value.SubmittedAt);
        Assert.InRange(returnCommands, 1, 7);

        var returnedStep = Assert.Single(await StepsAsync(underReview.Id));
        Assert.Equal(firstStep.Id, returnedStep.Id);
        Assert.Equal(1, returnedStep.ApprovalCycleNo);
        Assert.Equal(ApprovalStatus.ReturnedForCorrection, returnedStep.Status);
        Assert.Equal(ReturnReason, returnedStep.DecisionReason);
        Assert.NotNull(returnedStep.DecidedAt);
        Assert.Equal(world.Approver.UserId, returnedStep.AssignedApproverId);

        var returnAudit = Assert.Single(await AuditsAsync(underReview.Id, RepairRequestAudit.ReturnedForCorrectionAction));
        Assert.Equal("UNDER_REVIEW", returnAudit.FromState);
        Assert.Equal("DRAFT", returnAudit.ToState);
        Assert.Equal(ReturnReason, returnAudit.Reason);
        Assert.Equal(world.Approver.UserId, returnAudit.ActorId);

        // Edit permission is restored for the owner (ST-RR-006), and attachments may be added while DRAFT.
        await using (var scope = _host.CreateScope())
        {
            var edited = await scope.ServiceProvider.GetRequiredService<RepairRequestDraftService>().UpdateAsync(
                new CommandContext(world.Requester, Guid.NewGuid()), underReview.Id, returned.Value.RowVersion,
                Fields(world.Requester, world.Scope.SiteA1, "Pump leaking at the seal"), None);
            Assert.True(edited.Succeeded);
            Assert.Equal(RepairRequestStatus.Draft, await scope.ServiceProvider.GetRequiredService<IAttachmentStore>()
                .GetOwnRequestStatusAsync(world.Requester, underReview.Id, None));

            var resubmitted = await SubmitAsync(world.Requester, underReview.Id, edited.Value!.RowVersion);

            Assert.True(resubmitted.Succeeded);
            Assert.Equal(RepairRequestStatus.UnderReview, resubmitted.Value!.Status);
            Assert.Equal(underReview.RequestNo, resubmitted.Value.RequestNo);
            Assert.Equal(underReview.SubmittedAt, resubmitted.Value.SubmittedAt);

            var stored = await StoredAsync(underReview.Id);
            Assert.Equal("Pump leaking at the seal", stored.Description);
            Assert.Equal(world.Requester.UserId, stored.SubmittedBy);
            Assert.Equal(counterBefore, await CounterAsync(world.Scope.TenantId));

            var steps = await StepsAsync(underReview.Id);
            Assert.Equal(2, steps.Count);
            Assert.Equal(ApprovalStatus.ReturnedForCorrection, steps[0].Status);
            Assert.Equal(returnedStep.RowVersion, steps[0].RowVersion);
            Assert.Equal(2, steps[1].ApprovalCycleNo);
            Assert.True(steps[1].IsAssignedPending);
            Assert.Equal(world.Approver.UserId, steps[1].AssignedApproverId);

            var approved = await DecideAsync(world.Approver, underReview.Id, resubmitted.Value.RowVersion, approve: true);
            Assert.True(approved.Succeeded);
            Assert.Equal(RepairRequestStatus.Approved, approved.Value!.Status);
        }

        var history = await StepsAsync(underReview.Id);
        Assert.Equal(ApprovalStatus.ReturnedForCorrection, history[0].Status);
        Assert.Equal(ReturnReason, history[0].DecisionReason);
        Assert.Equal(ApprovalStatus.Approved, history[1].Status);

        var timeline = await AuditsAsync(
            underReview.Id,
            RepairRequestAudit.SubmittedAction,
            RepairRequestRoutingAudit.RoutedAction,
            RepairRequestAudit.ReturnedForCorrectionAction,
            RepairRequestAudit.DraftUpdatedAction,
            RepairRequestAudit.ResubmittedAction,
            RepairRequestAudit.ApprovedAction);
        Assert.Equal(
            [
                RepairRequestAudit.SubmittedAction,
                RepairRequestRoutingAudit.RoutedAction,
                RepairRequestAudit.ReturnedForCorrectionAction,
                RepairRequestAudit.DraftUpdatedAction,
                RepairRequestAudit.ResubmittedAction,
                RepairRequestRoutingAudit.RoutedAction,
                RepairRequestAudit.ApprovedAction
            ],
            timeline.Select(audit => audit.ActionCode));
        Assert.Contains("\"approvalCycleNo\":1", timeline[1].NewValueJson);
        Assert.Contains("\"approvalCycleNo\":2", timeline[5].NewValueJson);
        Assert.Equal(world.Requester.UserId, timeline[4].ActorId);
    }

    [Fact]
    public async Task ResubmitWithNoActiveRoute_StaysSubmitted_IsListedForRecovery_AndAdminRetryAssignsTheNextCycle()
    {
        var world = await WorldAsync();
        var underReview = await UnderReviewAsync(world);
        var returned = await ReturnAsync(world.Approver, underReview.Id, underReview.RowVersion);
        await WithDbAsync(db => db.Database.ExecuteSqlAsync($"UPDATE [approval_route] SET [is_active] = 0 WHERE [tenant_id] = {world.Scope.TenantId}"));

        var resubmitted = await SubmitAsync(world.Requester, underReview.Id, returned.Value!.RowVersion);

        Assert.True(resubmitted.Succeeded);
        Assert.Equal(RepairRequestStatus.Submitted, resubmitted.Value!.Status);
        var steps = await StepsAsync(underReview.Id);
        Assert.Equal(ApprovalStatus.ReturnedForCorrection, Assert.Single(steps).Status);
        var failure = (await AuditsAsync(underReview.Id, RepairRequestRoutingAudit.RoutingFailedAction)).Last();
        Assert.Contains("\"approvalCycleNo\":2", failure.NewValueJson);

        await using (var scope = _host.CreateScope())
        {
            var routing = scope.ServiceProvider.GetRequiredService<RepairRequestRoutingService>();
            var issues = await routing.ListIssuesAsync(world.Admin, new PageRequest(1, 50), None);
            Assert.Contains(issues.Items, item => item.RepairRequestId == underReview.Id);

            await CreateRouteAsync(world.Admin, world.Scope.SiteA1);
            var retried = await routing.RetryAsync(new CommandContext(world.Admin, Guid.NewGuid()), underReview.Id, resubmitted.Value.RowVersion, None);
            Assert.True(retried.Succeeded);
            Assert.Equal(RepairRequestStatus.UnderReview, retried.Value!.Status);
        }

        steps = await StepsAsync(underReview.Id);
        Assert.Equal(2, steps.Count);
        Assert.Equal(ApprovalStatus.ReturnedForCorrection, steps[0].Status);
        Assert.Equal(2, steps[1].ApprovalCycleNo);
        Assert.True(steps[1].IsAssignedPending);
    }

    // ---------------- Guards ----------------

    [Fact]
    public async Task ReturnOfASubmittedRequest_IsStateConflict_AndAnUnassignedApproverOrTheOwner_IsDenied()
    {
        var world = await WorldAsync(withRoute: false);
        var draft = await DraftWithPhotoAsync(world.Requester, world.Scope.SiteA1);
        var submitted = (await SubmitAsync(world.Requester, draft.Id, draft.RowVersion)).Value!;
        Assert.Equal(RepairRequestStatus.Submitted, submitted.Status);

        Assert.Equal(CommandFailure.StateConflict, (await ReturnAsync(world.Approver, submitted.Id, submitted.RowVersion)).Error?.Failure);

        await CreateRouteAsync(world.Admin, world.Scope.SiteA1);
        var underReview = await UnderReviewAsync(world, "Second fault at the same Site");
        var otherApprover = await UserAsync(world.Scope.TenantId, [world.Scope.SiteA1], RoleCodes.Approver);
        var ownerAsApprover = new CurrentUser(world.Requester.UserId, world.Scope.TenantId, [RoleCodes.Requester, RoleCodes.Approver]);

        Assert.Equal(CommandFailure.AccessDenied, (await ReturnAsync(otherApprover, underReview.Id, underReview.RowVersion)).Error?.Failure);
        Assert.Equal(CommandFailure.AccessDenied, (await ReturnAsync(ownerAsApprover, underReview.Id, underReview.RowVersion)).Error?.Failure);
        Assert.Equal(CommandFailure.ValidationFailed, (await ReturnAsync(world.Approver, underReview.Id, underReview.RowVersion, "   ")).Error?.Failure);

        Assert.Equal(RepairRequestStatus.UnderReview, (await StoredAsync(underReview.Id)).Status);
        Assert.True(Assert.Single(await StepsAsync(underReview.Id)).IsAssignedPending);
        Assert.Empty(await AuditsAsync(underReview.Id, RepairRequestAudit.ReturnedForCorrectionAction));
        Assert.Empty(await AuditsAsync(submitted.Id, RepairRequestAudit.ReturnedForCorrectionAction));
    }

    [Fact]
    public async Task Resubmit_RerunsTheDuplicateRule_WithoutMatchingItself_AndTheAppliedReasonReplacesTheStoredOne()
    {
        var world = await WorldAsync();
        var underReview = await UnderReviewAsync(world);
        var returned = await ReturnAsync(world.Approver, underReview.Id, underReview.RowVersion);

        // Alone, the returned request never matches its own earlier submission.
        var alone = await SubmitAsync(world.Requester, underReview.Id, returned.Value!.RowVersion);
        Assert.True(alone.Succeeded);
        Assert.Null((await StoredAsync(underReview.Id)).DuplicateContinuationReason);

        var secondTime = await ReturnAsync(world.Approver, underReview.Id, alone.Value!.RowVersion);
        Assert.True(secondTime.Succeeded);

        // Another matching request submitted meanwhile makes the resubmission a duplicate.
        var other = await DraftWithPhotoAsync(world.Requester, world.Scope.SiteA1);
        Assert.True((await SubmitAsync(world.Requester, other.Id, other.RowVersion, "Second pump")).Succeeded);

        var stepsBeforeWarning = (await StepsAsync(underReview.Id)).Count;
        var counterBeforeWarning = await CounterAsync(world.Scope.TenantId);

        var warning = await SubmitAsync(world.Requester, underReview.Id, secondTime.Value!.RowVersion);
        Assert.Equal(CommandFailure.ValidationFailed, warning.Error?.Failure);
        Assert.Equal(1, warning.Error!.DuplicateCount);

        // The warning writes nothing: still DRAFT, same version, no resubmission audit, no new cycle, no Request No.
        var afterWarning = await StoredAsync(underReview.Id);
        Assert.Equal(RepairRequestStatus.Draft, afterWarning.Status);
        Assert.Equal(secondTime.Value.RowVersion, afterWarning.RowVersion);
        Assert.Single(await AuditsAsync(underReview.Id, RepairRequestAudit.ResubmittedAction));
        Assert.Equal(stepsBeforeWarning, (await StepsAsync(underReview.Id)).Count);
        Assert.Equal(counterBeforeWarning, await CounterAsync(world.Scope.TenantId));

        var continued = await SubmitAsync(world.Requester, underReview.Id, secondTime.Value.RowVersion, "Separate leak on the return line");
        Assert.True(continued.Succeeded);
        Assert.Equal("Separate leak on the return line", (await StoredAsync(underReview.Id)).DuplicateContinuationReason);

        var resubmits = await AuditsAsync(underReview.Id, RepairRequestAudit.ResubmittedAction);
        Assert.Equal(2, resubmits.Count);
        Assert.Null(resubmits[0].Reason);
        Assert.Equal("Separate leak on the return line", resubmits[1].Reason);
        Assert.Equal(3, (await StepsAsync(underReview.Id)).Count);
    }

    [Fact]
    public async Task ResubmitRoutesTheCurrentlyEligibleApprover_AndTheCycleOneApproverCannotDecideCycleTwo()
    {
        var world = await WorldAsync();
        var underReview = await UnderReviewAsync(world);
        var returned = await ReturnAsync(world.Approver, underReview.Id, underReview.RowVersion);
        Assert.True(returned.Succeeded);
        var cycleOne = Assert.Single(await StepsAsync(underReview.Id));

        // Routing is evaluated again: the cycle-1 approver is no longer eligible and another approver is.
        var formerApproverId = world.Approver.UserId;
        await WithDbAsync(db => db.Database.ExecuteSqlAsync($"DELETE FROM [user_site_scope] WHERE [user_id] = {formerApproverId}"));
        var currentApprover = await UserAsync(world.Scope.TenantId, [world.Scope.SiteA1], RoleCodes.Approver);

        var resubmitted = await SubmitAsync(world.Requester, underReview.Id, returned.Value!.RowVersion);

        Assert.True(resubmitted.Succeeded);
        Assert.Equal(RepairRequestStatus.UnderReview, resubmitted.Value!.Status);
        var steps = await StepsAsync(underReview.Id);
        Assert.Equal(2, steps.Count);
        Assert.Equal(currentApprover.UserId, steps[1].AssignedApproverId);
        Assert.Equal(2, steps[1].ApprovalCycleNo);

        // The former approver holds APPROVER but is neither assigned nor in scope any more: no decision of any kind.
        var etag = resubmitted.Value.RowVersion;
        Assert.Contains((await DecideAsync(world.Approver, underReview.Id, etag, approve: true)).Error!.Failure, new[] { CommandFailure.AccessDenied, CommandFailure.NotFound });
        Assert.Contains((await DecideAsync(world.Approver, underReview.Id, etag, approve: false)).Error!.Failure, new[] { CommandFailure.AccessDenied, CommandFailure.NotFound });
        Assert.Contains((await ReturnAsync(world.Approver, underReview.Id, etag)).Error!.Failure, new[] { CommandFailure.AccessDenied, CommandFailure.NotFound });

        // Give the former approver Site scope back: they are still not the current-cycle assignee, so 403.
        await WithDbAsync(db => ScopeWorld.AssignSitesAsync(db, world.Scope.TenantId, formerApproverId, [world.Scope.SiteA1]));
        Assert.Equal(CommandFailure.AccessDenied, (await DecideAsync(world.Approver, underReview.Id, etag, approve: true)).Error?.Failure);
        Assert.Equal(CommandFailure.AccessDenied, (await ReturnAsync(world.Approver, underReview.Id, etag)).Error?.Failure);

        var history = (await StepsAsync(underReview.Id))[0];
        Assert.Equal(cycleOne.Id, history.Id);
        Assert.Equal(ApprovalStatus.ReturnedForCorrection, history.Status);
        Assert.Equal(cycleOne.DecisionReason, history.DecisionReason);
        Assert.Equal(cycleOne.DecidedAt, history.DecidedAt);
        Assert.Equal(cycleOne.RowVersion, history.RowVersion);
        Assert.Equal(RepairRequestStatus.UnderReview, (await StoredAsync(underReview.Id)).Status);

        var approved = await DecideAsync(currentApprover, underReview.Id, etag, approve: true);
        Assert.True(approved.Succeeded);
        Assert.Equal(cycleOne.RowVersion, (await StepsAsync(underReview.Id))[0].RowVersion);
    }

    // ---------------- Concurrency ----------------

    [Theory]
    [InlineData("approve")]
    [InlineData("reject")]
    [InlineData("cancel")]
    [InlineData("return")]
    public async Task ReturnRacingAnotherCommandOnUnderReview_ExactlyOneWins(string competitor)
    {
        var world = await WorldAsync();
        var underReview = await UnderReviewAsync(world);

        Task<CommandResult<RepairRequestDraftDto>> Competitor() => competitor switch
        {
            "approve" => DecideAsync(world.Approver, underReview.Id, underReview.RowVersion, approve: true),
            "reject" => DecideAsync(world.Approver, underReview.Id, underReview.RowVersion, approve: false),
            "cancel" => CancelAsync(world.Requester, underReview.Id, underReview.RowVersion),
            _ => ReturnAsync(world.Approver, underReview.Id, underReview.RowVersion, "Duplicate click")
        };

        var results = await Task.WhenAll(
            Task.Run(() => ReturnAsync(world.Approver, underReview.Id, underReview.RowVersion)),
            Task.Run(Competitor));

        var winner = Assert.Single(results, result => result.Succeeded);
        Assert.Equal(CommandFailure.ConcurrencyConflict, Assert.Single(results, result => !result.Succeeded).Error!.Failure);

        var stored = await StoredAsync(underReview.Id);
        Assert.Equal(winner.Value!.Status, stored.Status);
        Assert.Single(await AuditsAsync(
            underReview.Id,
            RepairRequestAudit.ReturnedForCorrectionAction,
            RepairRequestAudit.ApprovedAction,
            RepairRequestAudit.RejectedAction,
            RepairRequestAudit.CancelledAction));
        var step = Assert.Single(await StepsAsync(underReview.Id));
        Assert.Equal(
            stored.Status switch
            {
                RepairRequestStatus.Draft => ApprovalStatus.ReturnedForCorrection,
                RepairRequestStatus.Approved => ApprovalStatus.Approved,
                RepairRequestStatus.Rejected => ApprovalStatus.Rejected,
                _ => ApprovalStatus.Pending
            },
            step.Status);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("resubmit")]
    public async Task ResubmitRacingCancelOrAnotherResubmit_ExactlyOneWins_AndOnlyOneCycleIsRouted(string competitor)
    {
        var world = await WorldAsync();
        var underReview = await UnderReviewAsync(world);
        var returned = (await ReturnAsync(world.Approver, underReview.Id, underReview.RowVersion)).Value!;

        Task<CommandResult<RepairRequestDraftDto>> Competitor() => competitor == "cancel"
            ? CancelAsync(world.Requester, underReview.Id, returned.RowVersion)
            : SubmitAsync(world.Requester, underReview.Id, returned.RowVersion);

        var results = await Task.WhenAll(
            Task.Run(() => SubmitAsync(world.Requester, underReview.Id, returned.RowVersion)),
            Task.Run(Competitor));

        Assert.Single(results, result => result.Succeeded);
        Assert.Equal(CommandFailure.ConcurrencyConflict, Assert.Single(results, result => !result.Succeeded).Error!.Failure);

        var stored = await StoredAsync(underReview.Id);
        var resubmits = await AuditsAsync(underReview.Id, RepairRequestAudit.ResubmittedAction);
        var cancels = await AuditsAsync(underReview.Id, RepairRequestAudit.CancelledAction);
        var steps = await StepsAsync(underReview.Id);
        if (stored.Status == RepairRequestStatus.Cancelled)
        {
            Assert.Empty(resubmits);
            Assert.Single(cancels);
            Assert.Single(steps);
        }
        else
        {
            Assert.Equal(RepairRequestStatus.UnderReview, stored.Status);
            Assert.Single(resubmits);
            Assert.Empty(cancels);
            Assert.Equal(2, steps.Count);
            Assert.Single(steps, step => step.IsAssignedPending);
        }

        Assert.Equal(underReview.RequestNo, stored.RequestNo);
        Assert.Equal(underReview.SubmittedAt, stored.SubmittedAt);
    }

    [Fact]
    public async Task ReturnWhileTheReviewLockIsHeld_IsConcurrencyConflict_AndWritesNothing()
    {
        var world = await WorldAsync();
        var underReview = await UnderReviewAsync(world);
        await using var impatientHost = new AuthenticationTestHost(services =>
            services.AddSingleton(new RepairRequestRoutingLockOptions { TimeoutMilliseconds = 200 }));

        await using (var scope = _host.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RepairRequestDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync();
            var resource = RepairRequestRoutingLock.Resource(underReview.Id);
            var granted = (await db.Database.SqlQuery<int>($"""
                DECLARE @lock_result int;
                EXEC @lock_result = sp_getapplock @Resource = {resource}, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 5000;
                SELECT @lock_result AS [Value];
                """).ToListAsync()).Single();
            Assert.True(granted >= 0);

            var blocked = await ReturnAsync(world.Approver, underReview.Id, underReview.RowVersion, host: impatientHost);
            Assert.Equal(CommandFailure.ConcurrencyConflict, blocked.Error?.Failure);

            await transaction.RollbackAsync();
        }

        Assert.Equal(RepairRequestStatus.UnderReview, (await StoredAsync(underReview.Id)).Status);
        Assert.True(Assert.Single(await StepsAsync(underReview.Id)).IsAssignedPending);
        Assert.Empty(await AuditsAsync(underReview.Id, RepairRequestAudit.ReturnedForCorrectionAction));
        Assert.True((await ReturnAsync(world.Approver, underReview.Id, underReview.RowVersion, host: impatientHost)).Succeeded);
    }

    // ---------------- Transaction and database ----------------

    [Fact]
    public async Task GenuineDatabaseFailure_RollsBackTheReturn_AndReleasesTheLock()
    {
        var world = await WorldAsync();
        var underReview = await UnderReviewAsync(world);
        await using var failingHost = new AuthenticationTestHost(services =>
            services.ConfigureDbContext<RepairRequestDbContext>(options => options.AddInterceptors(new FailReturnSaveInterceptor())));

        var thrown = await Assert.ThrowsAsync<DbUpdateException>(() => ReturnAsync(world.Approver, underReview.Id, underReview.RowVersion, host: failingHost));

        Assert.Equal(50000, Assert.IsType<SqlException>(thrown.InnerException).Number);
        var stored = await StoredAsync(underReview.Id);
        Assert.Equal(RepairRequestStatus.UnderReview, stored.Status);
        Assert.Equal(underReview.RowVersion, stored.RowVersion);
        var step = Assert.Single(await StepsAsync(underReview.Id));
        Assert.True(step.IsAssignedPending);
        Assert.Null(step.DecisionReason);
        Assert.Empty(await AuditsAsync(underReview.Id, RepairRequestAudit.ReturnedForCorrectionAction));
        var resource = RepairRequestRoutingLock.Resource(underReview.Id);
        Assert.Equal(1, await WithDbAsync(db => db.Database
            .SqlQuery<int>($"SELECT APPLOCK_TEST('public', {resource}, 'Exclusive', 'Session') AS [Value]")
            .SingleAsync()));

        Assert.True((await ReturnAsync(world.Approver, underReview.Id, underReview.RowVersion)).Succeeded);
    }

    [Fact]
    public async Task GenuineDatabaseFailureDuringResubmit_RollsBackEverything()
    {
        var world = await WorldAsync();
        var underReview = await UnderReviewAsync(world);
        var returned = (await ReturnAsync(world.Approver, underReview.Id, underReview.RowVersion)).Value!;
        var counter = await CounterAsync(world.Scope.TenantId);
        await using var failingHost = new AuthenticationTestHost(services =>
            services.ConfigureDbContext<RepairRequestDbContext>(options => options.AddInterceptors(new FailReturnSaveInterceptor())));

        await using (var scope = failingHost.CreateScope())
        {
            var thrown = await Assert.ThrowsAsync<DbUpdateException>(() => scope.ServiceProvider.GetRequiredService<RepairRequestSubmitService>()
                .SubmitAsync(new CommandContext(world.Requester, Guid.NewGuid()), underReview.Id, returned.RowVersion, null, None));
            Assert.Equal(50000, Assert.IsType<SqlException>(thrown.InnerException).Number);
        }

        var stored = await StoredAsync(underReview.Id);
        Assert.Equal(RepairRequestStatus.Draft, stored.Status);
        Assert.Equal(returned.RowVersion, stored.RowVersion);
        Assert.Equal(underReview.RequestNo, stored.RequestNo);
        Assert.Equal(underReview.SubmittedAt, stored.SubmittedAt);
        Assert.Equal(counter, await CounterAsync(world.Scope.TenantId));
        Assert.Equal(ApprovalStatus.ReturnedForCorrection, Assert.Single(await StepsAsync(underReview.Id)).Status);
        Assert.Empty(await AuditsAsync(underReview.Id, RepairRequestAudit.ResubmittedAction));

        var resubmitted = await SubmitAsync(world.Requester, underReview.Id, returned.RowVersion);
        Assert.True(resubmitted.Succeeded);
        Assert.Equal(RepairRequestStatus.UnderReview, resubmitted.Value!.Status);
        Assert.Equal(2, (await StepsAsync(underReview.Id)).Count);
    }

    [Fact]
    public async Task Database_AllowsOneApprovalRowPerRequestStepAndCycle()
    {
        var world = await WorldAsync();
        var underReview = await UnderReviewAsync(world);
        var existing = Assert.Single(await StepsAsync(underReview.Id));

        var duplicate = await Assert.ThrowsAsync<DbUpdateException>(() => WithDbAsync(db =>
        {
            db.RepairRequestApprovals.Add(RepairRequestApproval.Assigned(
                world.Scope.TenantId, underReview.Id, existing.ApprovalCycleNo, existing.ApprovalRouteId, existing.ApprovalStepNo, world.Approver.UserId, DateTime.UtcNow));
            return db.SaveChangesAsync();
        }));
        Assert.Contains(Assert.IsType<SqlException>(duplicate.InnerException).Number, new[] { 2601, 2627 });

        await WithDbAsync(db =>
        {
            db.RepairRequestApprovals.Add(RepairRequestApproval.Assigned(
                world.Scope.TenantId, underReview.Id, 2, existing.ApprovalRouteId, existing.ApprovalStepNo, world.Approver.UserId, DateTime.UtcNow));
            return db.SaveChangesAsync();
        });
        Assert.Equal(2, (await StepsAsync(underReview.Id)).Count);
    }

    /// <summary>Turns a Return or Resubmit save batch (each inserts its audit) into a genuine SQL Server error.</summary>
    private sealed class FailReturnSaveInterceptor : DbCommandInterceptor
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
