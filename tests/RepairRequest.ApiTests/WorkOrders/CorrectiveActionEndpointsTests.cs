using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RepairRequest.Application.Attachments;
using RepairRequest.Domain.Files;
using RepairRequest.Domain.MasterData;
using RepairRequest.Domain.RepairRequests;
using RepairRequest.Domain.Security;
using RepairRequest.Domain.WorkOrders;
using RepairRequest.Infrastructure.Identity;
using RepairRequest.Infrastructure.Persistence;
using RepairRequest.Infrastructure.RepairRequests;

namespace RepairRequest.ApiTests.WorkOrders;

/// <summary>API host with its own disposable LocalDB database for the Corrective Action endpoint tests.</summary>
public sealed class CorrectiveActionApiFactory : AuthApiFactory
{
    public override string ConnectionString =>
        "Server=(localdb)\\MSSQLLocalDB;Database=RepairRequestDb_CorrectiveActionApiTest;Trusted_Connection=True;TrustServerCertificate=True";
}

/// <summary>
/// Corrective Action Submit Plan (ST-CA-002; CA-API-001; Team Lead), Approve Plan (ST-CA-003; CA-API-002;
/// Supervisor) and Schedule Rework (CA-API-003; Coordinator) end to end, plus the minimum read model
/// (`correctiveActionId`/`correctiveActionStatus`/`correctiveServiceVisitId` on `WorkOrderResponse`) — `docs/13`
/// §4.21 (Ticket 6) and §4.22 (CA-API-003). If-Match on Submit/Approve Plan is checked against the linked Work
/// Order's own RowVersion; Schedule Rework's is checked against the Corrective Action's own RowVersion instead
/// (see its own remarks). No Separation of Duties guards Approve Plan (Decision d). Schedule Rework's own scope
/// boundary (§4.22): no Technician Check-in, no rework completion, no re-acceptance, no Service Report, no Time
/// Correction, no Visit-scheduling-time overlap guard (baseline-silent, not baseline-permitting — see §4.22).
/// </summary>
public sealed class CorrectiveActionEndpointsTests : IClassFixture<CorrectiveActionApiFactory>
{
    private const string Requests = "/api/v1/repair-requests";
    private const string Routes = "/api/v1/approval-routes";
    private const string WorkOrders = "/api/v1/work-orders";
    private const string Visits = "/api/v1/service-visits";
    private const string Sessions = "/api/v1/work-sessions";
    private const string CorrectiveActions = "/api/v1/corrective-actions";
    private const string ValidHash = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";
    private const string StaleETag = "\"AAAAAAAAAAA=\"";

    private readonly CorrectiveActionApiFactory _factory;

    public CorrectiveActionEndpointsTests(CorrectiveActionApiFactory factory)
    {
        _factory = factory;
    }

    private sealed record Caller(Guid UserId, Guid TenantId, string Token);

    /// <summary>A Work Order CORRECTIVE_ACTION_REQUIRED, with a DRAFT Corrective Action already created by Reject (Ticket 3, unchanged), plus a valid plan file in the same tenant.</summary>
    private sealed record Rejected(Guid TenantId, Site Site, Caller Owner, Caller TeamLead, Caller Coordinator, Caller Technician, Guid WorkOrderId, Guid CorrectiveActionId, Guid PlanFileAssetId);

    // ---------------- Submit Plan: success ----------------

    [Fact]
    public async Task SubmitPlan_Success_BindsTheOwner_MovesBothStatuses_AndWritesOneAuditRow()
    {
        var r = await RejectedAsync();
        var teamLead = await CallerAsync(r.TenantId, [r.Site], RoleCodes.TeamLead);
        var before = DateTime.UtcNow.AddSeconds(-2);

        var response = await SendAsync(HttpMethod.Post, $"{CorrectiveActions}/{r.CorrectiveActionId}/submit-plan", teamLead,
            new { planText = "Replace the seal and retest.", planFileAssetId = r.PlanFileAssetId }, await WorkOrderETagAsync(r.WorkOrderId, teamLead));
        var after = DateTime.UtcNow.AddSeconds(2);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal("PENDING_PLAN_APPROVAL", body.GetProperty("status").GetString());
        Assert.Equal(teamLead.UserId, body.GetProperty("ownerTeamLeadId").GetGuid());
        Assert.Equal("Replace the seal and retest.", body.GetProperty("planText").GetString());
        Assert.Equal(r.PlanFileAssetId, body.GetProperty("planFileAssetId").GetGuid());
        Assert.True(body.GetProperty("approvedBy").ValueKind == JsonValueKind.Null);

        var correctiveAction = await WithDbAsync(db => db.CorrectiveActions.AsNoTracking().SingleAsync(item => item.Id == r.CorrectiveActionId));
        Assert.Equal(CorrectiveActionStatus.PendingPlanApproval, correctiveAction.Status);
        Assert.Equal(teamLead.UserId, correctiveAction.OwnerTeamLeadId);

        var workOrder = await WithDbAsync(db => db.WorkOrders.AsNoTracking().SingleAsync(item => item.Id == r.WorkOrderId));
        Assert.Equal(WorkOrderStatus.CorrectivePlanPending, workOrder.Status);

        var audit = await WithDbAsync(db => db.AuditHistory.AsNoTracking().SingleAsync(a => a.EntityId == r.CorrectiveActionId && a.ActionCode == "CORRECTIVE_ACTION_PLAN_SUBMITTED"));
        Assert.Equal("CORRECTIVE_ACTION", audit.EntityType);
        Assert.Equal("DRAFT", audit.FromState);
        Assert.Equal("PENDING_PLAN_APPROVAL", audit.ToState);
        Assert.Equal(teamLead.UserId, audit.ActorId);
        Assert.InRange(audit.OccurredAt, before, after);
        Assert.Contains(r.WorkOrderId.ToString(), audit.NewValueJson!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SubmitPlan_TrimsThePlanText()
    {
        var r = await RejectedAsync();
        var teamLead = await CallerAsync(r.TenantId, [r.Site], RoleCodes.TeamLead);

        var response = await SendAsync(HttpMethod.Post, $"{CorrectiveActions}/{r.CorrectiveActionId}/submit-plan", teamLead,
            new { planText = "  Replace the seal.  ", planFileAssetId = r.PlanFileAssetId }, await WorkOrderETagAsync(r.WorkOrderId, teamLead));

        Assert.Equal("Replace the seal.", (await JsonAsync(response)).GetProperty("planText").GetString());
    }

    // ---------------- Submit Plan: read model (Decision b) ----------------

    [Fact]
    public async Task GetWorkOrder_ShowsNoCorrectiveActionFields_BeforeAnyReject()
    {
        var c = await CheckedOutAndSummarySubmittedAsync();
        var body = await JsonAsync(await SendAsync(HttpMethod.Get, $"{WorkOrders}/{c.WorkOrderId}", c.Owner, null, null));

        Assert.True(body.GetProperty("correctiveActionId").ValueKind == JsonValueKind.Null);
        Assert.True(body.GetProperty("correctiveActionStatus").ValueKind == JsonValueKind.Null);
        Assert.True(body.GetProperty("correctiveServiceVisitId").ValueKind == JsonValueKind.Null);
        Assert.True(body.GetProperty("correctiveActionRowVersion").ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task GetWorkOrder_ShowsCorrectiveActionIdAndStatus_AsTheyProgress()
    {
        var r = await RejectedAsync();
        var teamLead = await CallerAsync(r.TenantId, [r.Site], RoleCodes.TeamLead);
        var supervisor = await CallerAsync(r.TenantId, [r.Site], RoleCodes.Supervisor);

        var afterReject = await JsonAsync(await SendAsync(HttpMethod.Get, $"{WorkOrders}/{r.WorkOrderId}", teamLead, null, null));
        Assert.Equal(r.CorrectiveActionId, afterReject.GetProperty("correctiveActionId").GetGuid());
        Assert.Equal("DRAFT", afterReject.GetProperty("correctiveActionStatus").GetString());

        var submitted = await SendAsync(HttpMethod.Post, $"{CorrectiveActions}/{r.CorrectiveActionId}/submit-plan", teamLead,
            new { planText = "Plan.", planFileAssetId = r.PlanFileAssetId }, await WorkOrderETagAsync(r.WorkOrderId, teamLead));
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);

        var afterSubmit = await JsonAsync(await SendAsync(HttpMethod.Get, $"{WorkOrders}/{r.WorkOrderId}", teamLead, null, null));
        Assert.Equal("PENDING_PLAN_APPROVAL", afterSubmit.GetProperty("correctiveActionStatus").GetString());

        var approved = await SendAsync(HttpMethod.Post, $"{CorrectiveActions}/{r.CorrectiveActionId}/approve-plan", supervisor, null, await WorkOrderETagAsync(r.WorkOrderId, supervisor));
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);

        var afterApprove = await JsonAsync(await SendAsync(HttpMethod.Get, $"{WorkOrders}/{r.WorkOrderId}", teamLead, null, null));
        Assert.Equal("APPROVED", afterApprove.GetProperty("correctiveActionStatus").GetString());
        // Never the plan's own content, even after it is approved (§4.21 Decision b's own boundary).
        Assert.False(afterApprove.TryGetProperty("planText", out _));
    }

    // ---------------- Submit Plan: authorization / scope ----------------

    [Theory]
    [InlineData(RoleCodes.Requester)]
    [InlineData(RoleCodes.Approver)]
    [InlineData(RoleCodes.Coordinator)]
    [InlineData(RoleCodes.Technician)]
    [InlineData(RoleCodes.Supervisor)]
    [InlineData(RoleCodes.Administrator)]
    public async Task SubmitPlan_ByAnyoneWithoutTheTeamLeadRole_Get403_AndNothingChanges(string role)
    {
        var r = await RejectedAsync();
        var caller = await CallerAsync(r.TenantId, [r.Site], role);

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{CorrectiveActions}/{r.CorrectiveActionId}/submit-plan", caller,
                new { planText = "Plan.", planFileAssetId = r.PlanFileAssetId }, StaleETag),
            HttpStatusCode.Forbidden, "ACCESS_DENIED");

        await AssertStillDraftAsync(r.CorrectiveActionId);
    }

