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

/// <summary>API host with its own disposable LocalDB database for the Work Summary Submit/Review endpoint tests.</summary>
public sealed class WorkSummaryApiFactory : AuthApiFactory
{
    public override string ConnectionString =>
        "Server=(localdb)\\MSSQLLocalDB;Database=RepairRequestDb_WorkSummaryApiTest;Trusted_Connection=True;TrustServerCertificate=True";
}

/// <summary>
/// WSM-API-001 Submit Work Summary (ST-WO-003; UC-WO-020) and WSM-API-002 Submit for Acceptance (ST-WO-004), plus
/// the Work Summary read, end to end (`docs/13` §4.15). Technician submits for their own checked-out Work Order
/// only; Team Lead or Supervisor reviews and submits for acceptance within their Site scope; the Customer
/// Acceptance Contact is never involved in this ticket. If-Match is checked against the Work Order's own
/// RowVersion for both actions.
/// </summary>
public sealed class WorkSummaryEndpointsTests : IClassFixture<WorkSummaryApiFactory>
{
    private const string Requests = "/api/v1/repair-requests";
    private const string Routes = "/api/v1/approval-routes";
    private const string WorkOrders = "/api/v1/work-orders";
    private const string Visits = "/api/v1/service-visits";
    private const string Sessions = "/api/v1/work-sessions";
    private const string ValidHash = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";

    private readonly WorkSummaryApiFactory _factory;

    public WorkSummaryEndpointsTests(WorkSummaryApiFactory factory)
    {
        _factory = factory;
    }

    private sealed record Caller(Guid UserId, Guid TenantId, string Token);

    private sealed record CheckedOut(Guid TenantId, Site Site, Caller TeamLead, Caller Supervisor, Caller Technician, Guid WorkOrderId, Guid ServiceVisitId, string WorkOrderETag);

    // ---------------- Success ----------------

    [Fact]
    public async Task Submit_Success_MovesToAwaitingSupervisorReview_CreatesTheSummary_AndAudits()
    {
        var c = await CheckedOutAsync();
        var before = DateTime.UtcNow.AddSeconds(-2);

        var response = await SendAsync(HttpMethod.Post, $"{WorkOrders}/{c.WorkOrderId}/submit-work-summary", c.Technician,
            new { summaryText = "Replaced the pump seal.", repairOutcomeCode = "REPAIRED" }, c.WorkOrderETag);
        var after = DateTime.UtcNow.AddSeconds(2);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal("AWAITING_SUPERVISOR_REVIEW", body.GetProperty("status").GetString());
        var newETag = $"\"{body.GetProperty("rowVersion").GetString()}\"";
        Assert.Equal(newETag, response.Headers.ETag!.Tag);
        Assert.NotEqual(c.WorkOrderETag, newETag);

        var workOrder = await WithDbAsync(db => db.WorkOrders.AsNoTracking().SingleAsync(w => w.Id == c.WorkOrderId));
        Assert.Equal(WorkOrderStatus.AwaitingSupervisorReview, workOrder.Status);

        var summary = await WithDbAsync(db => db.WorkSummaries.AsNoTracking().SingleAsync(s => s.WorkOrderId == c.WorkOrderId));
        Assert.Equal(c.ServiceVisitId, summary.ServiceVisitId);
        Assert.Equal(1, summary.RevisionNo);
        Assert.Equal("Replaced the pump seal.", summary.SummaryText);
        Assert.Equal(RepairOutcomeCode.Repaired, summary.RepairOutcomeCode);

        var audit = await WithDbAsync(db => db.AuditHistory.AsNoTracking().SingleAsync(a => a.EntityId == c.WorkOrderId && a.ActionCode == "WORK_SUMMARY_SUBMITTED"));
        Assert.Equal("WORK_ORDER", audit.EntityType);
        Assert.Equal("IN_PROGRESS", audit.FromState);
        Assert.Equal("AWAITING_SUPERVISOR_REVIEW", audit.ToState);
        Assert.Null(audit.Reason);
        Assert.Equal(c.Technician.UserId, audit.ActorId);
        Assert.InRange(audit.OccurredAt, before, after);
        using var auditJson = JsonDocument.Parse(audit.NewValueJson!);
        Assert.Equal(summary.Id, auditJson.RootElement.GetProperty("workSummaryId").GetGuid());
        Assert.Equal("REPAIRED", auditJson.RootElement.GetProperty("repairOutcomeCode").GetString());
    }