    [Fact]
    public async Task SubmitPlan_ByATeamLeadOfAnotherTenant_Get404()
    {
        var r = await RejectedAsync();
        var foreign = await CallerAsync(Guid.NewGuid(), [], RoleCodes.TeamLead);

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{CorrectiveActions}/{r.CorrectiveActionId}/submit-plan", foreign,
                new { planText = "Plan.", planFileAssetId = r.PlanFileAssetId }, StaleETag),
            HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task SubmitPlan_ByATeamLeadWithNoSiteScopeOnThisWorkOrder_Get404()
    {
        var r = await RejectedAsync();
        var outOfScope = await CallerAsync(r.TenantId, [], RoleCodes.TeamLead);

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{CorrectiveActions}/{r.CorrectiveActionId}/submit-plan", outOfScope,
                new { planText = "Plan.", planFileAssetId = r.PlanFileAssetId }, StaleETag),
            HttpStatusCode.NotFound, "NOT_FOUND");
        await AssertStillDraftAsync(r.CorrectiveActionId);
    }

    [Fact]
    public async Task SubmitPlan_OfAnUnknownCorrectiveAction_Get404()
    {
        var r = await RejectedAsync();
        var teamLead = await CallerAsync(r.TenantId, [r.Site], RoleCodes.TeamLead);

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{CorrectiveActions}/{Guid.NewGuid()}/submit-plan", teamLead,
                new { planText = "Plan.", planFileAssetId = r.PlanFileAssetId }, StaleETag),
            HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task SubmitPlan_WithoutIfMatch_Returns400_AndWithoutAToken_Returns401()
    {
        var r = await RejectedAsync();
        var teamLead = await CallerAsync(r.TenantId, [r.Site], RoleCodes.TeamLead);
        var etag = await WorkOrderETagAsync(r.WorkOrderId, teamLead);
        var body = new { planText = "Plan.", planFileAssetId = r.PlanFileAssetId };

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{CorrectiveActions}/{r.CorrectiveActionId}/submit-plan", teamLead, body, null),
            HttpStatusCode.BadRequest, "BAD_REQUEST");
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await SendAsync(HttpMethod.Post, $"{CorrectiveActions}/{r.CorrectiveActionId}/submit-plan", null, body, etag)).StatusCode);
        await AssertStillDraftAsync(r.CorrectiveActionId);
    }

    // ---------------- Submit Plan: validation ----------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SubmitPlan_WithoutAPlanText_Returns422_AndWritesNothing(string? planText)
    {
        var r = await RejectedAsync();
        var teamLead = await CallerAsync(r.TenantId, [r.Site], RoleCodes.TeamLead);

        var body = await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{CorrectiveActions}/{r.CorrectiveActionId}/submit-plan", teamLead,
                new { planText, planFileAssetId = r.PlanFileAssetId }, await WorkOrderETagAsync(r.WorkOrderId, teamLead)),
            HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
        Assert.True(body.GetProperty("errors").TryGetProperty("planText", out _));
        await AssertStillDraftAsync(r.CorrectiveActionId);
    }

    [Fact]
    public async Task SubmitPlan_WithoutAPlanFileAssetId_Returns422_AndWritesNothing()
    {
        var r = await RejectedAsync();
        var teamLead = await CallerAsync(r.TenantId, [r.Site], RoleCodes.TeamLead);

        var body = await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{CorrectiveActions}/{r.CorrectiveActionId}/submit-plan", teamLead,
                new { planText = "Plan.", planFileAssetId = (Guid?)null }, await WorkOrderETagAsync(r.WorkOrderId, teamLead)),
            HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
        Assert.True(body.GetProperty("errors").TryGetProperty("planFileAssetId", out _));
        await AssertStillDraftAsync(r.CorrectiveActionId);
    }

    [Fact]
    public async Task SubmitPlan_WithAPlanFileFromAnotherTenant_Returns422_AndWritesNothing()
    {
        var r = await RejectedAsync();
        var teamLead = await CallerAsync(r.TenantId, [r.Site], RoleCodes.TeamLead);
        var foreignTenantId = Guid.NewGuid();
        var foreignUser = await CallerAsync(foreignTenantId, [], RoleCodes.TeamLead);
        var foreignFileId = await WithDbAsync(async db =>
        {
            var file = new FileAsset(foreignTenantId, "plan.pdf", "application/pdf", 512, ValidHash, $"{foreignTenantId:N}/{Guid.NewGuid():N}", foreignUser.UserId, DateTime.UtcNow);
            file.MarkClean();
            db.FileAssets.Add(file);
            await db.SaveChangesAsync();
            return file.Id;
        });

        var body = await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{CorrectiveActions}/{r.CorrectiveActionId}/submit-plan", teamLead,
                new { planText = "Plan.", planFileAssetId = foreignFileId }, await WorkOrderETagAsync(r.WorkOrderId, teamLead)),
            HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
        Assert.True(body.GetProperty("errors").TryGetProperty("planFileAssetId", out _));
        await AssertStillDraftAsync(r.CorrectiveActionId);
    }

    [Fact]
    public async Task SubmitPlan_WithAPlanFileStillPendingScan_Returns422_AndWritesNothing()
    {
        // UC-WO-023 Preconditions: "Plan text + CLEAN evidence" (docs/04) — PENDING is not yet usable.
        var r = await RejectedAsync();
        var teamLead = await CallerAsync(r.TenantId, [r.Site], RoleCodes.TeamLead);
        var pendingFileId = await WithDbAsync(async db =>
        {
            var file = new FileAsset(r.TenantId, "plan.pdf", "application/pdf", 512, ValidHash, $"{r.TenantId:N}/{Guid.NewGuid():N}", teamLead.UserId, DateTime.UtcNow);
            // Deliberately left PENDING — no MarkClean()/MarkFailed() call.
            db.FileAssets.Add(file);
            await db.SaveChangesAsync();
            return file.Id;
        });

        var body = await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{CorrectiveActions}/{r.CorrectiveActionId}/submit-plan", teamLead,
                new { planText = "Plan.", planFileAssetId = pendingFileId }, await WorkOrderETagAsync(r.WorkOrderId, teamLead)),
            HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
        Assert.True(body.GetProperty("errors").TryGetProperty("planFileAssetId", out _));
        await AssertStillDraftAsync(r.CorrectiveActionId);
    }

    [Fact]
    public async Task SubmitPlan_WithAnInfectedPlanFile_Returns422_AndWritesNothing()
    {
        // UC-WO-023 Preconditions: "Plan text + CLEAN evidence" — FAILED (infected) is never usable.
        var r = await RejectedAsync();
        var teamLead = await CallerAsync(r.TenantId, [r.Site], RoleCodes.TeamLead);
        var failedFileId = await WithDbAsync(async db =>
        {
            var file = new FileAsset(r.TenantId, "plan.pdf", "application/pdf", 512, ValidHash, $"{r.TenantId:N}/{Guid.NewGuid():N}", teamLead.UserId, DateTime.UtcNow);
            file.MarkFailed();
            db.FileAssets.Add(file);
            await db.SaveChangesAsync();
            return file.Id;
        });

        var body = await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{CorrectiveActions}/{r.CorrectiveActionId}/submit-plan", teamLead,
                new { planText = "Plan.", planFileAssetId = failedFileId }, await WorkOrderETagAsync(r.WorkOrderId, teamLead)),
            HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
        Assert.True(body.GetProperty("errors").TryGetProperty("planFileAssetId", out _));
        await AssertStillDraftAsync(r.CorrectiveActionId);
    }

    [Fact]
    public async Task SubmitPlan_WithAPlanFileThatDoesNotExist_Returns422_AndWritesNothing()
    {
        var r = await RejectedAsync();
        var teamLead = await CallerAsync(r.TenantId, [r.Site], RoleCodes.TeamLead);

        var body = await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{CorrectiveActions}/{r.CorrectiveActionId}/submit-plan", teamLead,
                new { planText = "Plan.", planFileAssetId = Guid.NewGuid() }, await WorkOrderETagAsync(r.WorkOrderId, teamLead)),
            HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
        Assert.True(body.GetProperty("errors").TryGetProperty("planFileAssetId", out _));
        await AssertStillDraftAsync(r.CorrectiveActionId);
    }

    // ---------------- Submit Plan: state / concurrency ----------------

    [Fact]
    public async Task SubmitPlan_WithAStaleRowVersion_Returns409ConcurrencyConflict_AndWritesNothing()
    {
        var r = await RejectedAsync();
        var teamLead = await CallerAsync(r.TenantId, [r.Site], RoleCodes.TeamLead);

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{CorrectiveActions}/{r.CorrectiveActionId}/submit-plan", teamLead,
                new { planText = "Plan.", planFileAssetId = r.PlanFileAssetId }, StaleETag),
            HttpStatusCode.Conflict, "CONCURRENCY_CONFLICT");
        await AssertStillDraftAsync(r.CorrectiveActionId);
    }

    [Fact]
    public async Task ASecondSubmitPlan_Returns409StateConflict_AndKeepsTheFirstPlan()
    {
        var r = await RejectedAsync();
        var teamLead = await CallerAsync(r.TenantId, [r.Site], RoleCodes.TeamLead);
        var first = await SendAsync(HttpMethod.Post, $"{CorrectiveActions}/{r.CorrectiveActionId}/submit-plan", teamLead,
            new { planText = "First plan.", planFileAssetId = r.PlanFileAssetId }, await WorkOrderETagAsync(r.WorkOrderId, teamLead));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var freshETag = first.Headers.ETag!.Tag;

        var otherTeamLead = await CallerAsync(r.TenantId, [r.Site], RoleCodes.TeamLead);
        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{CorrectiveActions}/{r.CorrectiveActionId}/submit-plan", otherTeamLead,
                new { planText = "Second plan.", planFileAssetId = r.PlanFileAssetId }, freshETag),
            HttpStatusCode.Conflict, "STATE_CONFLICT");

        var correctiveAction = await WithDbAsync(db => db.CorrectiveActions.AsNoTracking().SingleAsync(item => item.Id == r.CorrectiveActionId));
        Assert.Equal("First plan.", correctiveAction.PlanText);
        Assert.Equal(teamLead.UserId, correctiveAction.OwnerTeamLeadId);
        Assert.Equal(1, await WithDbAsync(db => db.AuditHistory.AsNoTracking().CountAsync(a => a.EntityId == r.CorrectiveActionId && a.ActionCode == "CORRECTIVE_ACTION_PLAN_SUBMITTED")));
    }

    [Fact]
    public async Task TwoConcurrentSubmitPlan_ExactlyOneSucceeds_NoDuplicateAudit()
    {
        var r = await RejectedAsync();
        var teamLead = await CallerAsync(r.TenantId, [r.Site], RoleCodes.TeamLead);
        var etag = await WorkOrderETagAsync(r.WorkOrderId, teamLead);
        var body = new { planText = "Plan.", planFileAssetId = r.PlanFileAssetId };

        var responses = await Task.WhenAll(
            Task.Run(() => SendAsync(HttpMethod.Post, $"{CorrectiveActions}/{r.CorrectiveActionId}/submit-plan", teamLead, body, etag)),
            Task.Run(() => SendAsync(HttpMethod.Post, $"{CorrectiveActions}/{r.CorrectiveActionId}/submit-plan", teamLead, body, etag)));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal(1, await WithDbAsync(db => db.AuditHistory.AsNoTracking().CountAsync(a => a.EntityId == r.CorrectiveActionId && a.ActionCode == "CORRECTIVE_ACTION_PLAN_SUBMITTED")));
        var workOrder = await WithDbAsync(db => db.WorkOrders.AsNoTracking().SingleAsync(item => item.Id == r.WorkOrderId));
        Assert.Equal(WorkOrderStatus.CorrectivePlanPending, workOrder.Status);
    }

    // ---------------- Approve Plan: success ----------------

    [Fact]
    public async Task ApprovePlan_Success_Approves_AndWritesOneAuditRow()
    {
        var r = await RejectedAsync();
        var teamLead = await CallerAsync(r.TenantId, [r.Site], RoleCodes.TeamLead);
        var supervisor = await CallerAsync(r.TenantId, [r.Site], RoleCodes.Supervisor);
        var submitted = await SendAsync(HttpMethod.Post, $"{CorrectiveActions}/{r.CorrectiveActionId}/submit-plan", teamLead,
            new { planText = "Plan.", planFileAssetId = r.PlanFileAssetId }, await WorkOrderETagAsync(r.WorkOrderId, teamLead));
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
        var before = DateTime.UtcNow.AddSeconds(-2);

        var response = await SendAsync(HttpMethod.Post, $"{CorrectiveActions}/{r.CorrectiveActionId}/approve-plan", supervisor, null, await WorkOrderETagAsync(r.WorkOrderId, supervisor));
        var after = DateTime.UtcNow.AddSeconds(2);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal("APPROVED", body.GetProperty("status").GetString());
        Assert.Equal(supervisor.UserId, body.GetProperty("approvedBy").GetGuid());

        var correctiveAction = await WithDbAsync(db => db.CorrectiveActions.AsNoTracking().SingleAsync(item => item.Id == r.CorrectiveActionId));
        Assert.Equal(CorrectiveActionStatus.Approved, correctiveAction.Status);
        Assert.Equal(supervisor.UserId, correctiveAction.ApprovedBy);
        Assert.InRange(correctiveAction.ApprovedAt!.Value, before, after);

        var workOrder = await WithDbAsync(db => db.WorkOrders.AsNoTracking().SingleAsync(item => item.Id == r.WorkOrderId));
        Assert.Equal(WorkOrderStatus.CorrectivePlanApproved, workOrder.Status);

        var audit = await WithDbAsync(db => db.AuditHistory.AsNoTracking().SingleAsync(a => a.EntityId == r.CorrectiveActionId && a.ActionCode == "CORRECTIVE_ACTION_PLAN_APPROVED"));
        Assert.Equal("PENDING_PLAN_APPROVAL", audit.FromState);
        Assert.Equal("APPROVED", audit.ToState);
        Assert.Equal(supervisor.UserId, audit.ActorId);
    }

    [Fact]
    public async Task ApprovePlan_BySameUserWhoSubmitted_HoldingBothRoles_Succeeds_BecauseThereIsNoSeparationOfDutiesGuard()
    {
        // `docs/13` §4.21 Decision (d): recorded as a review risk, not enforced.
        var r = await RejectedAsync();
        var multiRole = await CallerAsync(r.TenantId, [r.Site], RoleCodes.TeamLead, RoleCodes.Supervisor);
        var submitted = await SendAsync(HttpMethod.Post, $"{CorrectiveActions}/{r.CorrectiveActionId}/submit-plan", multiRole,
            new { planText = "Plan.", planFileAssetId = r.PlanFileAssetId }, await WorkOrderETagAsync(r.WorkOrderId, multiRole));
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);

        var response = await SendAsync(HttpMethod.Post, $"{CorrectiveActions}/{r.CorrectiveActionId}/approve-plan", multiRole, null, await WorkOrderETagAsync(r.WorkOrderId, multiRole));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(multiRole.UserId, (await JsonAsync(response)).GetProperty("approvedBy").GetGuid());
    }

    // ---------------- Approve Plan: authorization / scope ----------------

    [Theory]
    [InlineData(RoleCodes.Requester)]
    [InlineData(RoleCodes.Approver)]
    [InlineData(RoleCodes.Coordinator)]
    [InlineData(RoleCodes.Technician)]
    [InlineData(RoleCodes.TeamLead)]
    [InlineData(RoleCodes.Administrator)]
    public async Task ApprovePlan_ByAnyoneWithoutTheSupervisorRole_Get403(string role)
    {
        var r = await SubmittedAsync();
        var caller = await CallerAsync(r.TenantId, [r.Site], role);

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{CorrectiveActions}/{r.CorrectiveActionId}/approve-plan", caller, null, StaleETag),
            HttpStatusCode.Forbidden, "ACCESS_DENIED");
    }

    [Fact]
    public async Task ApprovePlan_ByASupervisorOfAnotherTenant_Get404()
    {
        var r = await SubmittedAsync();
        var foreign = await CallerAsync(Guid.NewGuid(), [], RoleCodes.Supervisor);

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{CorrectiveActions}/{r.CorrectiveActionId}/approve-plan", foreign, null, StaleETag),
            HttpStatusCode.NotFound, "NOT_FOUND");
    }

    // ---------------- Approve Plan: state / concurrency ----------------

    [Fact]
    public async Task ApprovePlan_BeforeAnyPlanIsSubmitted_Returns409StateConflict()
    {
        var r = await RejectedAsync();
        var supervisor = await CallerAsync(r.TenantId, [r.Site], RoleCodes.Supervisor);

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{CorrectiveActions}/{r.CorrectiveActionId}/approve-plan", supervisor, null, await WorkOrderETagAsync(r.WorkOrderId, supervisor)),
            HttpStatusCode.Conflict, "STATE_CONFLICT");
    }

    [Fact]
    public async Task ApprovePlan_WithAStaleRowVersion_Returns409ConcurrencyConflict()
    {
        var r = await SubmittedAsync();
        var supervisor = await CallerAsync(r.TenantId, [r.Site], RoleCodes.Supervisor);

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{CorrectiveActions}/{r.CorrectiveActionId}/approve-plan", supervisor, null, StaleETag),
            HttpStatusCode.Conflict, "CONCURRENCY_CONFLICT");
    }

    [Fact]
    public async Task ASecondApprovePlan_Returns409StateConflict_AndWritesNoSecondAudit()
    {
        var r = await SubmittedAsync();
        var supervisor = await CallerAsync(r.TenantId, [r.Site], RoleCodes.Supervisor);
        var first = await SendAsync(HttpMethod.Post, $"{CorrectiveActions}/{r.CorrectiveActionId}/approve-plan", supervisor, null, await WorkOrderETagAsync(r.WorkOrderId, supervisor));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{CorrectiveActions}/{r.CorrectiveActionId}/approve-plan", supervisor, null, first.Headers.ETag!.Tag),
            HttpStatusCode.Conflict, "STATE_CONFLICT");
        Assert.Equal(1, await WithDbAsync(db => db.AuditHistory.AsNoTracking().CountAsync(a => a.EntityId == r.CorrectiveActionId && a.ActionCode == "CORRECTIVE_ACTION_PLAN_APPROVED")));
    }

    [Fact]
    public async Task TwoConcurrentApprovePlan_ExactlyOneSucceeds_NoDuplicateAudit()
    {
        var r = await SubmittedAsync();
        var supervisor = await CallerAsync(r.TenantId, [r.Site], RoleCodes.Supervisor);
        var etag = await WorkOrderETagAsync(r.WorkOrderId, supervisor);

        var responses = await Task.WhenAll(
            Task.Run(() => SendAsync(HttpMethod.Post, $"{CorrectiveActions}/{r.CorrectiveActionId}/approve-plan", supervisor, null, etag)),
            Task.Run(() => SendAsync(HttpMethod.Post, $"{CorrectiveActions}/{r.CorrectiveActionId}/approve-plan", supervisor, null, etag)));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal(1, await WithDbAsync(db => db.AuditHistory.AsNoTracking().CountAsync(a => a.EntityId == r.CorrectiveActionId && a.ActionCode == "CORRECTIVE_ACTION_PLAN_APPROVED")));
    }

    // ---------------- Schedule Rework: success ----------------

    [Fact]
    public async Task ScheduleRework_Success_CreatesAndLinksTheVisit_WritesOneAuditRow_AndKeepsBothStatusesUnchanged()
    {
        var r = await ApprovedAsync();
        var before = DateTime.UtcNow.AddSeconds(-2);

        var response = await SendAsync(HttpMethod.Post, $"{CorrectiveActions}/{r.CorrectiveActionId}/schedule-rework", r.Coordinator,
            ScheduleReworkBody(r), await CorrectiveActionETagAsync(r.WorkOrderId, r.Coordinator));
        var after = DateTime.UtcNow.AddSeconds(2);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal("APPROVED", body.GetProperty("status").GetString());
        var visitId = body.GetProperty("correctiveServiceVisitId").GetGuid();
        Assert.NotEqual(Guid.Empty, visitId);

        var correctiveAction = await WithDbAsync(db => db.CorrectiveActions.AsNoTracking().SingleAsync(item => item.Id == r.CorrectiveActionId));
        Assert.Equal(CorrectiveActionStatus.Approved, correctiveAction.Status);
        Assert.Equal(visitId, correctiveAction.CorrectiveServiceVisitId);

        var visit = await WithDbAsync(db => db.ServiceVisits.AsNoTracking().SingleAsync(item => item.Id == visitId));
        Assert.Equal(r.WorkOrderId, visit.WorkOrderId);
        Assert.Equal(ServiceVisitType.Corrective, visit.VisitType);
        Assert.Equal(ServiceVisitStatus.Scheduled, visit.Status);
        Assert.Equal(r.Technician.UserId, visit.AssignedTechnicianId);

        var workOrder = await WithDbAsync(db => db.WorkOrders.AsNoTracking().SingleAsync(item => item.Id == r.WorkOrderId));
        Assert.Equal(WorkOrderStatus.CorrectivePlanApproved, workOrder.Status);

        var audit = await WithDbAsync(db => db.AuditHistory.AsNoTracking().SingleAsync(a => a.EntityId == r.CorrectiveActionId && a.ActionCode == "CORRECTIVE_ACTION_REWORK_SCHEDULED"));
        Assert.Equal("CORRECTIVE_ACTION", audit.EntityType);
        Assert.Equal("APPROVED", audit.FromState);
        Assert.Equal("APPROVED", audit.ToState);
        Assert.Equal(r.Coordinator.UserId, audit.ActorId);
        Assert.InRange(audit.OccurredAt, before, after);
        Assert.Contains(r.WorkOrderId.ToString(), audit.NewValueJson!, StringComparison.OrdinalIgnoreCase);

        // The ETag is the Corrective Action's own new RowVersion, not the Work Order's (the Work Order's never changed).
        Assert.Equal(Convert.ToBase64String(correctiveAction.RowVersion), response.Headers.ETag!.Tag.Trim('"'));
    }

    [Fact]
    public async Task GetWorkOrder_ShowsCorrectiveServiceVisitId_OnlyOnceReworkIsScheduled()
    {
        var r = await ApprovedAsync();
        var before = await JsonAsync(await SendAsync(HttpMethod.Get, $"{WorkOrders}/{r.WorkOrderId}", r.Owner, null, null));
        Assert.True(before.GetProperty("correctiveServiceVisitId").ValueKind == JsonValueKind.Null);

        var scheduled = await SendAsync(HttpMethod.Post, $"{CorrectiveActions}/{r.CorrectiveActionId}/schedule-rework", r.Coordinator,
            ScheduleReworkBody(r), await CorrectiveActionETagAsync(r.WorkOrderId, r.Coordinator));
        Assert.Equal(HttpStatusCode.OK, scheduled.StatusCode);

        var after = await JsonAsync(await SendAsync(HttpMethod.Get, $"{WorkOrders}/{r.WorkOrderId}", r.Owner, null, null));
        Assert.Equal((await JsonAsync(scheduled)).GetProperty("correctiveServiceVisitId").GetGuid(), after.GetProperty("correctiveServiceVisitId").GetGuid());
    }

    [Fact]
    public async Task GetWorkOrder_ShowsCorrectiveActionRowVersion_AndItRotatesAfterScheduleRework()
    {
        // `docs/13` §4.22: the only way an Angular client can learn Schedule Rework's own If-Match token before
        // its first call — mirrors `costSummaryRowVersion`'s identical purpose.
        var r = await ApprovedAsync();
        var beforeEtag = await CorrectiveActionETagAsync(r.WorkOrderId, r.Coordinator);
        Assert.NotNull(beforeEtag);

        var scheduled = await SendAsync(HttpMethod.Post, $"{CorrectiveActions}/{r.CorrectiveActionId}/schedule-rework", r.Coordinator, ScheduleReworkBody(r), beforeEtag);
        Assert.Equal(HttpStatusCode.OK, scheduled.StatusCode);

        var afterEtag = await CorrectiveActionETagAsync(r.WorkOrderId, r.Coordinator);
        Assert.NotEqual(beforeEtag, afterEtag);
        Assert.Equal(scheduled.Headers.ETag!.Tag, afterEtag);
    }

    // ---------------- Schedule Rework: authorization / scope ----------------

    [Theory]
    [InlineData(RoleCodes.Requester)]
    [InlineData(RoleCodes.Approver)]
    [InlineData(RoleCodes.Technician)]
    [InlineData(RoleCodes.TeamLead)]
    [InlineData(RoleCodes.Supervisor)]
    [InlineData(RoleCodes.Administrator)]
    public async Task ScheduleRework_ByAnyoneWithoutTheCoordinatorRole_Get403_AndNothingChanges(string role)
    {
        var r = await ApprovedAsync();
        var caller = await CallerAsync(r.TenantId, [r.Site], role);

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{CorrectiveActions}/{r.CorrectiveActionId}/schedule-rework", caller, ScheduleReworkBody(r), StaleETag),
            HttpStatusCode.Forbidden, "ACCESS_DENIED");

        await AssertNoReworkScheduledAsync(r.CorrectiveActionId);
    }

    [Fact]
    public async Task ScheduleRework_ByACoordinatorOfAnotherTenant_Get404()
    {
        var r = await ApprovedAsync();
        var foreign = await CallerAsync(Guid.NewGuid(), [], RoleCodes.Coordinator);

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{CorrectiveActions}/{r.CorrectiveActionId}/schedule-rework", foreign, ScheduleReworkBody(r), StaleETag),
            HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task ScheduleRework_ByACoordinatorWithNoSiteScopeOnThisWorkOrder_Get404()
    {
        var r = await ApprovedAsync();
        var outOfScope = await CallerAsync(r.TenantId, [], RoleCodes.Coordinator);

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{CorrectiveActions}/{r.CorrectiveActionId}/schedule-rework", outOfScope, ScheduleReworkBody(r), StaleETag),
            HttpStatusCode.NotFound, "NOT_FOUND");
        await AssertNoReworkScheduledAsync(r.CorrectiveActionId);
    }

    [Fact]
    public async Task ScheduleRework_OfAnUnknownCorrectiveAction_Get404()
    {
        var r = await ApprovedAsync();

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{CorrectiveActions}/{Guid.NewGuid()}/schedule-rework", r.Coordinator, ScheduleReworkBody(r), StaleETag),
            HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task ScheduleRework_WithoutIfMatch_Returns400_AndWithoutAToken_Returns401()
    {
        var r = await ApprovedAsync();
        var etag = await CorrectiveActionETagAsync(r.WorkOrderId, r.Coordinator);
        var body = ScheduleReworkBody(r);

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{CorrectiveActions}/{r.CorrectiveActionId}/schedule-rework", r.Coordinator, body, null),
            HttpStatusCode.BadRequest, "BAD_REQUEST");
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await SendAsync(HttpMethod.Post, $"{CorrectiveActions}/{r.CorrectiveActionId}/schedule-rework", null, body, etag)).StatusCode);
        await AssertNoReworkScheduledAsync(r.CorrectiveActionId);
    }

    // ---------------- Schedule Rework: validation ----------------

    [Fact]
    public async Task ScheduleRework_WithoutATeam_Returns422_AndWritesNothing()
    {
        var r = await ApprovedAsync();

        var body = await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{CorrectiveActions}/{r.CorrectiveActionId}/schedule-rework", r.Coordinator,
                new { assignedTeamId = (Guid?)null, assignedTechnicianId = r.Technician.UserId, scheduledStartAt = "2026-10-05T08:00:00Z", scheduledEndAt = "2026-10-05T10:00:00Z" },
                await CorrectiveActionETagAsync(r.WorkOrderId, r.Coordinator)),
            HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
        Assert.True(body.GetProperty("errors").TryGetProperty("assignedTeamId", out _));
        await AssertNoReworkScheduledAsync(r.CorrectiveActionId);
    }

    [Fact]
    public async Task ScheduleRework_WithAnEndBeforeTheStart_Returns422_AndWritesNothing()
    {
        var r = await ApprovedAsync();

        var body = await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{CorrectiveActions}/{r.CorrectiveActionId}/schedule-rework", r.Coordinator,
                new { assignedTeamId = Guid.NewGuid(), assignedTechnicianId = r.Technician.UserId, scheduledStartAt = "2026-10-05T10:00:00Z", scheduledEndAt = "2026-10-05T08:00:00Z" },
                await CorrectiveActionETagAsync(r.WorkOrderId, r.Coordinator)),
            HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
        Assert.True(body.GetProperty("errors").TryGetProperty("scheduledEndAt", out _));
        await AssertNoReworkScheduledAsync(r.CorrectiveActionId);
    }

    [Fact]
    public async Task ScheduleRework_TechnicianOutsideTheWorkOrdersSite_Returns422_AndWritesNothing()
    {
        var r = await ApprovedAsync();
        var outsideSite = await SiteAsync(r.TenantId);
        var outsideTechnician = await CallerAsync(r.TenantId, [outsideSite], RoleCodes.Technician);

        var body = await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{CorrectiveActions}/{r.CorrectiveActionId}/schedule-rework", r.Coordinator,
                new { assignedTeamId = Guid.NewGuid(), assignedTechnicianId = outsideTechnician.UserId, scheduledStartAt = "2026-10-05T08:00:00Z", scheduledEndAt = "2026-10-05T10:00:00Z" },
                await CorrectiveActionETagAsync(r.WorkOrderId, r.Coordinator)),
            HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
        Assert.True(body.GetProperty("errors").TryGetProperty("assignedTechnicianId", out _));
        await AssertNoReworkScheduledAsync(r.CorrectiveActionId);
    }

    [Fact]
    public async Task ScheduleRework_NonTechnicianAssignee_Returns422_AndWritesNothing()
    {
        var r = await ApprovedAsync();

        var body = await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{CorrectiveActions}/{r.CorrectiveActionId}/schedule-rework", r.Coordinator,
                new { assignedTeamId = Guid.NewGuid(), assignedTechnicianId = r.Owner.UserId, scheduledStartAt = "2026-10-05T08:00:00Z", scheduledEndAt = "2026-10-05T10:00:00Z" },
                await CorrectiveActionETagAsync(r.WorkOrderId, r.Coordinator)),
            HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
        Assert.True(body.GetProperty("errors").TryGetProperty("assignedTechnicianId", out _));
        await AssertNoReworkScheduledAsync(r.CorrectiveActionId);
    }

    // ---------------- Schedule Rework: state / concurrency ----------------

    [Fact]
    public async Task ScheduleRework_BeforeThePlanIsApproved_Returns409StateConflict()
    {
        var r = await SubmittedAsync();
        var coordinator = await CallerAsync(r.TenantId, [r.Site], RoleCodes.Coordinator);

        var body = await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{CorrectiveActions}/{r.CorrectiveActionId}/schedule-rework", coordinator,
                new { assignedTeamId = Guid.NewGuid(), assignedTechnicianId = r.Technician.UserId, scheduledStartAt = "2026-10-05T08:00:00Z", scheduledEndAt = "2026-10-05T10:00:00Z" },
                await CorrectiveActionETagAsync(r.WorkOrderId, coordinator)),
            HttpStatusCode.Conflict, "STATE_CONFLICT");
        Assert.Equal("Only an APPROVED Corrective Action can have its rework scheduled.", body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task ScheduleRework_WithAStaleRowVersion_Returns409ConcurrencyConflict_AndWritesNothing()
    {
        var r = await ApprovedAsync();

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{CorrectiveActions}/{r.CorrectiveActionId}/schedule-rework", r.Coordinator, ScheduleReworkBody(r), StaleETag),
            HttpStatusCode.Conflict, "CONCURRENCY_CONFLICT");
        await AssertNoReworkScheduledAsync(r.CorrectiveActionId);
    }

    [Fact]
    public async Task ASecondScheduleRework_Returns409StateConflict_AndKeepsTheFirstVisitLinked_AndWritesNoSecondAudit()
    {
        var r = await ApprovedAsync();
        var first = await SendAsync(HttpMethod.Post, $"{CorrectiveActions}/{r.CorrectiveActionId}/schedule-rework", r.Coordinator,
            ScheduleReworkBody(r), await CorrectiveActionETagAsync(r.WorkOrderId, r.Coordinator));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstVisitId = (await JsonAsync(first)).GetProperty("correctiveServiceVisitId").GetGuid();

        var body = await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{CorrectiveActions}/{r.CorrectiveActionId}/schedule-rework", r.Coordinator, ScheduleReworkBody(r), first.Headers.ETag!.Tag),
            HttpStatusCode.Conflict, "STATE_CONFLICT");
        Assert.Equal("This Corrective Action's rework has already been scheduled.", body.GetProperty("detail").GetString());

        var correctiveAction = await WithDbAsync(db => db.CorrectiveActions.AsNoTracking().SingleAsync(item => item.Id == r.CorrectiveActionId));
        Assert.Equal(firstVisitId, correctiveAction.CorrectiveServiceVisitId);
        Assert.Equal(1, await WithDbAsync(db => db.AuditHistory.AsNoTracking().CountAsync(a => a.EntityId == r.CorrectiveActionId && a.ActionCode == "CORRECTIVE_ACTION_REWORK_SCHEDULED")));
        Assert.Equal(1, await WithDbAsync(db => db.ServiceVisits.AsNoTracking().CountAsync(v => v.WorkOrderId == r.WorkOrderId && v.VisitType == ServiceVisitType.Corrective)));
    }

    [Fact]
    public async Task TwoConcurrentScheduleRework_ExactlyOneSucceeds_WithExactlyOneVisit_AndNoDuplicateAudit()
    {
        // `docs/13` §4.22's own "duplicate-request protection" requirement: a failed action creates neither a
        // Visit nor an audit/state change.
        var r = await ApprovedAsync();
        var etag = await CorrectiveActionETagAsync(r.WorkOrderId, r.Coordinator);
        var body = ScheduleReworkBody(r);

        var responses = await Task.WhenAll(
            Task.Run(() => SendAsync(HttpMethod.Post, $"{CorrectiveActions}/{r.CorrectiveActionId}/schedule-rework", r.Coordinator, body, etag)),
            Task.Run(() => SendAsync(HttpMethod.Post, $"{CorrectiveActions}/{r.CorrectiveActionId}/schedule-rework", r.Coordinator, body, etag)));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal(1, await WithDbAsync(db => db.AuditHistory.AsNoTracking().CountAsync(a => a.EntityId == r.CorrectiveActionId && a.ActionCode == "CORRECTIVE_ACTION_REWORK_SCHEDULED")));
        Assert.Equal(1, await WithDbAsync(db => db.ServiceVisits.AsNoTracking().CountAsync(v => v.WorkOrderId == r.WorkOrderId && v.VisitType == ServiceVisitType.Corrective)));
        var workOrder = await WithDbAsync(db => db.WorkOrders.AsNoTracking().SingleAsync(item => item.Id == r.WorkOrderId));
        Assert.Equal(WorkOrderStatus.CorrectivePlanApproved, workOrder.Status);
    }

    // ---------------- Helpers ----------------

    private async Task AssertStillDraftAsync(Guid correctiveActionId)
    {
        var correctiveAction = await WithDbAsync(db => db.CorrectiveActions.AsNoTracking().SingleAsync(item => item.Id == correctiveActionId));
        Assert.Equal(CorrectiveActionStatus.Draft, correctiveAction.Status);
        Assert.Null(correctiveAction.OwnerTeamLeadId);
        Assert.Equal(0, await WithDbAsync(db => db.AuditHistory.AsNoTracking().CountAsync(a => a.EntityId == correctiveActionId)));
    }

    private async Task<string> WorkOrderETagAsync(Guid workOrderId, Caller caller)
    {
        var response = await SendAsync(HttpMethod.Get, $"{WorkOrders}/{workOrderId}", caller, null, null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return response.Headers.ETag!.Tag;
    }

    /// <summary>
    /// The Corrective Action's own current RowVersion (Schedule Rework's own If-Match token — §4.22), read the
    /// same way the real Angular client would: off `WorkOrderResponse.correctiveActionRowVersion`, not the
    /// database directly — proving that field is actually wired end to end.
    /// </summary>
    private async Task<string> CorrectiveActionETagAsync(Guid workOrderId, Caller caller)
    {
        var body = await JsonAsync(await SendAsync(HttpMethod.Get, $"{WorkOrders}/{workOrderId}", caller, null, null));
        return $"\"{body.GetProperty("correctiveActionRowVersion").GetString()}\"";
    }

    private static object ScheduleReworkBody(Rejected r) => new
    {
        assignedTeamId = Guid.NewGuid(),
        assignedTechnicianId = r.Technician.UserId,
        scheduledStartAt = "2026-10-05T08:00:00Z",
        scheduledEndAt = "2026-10-05T10:00:00Z"
    };

    private async Task AssertNoReworkScheduledAsync(Guid correctiveActionId)
    {
        var correctiveAction = await WithDbAsync(db => db.CorrectiveActions.AsNoTracking().SingleAsync(item => item.Id == correctiveActionId));
        Assert.Null(correctiveAction.CorrectiveServiceVisitId);
        Assert.Equal(0, await WithDbAsync(db => db.AuditHistory.AsNoTracking().CountAsync(a => a.EntityId == correctiveActionId && a.ActionCode == "CORRECTIVE_ACTION_REWORK_SCHEDULED")));
        Assert.Equal(0, await WithDbAsync(db => db.ServiceVisits.AsNoTracking().CountAsync(v => v.WorkOrderId == correctiveAction.WorkOrderId && v.VisitType == ServiceVisitType.Corrective)));
    }

    /// <summary>A Corrective Action with its plan already submitted (PENDING_PLAN_APPROVAL), ready for Approve Plan.</summary>
    private async Task<Rejected> SubmittedAsync()
    {
        var r = await RejectedAsync();
        var teamLead = await CallerAsync(r.TenantId, [r.Site], RoleCodes.TeamLead);
        var submitted = await SendAsync(HttpMethod.Post, $"{CorrectiveActions}/{r.CorrectiveActionId}/submit-plan", teamLead,
            new { planText = "Plan.", planFileAssetId = r.PlanFileAssetId }, await WorkOrderETagAsync(r.WorkOrderId, teamLead));
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
        return r;
    }

    /// <summary>A Corrective Action with its plan already approved (APPROVED), ready for Schedule Rework.</summary>
    private async Task<Rejected> ApprovedAsync()
    {
        var r = await SubmittedAsync();
        var supervisor = await CallerAsync(r.TenantId, [r.Site], RoleCodes.Supervisor);
        var approved = await SendAsync(HttpMethod.Post, $"{CorrectiveActions}/{r.CorrectiveActionId}/approve-plan", supervisor, null, await WorkOrderETagAsync(r.WorkOrderId, supervisor));
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        return r;
    }

    /// <summary>A Work Order CORRECTIVE_ACTION_REQUIRED with its DRAFT Corrective Action, reached through the real Reject flow (Ticket 3, unchanged).</summary>
    private async Task<Rejected> RejectedAsync()
    {
        var c = await CheckedOutAndSummarySubmittedAsync();

        var submittedForAcceptance = await SendAsync(HttpMethod.Post, $"{WorkOrders}/{c.WorkOrderId}/submit-for-acceptance", c.TeamLead,
            new { acceptanceContactId = c.Owner.UserId }, c.WorkOrderETag);
        Assert.Equal(HttpStatusCode.OK, submittedForAcceptance.StatusCode);
        var awaitingETag = $"\"{(await JsonAsync(submittedForAcceptance)).GetProperty("rowVersion").GetString()}\"";

        var rejected = await SendAsync(HttpMethod.Post, $"{WorkOrders}/{c.WorkOrderId}/reject", c.Owner, new { decisionReason = "Leak persists." }, awaitingETag);
        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);

        var correctiveActionId = await WithDbAsync(db => db.CorrectiveActions.AsNoTracking().Where(item => item.WorkOrderId == c.WorkOrderId).Select(item => item.Id).SingleAsync());

        var planFileAssetId = await WithDbAsync(async db =>
        {
            var file = new FileAsset(c.TenantId, "plan.pdf", "application/pdf", 512, ValidHash, $"{c.TenantId:N}/{Guid.NewGuid():N}", c.TeamLead.UserId, DateTime.UtcNow);
            file.MarkClean();
            db.FileAssets.Add(file);
            await db.SaveChangesAsync();
            return file.Id;
        });

        return new Rejected(c.TenantId, c.Site, c.Owner, c.TeamLead, c.Coordinator, c.Technician, c.WorkOrderId, correctiveActionId, planFileAssetId);
    }

    private sealed record ReviewReady(Guid TenantId, Site Site, Caller Owner, Caller TeamLead, Caller Coordinator, Caller Technician, Guid WorkOrderId, string WorkOrderETag);

    /// <summary>The full flow up to a submitted Work Summary (AWAITING_SUPERVISOR_REVIEW): Schedule, Check-in, Check-out, Submit Work Summary.</summary>
    private async Task<ReviewReady> CheckedOutAndSummarySubmittedAsync()
    {
        var tenantId = Guid.NewGuid();
        var site = await SiteAsync(tenantId);
        var admin = await CallerAsync(tenantId, [], RoleCodes.Administrator);
        var owner = await CallerAsync(tenantId, [site], RoleCodes.Requester);
        var approver = await CallerAsync(tenantId, [site], RoleCodes.Approver);
        var coordinator = await CallerAsync(tenantId, [site], RoleCodes.Coordinator);
        var technician = await CallerAsync(tenantId, [site], RoleCodes.Technician);
        var teamLead = await CallerAsync(tenantId, [site], RoleCodes.TeamLead);

        Assert.Equal(HttpStatusCode.Created, (await SendAsync(HttpMethod.Post, Routes, admin, new { requestCategoryCode = "ELECTRICAL", siteId = site.Id, approverRoleCode = "APPROVER" }, null)).StatusCode);

        var (id, draftETag) = await DraftAsync(owner, site);
        await WithDbAsync(async db =>
        {
            var file = new FileAsset(tenantId, "evidence.png", "image/png", 1024, ValidHash, $"{tenantId:N}/{Guid.NewGuid():N}", owner.UserId, DateTime.UtcNow);
            file.MarkClean();
            db.FileAssets.Add(file);
            db.RepairRequestAttachments.Add(new RepairRequestAttachment(id, file.Id, AttachmentFileRules.AccessScopeCode));
            await db.SaveChangesAsync();
        });

        var submitted = await SendAsync(HttpMethod.Post, $"{Requests}/{id}/submit", owner, new { duplicateContinuationReason = (string?)null }, draftETag);
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);

        var approved = await SendAsync(HttpMethod.Post, $"{Requests}/{id}/approve", approver, null, submitted.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);

        var converted = await SendAsync(HttpMethod.Post, $"{Requests}/{id}/convert-to-work-order", coordinator, null, approved.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.OK, converted.StatusCode);
        var workOrderId = (await JsonAsync(converted)).GetProperty("workOrderId").GetGuid();

        var scheduled = await SendAsync(HttpMethod.Post, $"{WorkOrders}/{workOrderId}/schedule", coordinator, new
        {
            ownerTeamId = Guid.NewGuid(),
            assignedTechnicianId = technician.UserId,
            scheduledStartAt = "2026-10-01T08:00:00Z",
            scheduledEndAt = "2026-10-01T10:00:00Z"
        }, converted.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.OK, scheduled.StatusCode);
        var visit = Assert.Single((await JsonAsync(scheduled)).GetProperty("visits").EnumerateArray());
        var visitId = visit.GetProperty("serviceVisitId").GetGuid();
        var visitETag = $"\"{visit.GetProperty("rowVersion").GetString()}\"";

        var checkedIn = await SendAsync(HttpMethod.Post, $"{Visits}/{visitId}/check-in", technician, null, visitETag);
        Assert.Equal(HttpStatusCode.OK, checkedIn.StatusCode);

        var current = await SendAsync(HttpMethod.Get, $"{Sessions}/current", technician, null, null);
        var currentBody = await JsonAsync(current);
        var sessionId = currentBody.GetProperty("workSessionId").GetGuid();
        var sessionETag = $"\"{currentBody.GetProperty("rowVersion").GetString()}\"";

        var checkedOut = await SendAsync(HttpMethod.Post, $"{Sessions}/{sessionId}/check-out", technician, null, sessionETag);
        Assert.Equal(HttpStatusCode.OK, checkedOut.StatusCode);
        var workOrderETag = $"\"{(await JsonAsync(checkedOut)).GetProperty("workOrderRowVersion").GetString()}\"";

        var summarySubmitted = await SendAsync(HttpMethod.Post, $"{WorkOrders}/{workOrderId}/submit-work-summary", technician,
            new { summaryText = "Replaced the pump seal.", repairOutcomeCode = "REPAIRED" }, workOrderETag);
        Assert.Equal(HttpStatusCode.OK, summarySubmitted.StatusCode);
        var reviewEtag = $"\"{(await JsonAsync(summarySubmitted)).GetProperty("rowVersion").GetString()}\"";

        return new ReviewReady(tenantId, site, owner, teamLead, coordinator, technician, workOrderId, reviewEtag);
    }

    private async Task<(Guid Id, string ETag)> DraftAsync(Caller owner, Site site)
    {
        var created = await SendAsync(HttpMethod.Post, Requests, owner, new
        {
            siteId = site.Id,
            requestCategoryCode = "ELECTRICAL",
            priorityCode = "HIGH",
            requestContactId = owner.UserId,
            description = "Pump leaking",
            preferredStartAt = "2026-09-20T08:00:00Z",
            preferredEndAt = "2026-09-20T10:00:00Z"
        }, null);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return ((await JsonAsync(created)).GetProperty("id").GetGuid(), created.Headers.ETag!.Tag);
    }

    private async Task<Site> SiteAsync(Guid tenantId)
    {
        var site = await WithDbAsync(async db =>
        {
            var customer = new Customer(tenantId, $"CUST-{Guid.NewGuid():N}"[..12]);
            db.Customers.Add(customer);
            var created = new Site(tenantId, customer.Id, "SITE-A");
            db.Sites.Add(created);
            await db.SaveChangesAsync();
            return created;
        });

        await using var scope = _factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<RequestLookupSeeder>().SeedTenantAsync(tenantId, CancellationToken.None);
        return site;
    }

    private async Task<Caller> CallerAsync(Guid tenantId, Site[] sites, params string[] roles)
    {
        var user = await _factory.CreateUserInTenantAsync(tenantId, roles);
        if (sites.Length > 0)
        {
            await WithDbAsync(db =>
            {
                foreach (var site in sites)
                {
                    db.UserSiteScopes.Add(new UserSiteScope(tenantId, user.Id, site.Id));
                }

                return db.SaveChangesAsync();
            });
        }

        var response = await CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { email = user.Email, password = AuthApiFactory.ValidPassword });
        response.EnsureSuccessStatusCode();
        var token = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
        return new Caller(user.Id, tenantId, token);
    }

    private async Task<T> WithDbAsync<T>(Func<RepairRequestDbContext, Task<T>> action)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<RepairRequestDbContext>());
    }

    private async Task WithDbAsync(Func<RepairRequestDbContext, Task> action)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<RepairRequestDbContext>());
    }

    private HttpClient CreateClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, Caller? caller, object? body, string? ifMatch)
    {
        var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
        if (caller is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", caller.Token);
        }

        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return await CreateClient().SendAsync(request);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    private static async Task<JsonElement> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await JsonAsync(response);
        Assert.Equal(code, body.GetProperty("code").GetString());
        Assert.True(body.TryGetProperty("correlationId", out _));
        return body;
    }
}