    [Fact]
    public async Task SubmitForAcceptance_ByTeamLead_MovesToAwaitingCustomerAcceptance_AndAudits()
    {
        var reviewReady = await SubmittedAsync();

        var response = await SendAsync(HttpMethod.Post, $"{WorkOrders}/{reviewReady.WorkOrderId}/submit-for-acceptance", reviewReady.Checked.TeamLead, null, reviewReady.WorkOrderETag);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal("AWAITING_CUSTOMER_ACCEPTANCE", body.GetProperty("status").GetString());

        var workOrder = await WithDbAsync(db => db.WorkOrders.AsNoTracking().SingleAsync(w => w.Id == reviewReady.WorkOrderId));
        Assert.Equal(WorkOrderStatus.AwaitingCustomerAcceptance, workOrder.Status);

        var audit = await WithDbAsync(db => db.AuditHistory.AsNoTracking().SingleAsync(a => a.EntityId == reviewReady.WorkOrderId && a.ActionCode == "WORK_ORDER_SUBMITTED_FOR_ACCEPTANCE"));
        Assert.Equal("AWAITING_SUPERVISOR_REVIEW", audit.FromState);
        Assert.Equal("AWAITING_CUSTOMER_ACCEPTANCE", audit.ToState);
        Assert.Equal(reviewReady.Checked.TeamLead.UserId, audit.ActorId);
    }

    [Fact]
    public async Task SubmitForAcceptance_BySupervisor_Succeeds_EitherRoleMayReview()
    {
        var reviewReady = await SubmittedAsync();

        var response = await SendAsync(HttpMethod.Post, $"{WorkOrders}/{reviewReady.WorkOrderId}/submit-for-acceptance", reviewReady.Checked.Supervisor, null, reviewReady.WorkOrderETag);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("AWAITING_CUSTOMER_ACCEPTANCE", (await JsonAsync(response)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task GetWorkSummary_ByTheSubmittingTechnician_AndByTeamLeadSupervisor_BothSucceed()
    {
        var reviewReady = await SubmittedAsync();

        var byTechnician = await SendAsync(HttpMethod.Get, $"{WorkOrders}/{reviewReady.WorkOrderId}/work-summary", reviewReady.Checked.Technician, null, null);
        Assert.Equal(HttpStatusCode.OK, byTechnician.StatusCode);
        Assert.Equal("REPAIRED", (await JsonAsync(byTechnician)).GetProperty("repairOutcomeCode").GetString());

        var byTeamLead = await SendAsync(HttpMethod.Get, $"{WorkOrders}/{reviewReady.WorkOrderId}/work-summary", reviewReady.Checked.TeamLead, null, null);
        Assert.Equal(HttpStatusCode.OK, byTeamLead.StatusCode);

        var bySupervisor = await SendAsync(HttpMethod.Get, $"{WorkOrders}/{reviewReady.WorkOrderId}/work-summary", reviewReady.Checked.Supervisor, null, null);
        Assert.Equal(HttpStatusCode.OK, bySupervisor.StatusCode);
    }

    [Fact]
    public async Task GetWorkSummary_BeforeAnySubmission_Returns404()
    {
        var c = await CheckedOutAsync();

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Get, $"{WorkOrders}/{c.WorkOrderId}/work-summary", c.Technician, null, null),
            HttpStatusCode.NotFound, "NOT_FOUND");
    }

    // ---------------- Authorization / scope / IDOR ----------------

    [Fact]
    public async Task Submit_ByANonTechnician_Get403()
    {
        var c = await CheckedOutAsync();

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{WorkOrders}/{c.WorkOrderId}/submit-work-summary", c.TeamLead, new { summaryText = "x", repairOutcomeCode = "REPAIRED" }, c.WorkOrderETag),
            HttpStatusCode.Forbidden, "ACCESS_DENIED");
        await AssertNoSummaryExistsAsync(c.WorkOrderId);
    }

    [Fact]
    public async Task Submit_ByADifferentTechnicianInTheSameTenant_Get404()
    {
        var c = await CheckedOutAsync();
        var other = await CallerAsync(c.TenantId, [c.Site], RoleCodes.Technician);

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{WorkOrders}/{c.WorkOrderId}/submit-work-summary", other, new { summaryText = "x", repairOutcomeCode = "REPAIRED" }, c.WorkOrderETag),
            HttpStatusCode.NotFound, "NOT_FOUND");
        await AssertNoSummaryExistsAsync(c.WorkOrderId);
    }

    [Fact]
    public async Task Submit_ByATechnicianOfAnotherTenant_Get404()
    {
        var c = await CheckedOutAsync();
        var foreign = await CallerAsync(Guid.NewGuid(), [], RoleCodes.Technician);

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{WorkOrders}/{c.WorkOrderId}/submit-work-summary", foreign, new { summaryText = "x", repairOutcomeCode = "REPAIRED" }, c.WorkOrderETag),
            HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task Submit_ByTheOwnerWhoseSiteScopeWasRevoked_Get404()
    {
        var c = await CheckedOutAsync();
        await WithDbAsync(db => db.UserSiteScopes
            .Where(scope => scope.TenantId == c.TenantId && scope.UserId == c.Technician.UserId && scope.SiteId == c.Site.Id)
            .ExecuteDeleteAsync());

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{WorkOrders}/{c.WorkOrderId}/submit-work-summary", c.Technician, new { summaryText = "x", repairOutcomeCode = "REPAIRED" }, c.WorkOrderETag),
            HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task Submit_OfAnUnknownWorkOrder_Get404()
    {
        var c = await CheckedOutAsync();

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{WorkOrders}/{Guid.NewGuid()}/submit-work-summary", c.Technician, new { summaryText = "x", repairOutcomeCode = "REPAIRED" }, c.WorkOrderETag),
            HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task Submit_WithoutIfMatch_Returns400_AndWithoutAToken_Returns401()
    {
        var c = await CheckedOutAsync();

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{WorkOrders}/{c.WorkOrderId}/submit-work-summary", c.Technician, new { summaryText = "x", repairOutcomeCode = "REPAIRED" }, null),
            HttpStatusCode.BadRequest, "BAD_REQUEST");
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await SendAsync(HttpMethod.Post, $"{WorkOrders}/{c.WorkOrderId}/submit-work-summary", null, new { summaryText = "x", repairOutcomeCode = "REPAIRED" }, c.WorkOrderETag)).StatusCode);
        await AssertNoSummaryExistsAsync(c.WorkOrderId);
    }

    [Fact]
    public async Task SubmitForAcceptance_ByATechnician_Get403()
    {
        var reviewReady = await SubmittedAsync();

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{WorkOrders}/{reviewReady.WorkOrderId}/submit-for-acceptance", reviewReady.Checked.Technician, null, reviewReady.WorkOrderETag),
            HttpStatusCode.Forbidden, "ACCESS_DENIED");
    }

    [Fact]
    public async Task SubmitForAcceptance_ByATeamLeadOutsideTheSiteScope_Get404()
    {
        var reviewReady = await SubmittedAsync();
        var outsider = await CallerAsync(reviewReady.Checked.TenantId, [], RoleCodes.TeamLead);

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{WorkOrders}/{reviewReady.WorkOrderId}/submit-for-acceptance", outsider, null, reviewReady.WorkOrderETag),
            HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task GetWorkSummary_ByADifferentTechnician_Get404()
    {
        var reviewReady = await SubmittedAsync();
        var other = await CallerAsync(reviewReady.Checked.TenantId, [reviewReady.Checked.Site], RoleCodes.Technician);

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Get, $"{WorkOrders}/{reviewReady.WorkOrderId}/work-summary", other, null, null),
            HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task GetWorkSummary_ByTheSubmittingTechnicianWhoseSiteScopeWasRevoked_Get404()
    {
        var reviewReady = await SubmittedAsync();
        await WithDbAsync(db => db.UserSiteScopes
            .Where(scope => scope.TenantId == reviewReady.Checked.TenantId && scope.UserId == reviewReady.Checked.Technician.UserId && scope.SiteId == reviewReady.Checked.Site.Id)
            .ExecuteDeleteAsync());

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Get, $"{WorkOrders}/{reviewReady.WorkOrderId}/work-summary", reviewReady.Checked.Technician, null, null),
            HttpStatusCode.NotFound, "NOT_FOUND");
    }

    // ---------------- State / duplicate / concurrency ----------------

    [Fact]
    public async Task Submit_BeforeCheckOut_Returns409StateConflict()
    {
        var c = await ScheduledAndCheckedInAsync();

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{WorkOrders}/{c.WorkOrderId}/submit-work-summary", c.Technician, new { summaryText = "x", repairOutcomeCode = "REPAIRED" }, c.WorkOrderETag),
            HttpStatusCode.Conflict, "STATE_CONFLICT");
    }

    [Fact]
    public async Task ASecondSubmit_OfAnAlreadySubmittedWorkOrder_Returns409StateConflict()
    {
        var reviewReady = await SubmittedAsync();

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{WorkOrders}/{reviewReady.WorkOrderId}/submit-work-summary", reviewReady.Checked.Technician,
                new { summaryText = "second attempt", repairOutcomeCode = "REPAIRED" }, reviewReady.WorkOrderETag),
            HttpStatusCode.Conflict, "STATE_CONFLICT");

        Assert.Equal(1, await WithDbAsync(db => db.WorkSummaries.AsNoTracking().CountAsync(s => s.WorkOrderId == reviewReady.WorkOrderId)));
    }

    [Fact]
    public async Task SubmitForAcceptance_BeforeAnySubmission_Returns409StateConflict()
    {
        var c = await CheckedOutAsync();

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{WorkOrders}/{c.WorkOrderId}/submit-for-acceptance", c.TeamLead, null, c.WorkOrderETag),
            HttpStatusCode.Conflict, "STATE_CONFLICT");
    }

    [Fact]
    public async Task ASecondSubmitForAcceptance_Returns409StateConflict()
    {
        var reviewReady = await SubmittedAsync();
        var first = await SendAsync(HttpMethod.Post, $"{WorkOrders}/{reviewReady.WorkOrderId}/submit-for-acceptance", reviewReady.Checked.Supervisor, null, reviewReady.WorkOrderETag);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var freshETag = first.Headers.ETag!.Tag;

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{WorkOrders}/{reviewReady.WorkOrderId}/submit-for-acceptance", reviewReady.Checked.TeamLead, null, freshETag),
            HttpStatusCode.Conflict, "STATE_CONFLICT");
    }

    [Fact]
    public async Task Submit_WithAStaleRowVersion_Returns409ConcurrencyConflict()
    {
        var c = await CheckedOutAsync();
        var first = await SendAsync(HttpMethod.Post, $"{WorkOrders}/{c.WorkOrderId}/submit-work-summary", c.Technician, new { summaryText = "x", repairOutcomeCode = "REPAIRED" }, c.WorkOrderETag);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{WorkOrders}/{c.WorkOrderId}/submit-work-summary", c.Technician, new { summaryText = "y", repairOutcomeCode = "REPAIRED" }, c.WorkOrderETag),
            HttpStatusCode.Conflict, "CONCURRENCY_CONFLICT");
        Assert.Equal(1, await WithDbAsync(db => db.WorkSummaries.AsNoTracking().CountAsync(s => s.WorkOrderId == c.WorkOrderId)));
    }

    [Fact]
    public async Task TwoConcurrentSubmits_OfTheSameWorkOrder_ExactlyOneSucceeds()
    {
        var c = await CheckedOutAsync();

        var responses = await Task.WhenAll(
            Task.Run(() => SendAsync(HttpMethod.Post, $"{WorkOrders}/{c.WorkOrderId}/submit-work-summary", c.Technician, new { summaryText = "a", repairOutcomeCode = "REPAIRED" }, c.WorkOrderETag)),
            Task.Run(() => SendAsync(HttpMethod.Post, $"{WorkOrders}/{c.WorkOrderId}/submit-work-summary", c.Technician, new { summaryText = "b", repairOutcomeCode = "REPAIRED" }, c.WorkOrderETag)));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal(1, await WithDbAsync(db => db.WorkSummaries.AsNoTracking().CountAsync(s => s.WorkOrderId == c.WorkOrderId)));
        Assert.Equal(1, await WithDbAsync(db => db.AuditHistory.AsNoTracking().CountAsync(a => a.EntityId == c.WorkOrderId && a.ActionCode == "WORK_SUMMARY_SUBMITTED")));
    }

    // ---------------- Validation ----------------

    [Theory]
    [InlineData(null, "REPAIRED", "summaryText")]
    [InlineData(" ", "REPAIRED", "summaryText")]
    [InlineData("valid summary", null, "repairOutcomeCode")]
    [InlineData("valid summary", " ", "repairOutcomeCode")]
    public async Task Submit_WithMissingOrBlankFields_Returns422_AndWritesNothing(string? summaryText, string? repairOutcomeCode, string invalidField)
    {
        var c = await CheckedOutAsync();

        var problem = await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{WorkOrders}/{c.WorkOrderId}/submit-work-summary", c.Technician, new { summaryText, repairOutcomeCode }, c.WorkOrderETag),
            HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
        Assert.True(problem.GetProperty("errors").TryGetProperty(invalidField, out _));

        await AssertNoSummaryExistsAsync(c.WorkOrderId);
        var workOrder = await WithDbAsync(db => db.WorkOrders.AsNoTracking().SingleAsync(w => w.Id == c.WorkOrderId));
        Assert.Equal(WorkOrderStatus.InProgress, workOrder.Status);
    }

    [Fact]
    public async Task Submit_WithSummaryTextLongerThanTheMaxLength_Returns422()
    {
        var c = await CheckedOutAsync();

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{WorkOrders}/{c.WorkOrderId}/submit-work-summary", c.Technician,
                new { summaryText = new string('A', WorkSummary.SummaryTextMaxLength + 1), repairOutcomeCode = "REPAIRED" }, c.WorkOrderETag),
            HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
        await AssertNoSummaryExistsAsync(c.WorkOrderId);
    }

    // ---------------- repairOutcomeCode closed allowlist (`docs/13` §4.15 Decision 5) ----------------

    [Theory]
    [InlineData("REPAIRED", RepairOutcomeCode.Repaired)]
    [InlineData("TEMPORARY_FIX", RepairOutcomeCode.TemporaryFix)]
    [InlineData("PARTS_REQUIRED", RepairOutcomeCode.PartsRequired)]
    [InlineData("NO_FAULT_FOUND", RepairOutcomeCode.NoFaultFound)]
    [InlineData("NOT_REPAIRABLE", RepairOutcomeCode.NotRepairable)]
    [InlineData("FOLLOW_UP_REQUIRED", RepairOutcomeCode.FollowUpRequired)]
    public async Task Submit_WithEachAllowedOutcomeCode_Succeeds(string code, RepairOutcomeCode expected)
    {
        var c = await CheckedOutAsync();

        var response = await SendAsync(HttpMethod.Post, $"{WorkOrders}/{c.WorkOrderId}/submit-work-summary", c.Technician, new { summaryText = "x", repairOutcomeCode = code }, c.WorkOrderETag);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var summary = await WithDbAsync(db => db.WorkSummaries.AsNoTracking().SingleAsync(s => s.WorkOrderId == c.WorkOrderId));
        Assert.Equal(expected, summary.RepairOutcomeCode);
    }

    [Theory]
    [InlineData("repaired")]
    [InlineData("Repaired")]
    [InlineData("  REPAIRED  ")]
    [InlineData("  repaired  ")]
    public async Task Submit_NormalizesCaseAndWhitespace_ToTheCanonicalCode(string rawInput)
    {
        var c = await CheckedOutAsync();

        var response = await SendAsync(HttpMethod.Post, $"{WorkOrders}/{c.WorkOrderId}/submit-work-summary", c.Technician, new { summaryText = "x", repairOutcomeCode = rawInput }, c.WorkOrderETag);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var summary = await WithDbAsync(db => db.WorkSummaries.AsNoTracking().SingleAsync(s => s.WorkOrderId == c.WorkOrderId));
        Assert.Equal(RepairOutcomeCode.Repaired, summary.RepairOutcomeCode);
    }

    [Theory]
    [InlineData("UNKNOWN_CODE")]
    [InlineData("REPAIRED_")]
    [InlineData("FIXED")]
    [InlineData("PENDING")]
    public async Task Submit_WithAnUnrecognizedOutcomeCode_Returns422_AndCreatesNothing(string invalidCode)
    {
        var c = await CheckedOutAsync();

        var problem = await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{WorkOrders}/{c.WorkOrderId}/submit-work-summary", c.Technician, new { summaryText = "x", repairOutcomeCode = invalidCode }, c.WorkOrderETag),
            HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
        Assert.True(problem.GetProperty("errors").TryGetProperty("repairOutcomeCode", out _));

        // Item 7: an invalid code creates no Work Summary, changes no Work Order state, and writes no audit row.
        await AssertNoSummaryExistsAsync(c.WorkOrderId);
        var workOrder = await WithDbAsync(db => db.WorkOrders.AsNoTracking().SingleAsync(w => w.Id == c.WorkOrderId));
        Assert.Equal(WorkOrderStatus.InProgress, workOrder.Status);
        Assert.Equal(0, await WithDbAsync(db => db.AuditHistory.AsNoTracking().CountAsync(a => a.EntityId == c.WorkOrderId && a.ActionCode == "WORK_SUMMARY_SUBMITTED")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t\n")]
    public async Task Submit_WithNullEmptyOrWhitespaceOutcomeCode_Returns422_AndCreatesNothing(string? blankCode)
    {
        var c = await CheckedOutAsync();

        var problem = await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{WorkOrders}/{c.WorkOrderId}/submit-work-summary", c.Technician, new { summaryText = "x", repairOutcomeCode = blankCode }, c.WorkOrderETag),
            HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
        Assert.True(problem.GetProperty("errors").TryGetProperty("repairOutcomeCode", out _));

        await AssertNoSummaryExistsAsync(c.WorkOrderId);
        var workOrder = await WithDbAsync(db => db.WorkOrders.AsNoTracking().SingleAsync(w => w.Id == c.WorkOrderId));
        Assert.Equal(WorkOrderStatus.InProgress, workOrder.Status);
    }

    // ---------------- Helpers ----------------

    private async Task AssertNoSummaryExistsAsync(Guid workOrderId) =>
        Assert.Equal(0, await WithDbAsync(db => db.WorkSummaries.AsNoTracking().CountAsync(s => s.WorkOrderId == workOrderId)));

    private sealed record ReviewReady(CheckedOut Checked, Guid WorkOrderId, string WorkOrderETag);

    /// <summary>A Work Order with its Work Summary already submitted (AWAITING_SUPERVISOR_REVIEW), ready for Submit for Acceptance.</summary>
    private async Task<ReviewReady> SubmittedAsync()
    {
        var c = await CheckedOutAsync();
        var response = await SendAsync(HttpMethod.Post, $"{WorkOrders}/{c.WorkOrderId}/submit-work-summary", c.Technician,
            new { summaryText = "Replaced the pump seal.", repairOutcomeCode = "REPAIRED" }, c.WorkOrderETag);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var etag = $"\"{(await JsonAsync(response)).GetProperty("rowVersion").GetString()}\"";
        return new ReviewReady(c, c.WorkOrderId, etag);
    }

    /// <summary>The full flow up to a real Check-out: Schedule, Check-in, Check-out. The Work Order's own fresh ETag is captured after Check-out.</summary>
    private async Task<CheckedOut> CheckedOutAsync()
    {
        var c = await ScheduledAndCheckedInAsync();

        var current = await SendAsync(HttpMethod.Get, $"{Sessions}/current", c.Technician, null, null);
        var currentBody = await JsonAsync(current);
        var sessionId = currentBody.GetProperty("workSessionId").GetGuid();
        var sessionETag = $"\"{currentBody.GetProperty("rowVersion").GetString()}\"";

        var checkedOut = await SendAsync(HttpMethod.Post, $"{Sessions}/{sessionId}/check-out", c.Technician, null, sessionETag);
        Assert.Equal(HttpStatusCode.OK, checkedOut.StatusCode);

        // The Technician has no other way to reach the Work Order's RowVersion (GET /work-orders/{id} excludes
        // TECHNICIAN, DEC-S2-001-03) — `workOrderRowVersion` on the session response exists precisely for this.
        var workOrderETag = $"\"{(await JsonAsync(checkedOut)).GetProperty("workOrderRowVersion").GetString()}\"";

        return c with { WorkOrderETag = workOrderETag };
    }

    private async Task<CheckedOut> ScheduledAndCheckedInAsync()
    {
        var tenantId = Guid.NewGuid();
        var site = await SiteAsync(tenantId);
        var admin = await CallerAsync(tenantId, [], RoleCodes.Administrator);
        var owner = await CallerAsync(tenantId, [site], RoleCodes.Requester);
        var approver = await CallerAsync(tenantId, [site], RoleCodes.Approver);
        var coordinator = await CallerAsync(tenantId, [site], RoleCodes.Coordinator);
        var technician = await CallerAsync(tenantId, [site], RoleCodes.Technician);
        var teamLead = await CallerAsync(tenantId, [site], RoleCodes.TeamLead);
        var supervisor = await CallerAsync(tenantId, [site], RoleCodes.Supervisor);

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

        var workOrder = await SendAsync(HttpMethod.Get, $"{WorkOrders}/{workOrderId}", teamLead, null, null);
        var workOrderETag = $"\"{(await JsonAsync(workOrder)).GetProperty("rowVersion").GetString()}\"";

        return new CheckedOut(tenantId, site, teamLead, supervisor, technician, workOrderId, visitId, workOrderETag);
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
