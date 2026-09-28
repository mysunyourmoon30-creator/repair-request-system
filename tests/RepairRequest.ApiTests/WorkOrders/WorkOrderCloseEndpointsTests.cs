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

/// <summary>API host with its own disposable LocalDB database for the Work Order Close endpoint tests.</summary>
public sealed class WorkOrderCloseApiFactory : AuthApiFactory
{
    public override string ConnectionString =>
        "Server=(localdb)\\MSSQLLocalDB;Database=RepairRequestDb_WorkOrderCloseApiTest;Trusted_Connection=True;TrustServerCertificate=True";
}

/// <summary>
/// Work Order Close (ST-WO-006; WO-API-010; BR-08; TC-WO-011/TC-CST-001; `docs/13` §4.20) end to end. Supervisor
/// only, within Site scope; the Work Order's own RowVersion is the If-Match token; Close needs COMPLETED, a
/// current Work Summary that was reviewed (WO-008 set by ST-WO-004), the customer's ACCEPT on the HIGHEST
/// acceptance round, and a reviewed Cost Summary. Any miss is 409 STATE_CONFLICT and writes nothing. Scope
/// boundary per `docs/13` §4.20: no SLA stop (Q1), no extra Separation of Duties / Visit / Work Session /
/// Corrective Action guard (Q5), no Cancel, no Reopen, no notification.
/// </summary>
public sealed class WorkOrderCloseEndpointsTests : IClassFixture<WorkOrderCloseApiFactory>
{
    private const string Requests = "/api/v1/repair-requests";
    private const string Routes = "/api/v1/approval-routes";
    private const string WorkOrders = "/api/v1/work-orders";
    private const string Visits = "/api/v1/service-visits";
    private const string Sessions = "/api/v1/work-sessions";
    private const string ValidHash = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";
    private const string StaleETag = "\"AAAAAAAAAAA=\"";

    private readonly WorkOrderCloseApiFactory _factory;

    public WorkOrderCloseEndpointsTests(WorkOrderCloseApiFactory factory)
    {
        _factory = factory;
    }

    private sealed record Caller(Guid UserId, Guid TenantId, string Token);

    /// <summary>A COMPLETED Work Order and everyone involved in getting it there.</summary>
    private sealed record Completed(
        Guid TenantId, Site Site, Caller Owner, Caller TeamLead, Caller Approver, Caller Coordinator, Caller Technician,
        Caller Supervisor, Guid WorkOrderId, Guid VisitId, Guid SessionId, string SessionETag);

    // ---------------- Success ----------------

    [Fact]
    public async Task Close_ByASiteScopedSupervisor_ClosesTheWorkOrder_AndAuditsInTheSameTransaction()
    {
        var w = await ReadyToCloseAsync();
        var etag = await WorkOrderETagAsync(w.WorkOrderId, w.Supervisor);
        var costSummary = await WithDbAsync(db => db.CostSummaries.AsNoTracking().SingleAsync(item => item.WorkOrderId == w.WorkOrderId));
        var before = DateTime.UtcNow.AddSeconds(-2);

        var response = await CloseAsync(w.Supervisor, w.WorkOrderId, etag);
        var after = DateTime.UtcNow.AddSeconds(2);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(response.Headers.ETag);
        Assert.NotEqual(etag, response.Headers.ETag!.Tag);
        var body = await JsonAsync(response);
        Assert.Equal("CLOSED", body.GetProperty("status").GetString());
        Assert.False(body.GetProperty("closedAt").ValueKind == JsonValueKind.Null);

        var workOrder = await WithDbAsync(db => db.WorkOrders.AsNoTracking().SingleAsync(item => item.Id == w.WorkOrderId));
        Assert.Equal(WorkOrderStatus.Closed, workOrder.Status);
        Assert.Equal(w.Supervisor.UserId, workOrder.ClosedBy);
        Assert.InRange(workOrder.ClosedAt!.Value, before, after);

        var audit = await WithDbAsync(db => db.AuditHistory.AsNoTracking().SingleAsync(a => a.EntityId == w.WorkOrderId && a.ActionCode == "WORK_ORDER_CLOSED"));
        Assert.Equal("WORK_ORDER", audit.EntityType);
        Assert.Equal("COMPLETED", audit.FromState);
        Assert.Equal("CLOSED", audit.ToState);
        Assert.Equal(w.Supervisor.UserId, audit.ActorId);
        Assert.InRange(audit.OccurredAt, before, after);
        Assert.Contains(costSummary.Id.ToString(), audit.NewValueJson!, StringComparison.OrdinalIgnoreCase);

        // Close never touches the Cost Summary, the Work Summary or the acceptance history.
        var costSummaryAfter = await WithDbAsync(db => db.CostSummaries.AsNoTracking().SingleAsync(item => item.WorkOrderId == w.WorkOrderId));
        Assert.Equal(costSummary.ReviewedAt, costSummaryAfter.ReviewedAt);
        Assert.Equal(costSummary.ReviewedBy, costSummaryAfter.ReviewedBy);
        Assert.Equal(1, await WithDbAsync(db => db.CustomerAcceptances.AsNoTracking().CountAsync(item => item.WorkOrderId == w.WorkOrderId)));
    }

    [Fact]
    public async Task Close_NeverExposesClosedBy_ButShowsClosedAtToARequester()
    {
        var w = await ReadyToCloseAsync();
        var closed = await CloseAsync(w.Supervisor, w.WorkOrderId, await WorkOrderETagAsync(w.WorkOrderId, w.Supervisor));
        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);
        Assert.False((await JsonAsync(closed)).TryGetProperty("closedBy", out _));

        // The Requester who created the Repair Request reads the closed Work Order through the ordinary detail read.
        var asRequester = await SendAsync(HttpMethod.Get, $"{WorkOrders}/{w.WorkOrderId}", w.Owner, null, null);
        Assert.Equal(HttpStatusCode.OK, asRequester.StatusCode);
        var body = await JsonAsync(asRequester);
        Assert.Equal("CLOSED", body.GetProperty("status").GetString());
        Assert.False(body.GetProperty("closedAt").ValueKind == JsonValueKind.Null);
        Assert.False(body.TryGetProperty("closedBy", out _));
    }

    [Fact]
    public async Task Close_BySupervisorWhoAlsoReviewedTheCostSummary_Succeeds_BecauseThereIsNoSeparationOfDutiesForClose()
    {
        // ReadyToCloseAsync's Supervisor is the very user who reviewed the Cost Summary (Q5).
        var w = await ReadyToCloseAsync();
        var reviewer = await WithDbAsync(db => db.CostSummaries.AsNoTracking().Select(item => new { item.WorkOrderId, item.ReviewedBy }).SingleAsync(item => item.WorkOrderId == w.WorkOrderId));
        Assert.Equal(w.Supervisor.UserId, reviewer.ReviewedBy);

        var response = await CloseAsync(w.Supervisor, w.WorkOrderId, await WorkOrderETagAsync(w.WorkOrderId, w.Supervisor));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Close_ByAUserWhoPreparedTheCostSummary_HoldingTeamLeadAndSupervisor_Succeeds()
    {
        var tenantId = Guid.NewGuid();
        var site = await SiteAsync(tenantId);
        var multiRole = await CallerAsync(tenantId, [site], RoleCodes.TeamLead, RoleCodes.Supervisor);
        var w = await CompletedAsync(tenantId, site, multiRole, needsApprovalRoute: true);

        var costSummaryETag = await PrepareCostSummaryAsync(w, multiRole);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Post, $"{WorkOrders}/{w.WorkOrderId}/review-cost-summary", w.Supervisor, null, costSummaryETag)).StatusCode);

        var response = await CloseAsync(multiRole, w.WorkOrderId, await WorkOrderETagAsync(w.WorkOrderId, multiRole));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Close_WithRejectRoundsInTheHistory_Succeeds_WhenTheLatestRoundIsAccept()
    {
        // History: ACCEPT (round 1, the real flow), then REJECT (round 2), then ACCEPT (round 3). Only the highest round counts.
        var w = await ReadyToCloseAsync();
        await AddAcceptanceRoundsAsync(w, (2, AcceptanceDecision.Reject), (3, AcceptanceDecision.Accept));

        var response = await CloseAsync(w.Supervisor, w.WorkOrderId, await WorkOrderETagAsync(w.WorkOrderId, w.Supervisor));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---------------- Authorization / scope ----------------

    [Theory]
    [InlineData(RoleCodes.Requester)]
    [InlineData(RoleCodes.Approver)]
    [InlineData(RoleCodes.Coordinator)]
    [InlineData(RoleCodes.Technician)]
    [InlineData(RoleCodes.TeamLead)]
    [InlineData(RoleCodes.Administrator)]
    public async Task Close_ByAnyoneWithoutTheSupervisorRole_Get403_AndNothingChanges(string role)
    {
        var w = await ReadyToCloseAsync();
        var caller = await CallerAsync(w.TenantId, [w.Site], role);
        var etag = await WorkOrderETagAsync(w.WorkOrderId, w.Supervisor);

        await AssertProblemAsync(await CloseAsync(caller, w.WorkOrderId, etag), HttpStatusCode.Forbidden, "ACCESS_DENIED");

        await AssertStillCompletedAsync(w.WorkOrderId);
    }

    [Fact]
    public async Task Close_ByASupervisorOfAnotherTenant_Get404_AndNothingChanges()
    {
        var w = await ReadyToCloseAsync();
        var foreign = await CallerAsync(Guid.NewGuid(), [], RoleCodes.Supervisor);

        await AssertProblemAsync(
            await CloseAsync(foreign, w.WorkOrderId, await WorkOrderETagAsync(w.WorkOrderId, w.Supervisor)),
            HttpStatusCode.NotFound, "NOT_FOUND");

        await AssertStillCompletedAsync(w.WorkOrderId);
    }

    [Fact]
    public async Task Close_BySupervisorWithNoSiteScopeOnThisWorkOrder_Get404_AndNothingChanges()
    {
        var w = await ReadyToCloseAsync();
        var otherSite = await SiteAsync(w.TenantId);
        var outOfScope = await CallerAsync(w.TenantId, [otherSite], RoleCodes.Supervisor);
        var noScope = await CallerAsync(w.TenantId, [], RoleCodes.Supervisor);
        var etag = await WorkOrderETagAsync(w.WorkOrderId, w.Supervisor);

        await AssertProblemAsync(await CloseAsync(outOfScope, w.WorkOrderId, etag), HttpStatusCode.NotFound, "NOT_FOUND");
        await AssertProblemAsync(await CloseAsync(noScope, w.WorkOrderId, etag), HttpStatusCode.NotFound, "NOT_FOUND");

        await AssertStillCompletedAsync(w.WorkOrderId);
    }

    [Fact]
    public async Task Close_OfAWorkOrderThatDoesNotExist_Get404()
    {
        var w = await ReadyToCloseAsync();

        await AssertProblemAsync(await CloseAsync(w.Supervisor, Guid.NewGuid(), StaleETag), HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task Close_WithoutIfMatch_Returns400_AndWithoutAToken_Returns401()
    {
        var w = await ReadyToCloseAsync();
        var etag = await WorkOrderETagAsync(w.WorkOrderId, w.Supervisor);

        await AssertProblemAsync(await SendAsync(HttpMethod.Post, $"{WorkOrders}/{w.WorkOrderId}/close", w.Supervisor, null, null), HttpStatusCode.BadRequest, "BAD_REQUEST");
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Post, $"{WorkOrders}/{w.WorkOrderId}/close", null, null, etag)).StatusCode);

        await AssertStillCompletedAsync(w.WorkOrderId);
    }

    // ---------------- Concurrency / duplicate ----------------

    [Fact]
    public async Task Close_WithAStaleRowVersion_Returns409ConcurrencyConflict_AndWritesNothing()
    {
        var w = await ReadyToCloseAsync();

        await AssertProblemAsync(await CloseAsync(w.Supervisor, w.WorkOrderId, StaleETag), HttpStatusCode.Conflict, "CONCURRENCY_CONFLICT");

        await AssertStillCompletedAsync(w.WorkOrderId);
    }

    [Fact]
    public async Task Close_Twice_TheSecondCallIsAConflict_AndNoSecondAuditRowIsWritten()
    {
        var w = await ReadyToCloseAsync();
        var firstETag = await WorkOrderETagAsync(w.WorkOrderId, w.Supervisor);

        var first = await CloseAsync(w.Supervisor, w.WorkOrderId, firstETag);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var closedAt = (await WithDbAsync(db => db.WorkOrders.AsNoTracking().SingleAsync(item => item.Id == w.WorkOrderId))).ClosedAt;

        // With the fresh token: the Work Order is already CLOSED → state conflict with its own message.
        var problem = await AssertProblemAsync(await CloseAsync(w.Supervisor, w.WorkOrderId, first.Headers.ETag!.Tag), HttpStatusCode.Conflict, "STATE_CONFLICT");
        Assert.Contains("already closed", problem.GetProperty("detail").GetString());

        // With the now-stale token: a concurrency conflict.
        await AssertProblemAsync(await CloseAsync(w.Supervisor, w.WorkOrderId, firstETag), HttpStatusCode.Conflict, "CONCURRENCY_CONFLICT");

        Assert.Equal(1, await WithDbAsync(db => db.AuditHistory.AsNoTracking().CountAsync(a => a.EntityId == w.WorkOrderId && a.ActionCode == "WORK_ORDER_CLOSED")));
        Assert.Equal(closedAt, (await WithDbAsync(db => db.WorkOrders.AsNoTracking().SingleAsync(item => item.Id == w.WorkOrderId))).ClosedAt);
    }

    [Fact]
    public async Task TwoConcurrentCloses_OfTheSameWorkOrder_ExactlyOneSucceeds_NoDuplicateAudit()
    {
        var w = await ReadyToCloseAsync();
        var etag = await WorkOrderETagAsync(w.WorkOrderId, w.Supervisor);

        var responses = await Task.WhenAll(
            Task.Run(() => CloseAsync(w.Supervisor, w.WorkOrderId, etag)),
            Task.Run(() => CloseAsync(w.Supervisor, w.WorkOrderId, etag)));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        // The loser's audit row rides in the same SaveChanges as its status update, so it never commits either.
        Assert.Equal(1, await WithDbAsync(db => db.AuditHistory.AsNoTracking().CountAsync(a => a.EntityId == w.WorkOrderId && a.ActionCode == "WORK_ORDER_CLOSED")));
        var workOrder = await WithDbAsync(db => db.WorkOrders.AsNoTracking().SingleAsync(item => item.Id == w.WorkOrderId));
        Assert.Equal(WorkOrderStatus.Closed, workOrder.Status);
        Assert.Equal(w.Supervisor.UserId, workOrder.ClosedBy);
    }

    // ---------------- State: the Work Order must be COMPLETED ----------------

    [Theory]
    [InlineData("OPEN")]
    [InlineData("SCHEDULED")]
    [InlineData("IN_PROGRESS")]
    [InlineData("AWAITING_SUPERVISOR_REVIEW")]
    [InlineData("AWAITING_CUSTOMER_ACCEPTANCE")]
    [InlineData("CORRECTIVE_ACTION_REQUIRED")]
    [InlineData("CANCELLED")]
    public async Task Close_WhenTheWorkOrderIsNotCompleted_Get409StateConflict_AndNothingChanges(string status)
    {
        // Each status is arranged directly in SQL, on a Work Order that otherwise satisfies every other Close
        // prerequisite, to isolate the COMPLETED state guard. This test does not assert which of these statuses
        // can be reached together with a reviewed Cost Summary through the public APIs.
        var w = await ReadyToCloseAsync();
        await WithDbAsync(db => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE work_order SET status = {status} WHERE work_order_id = {w.WorkOrderId}"));

        var problem = await AssertProblemAsync(
            await CloseAsync(w.Supervisor, w.WorkOrderId, await CurrentETagFromDbAsync(w.WorkOrderId)),
            HttpStatusCode.Conflict, "STATE_CONFLICT");

        Assert.Contains("COMPLETED", problem.GetProperty("detail").GetString());
        var workOrder = await WithDbAsync(db => db.WorkOrders.AsNoTracking().SingleAsync(item => item.Id == w.WorkOrderId));
        Assert.Null(workOrder.ClosedBy);
        Assert.Null(workOrder.ClosedAt);
        Assert.Equal(0, await CloseAuditCountAsync(w.WorkOrderId));
    }

    [Fact]
    public async Task Close_BeforeTheCustomerHasAccepted_Get409StateConflict()
    {
        // The real flow, stopped one step early: submitted for acceptance, never accepted.
        var c = await CheckedOutAndSummarySubmittedAsync(needsApprovalRoute: true);
        var submitted = await SendAsync(HttpMethod.Post, $"{WorkOrders}/{c.WorkOrderId}/submit-for-acceptance", c.TeamLead,
            new { acceptanceContactId = c.Owner.UserId }, c.WorkOrderETag);
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
        var supervisor = await CallerAsync(c.TenantId, [c.Site], RoleCodes.Supervisor);

        await AssertProblemAsync(
            await CloseAsync(supervisor, c.WorkOrderId, submitted.Headers.ETag!.Tag),
            HttpStatusCode.Conflict, "STATE_CONFLICT");
    }

    // ---------------- Guards: every missing prerequisite ----------------

    [Fact]
    public async Task Close_WithoutAnyCostSummary_Get409StateConflict_WithASpecificMessage()
    {
        var w = await CompletedAsync();

        var problem = await AssertProblemAsync(
            await CloseAsync(w.Supervisor, w.WorkOrderId, await WorkOrderETagAsync(w.WorkOrderId, w.Supervisor)),
            HttpStatusCode.Conflict, "STATE_CONFLICT");

        Assert.Contains("no Cost Summary", problem.GetProperty("detail").GetString());
        await AssertStillCompletedAsync(w.WorkOrderId);
    }

    [Fact]
    public async Task Close_WithAPreparedButUnreviewedCostSummary_Get409StateConflict_WithASpecificMessage()
    {
        var w = await CompletedAsync();
        await PrepareCostSummaryAsync(w, w.TeamLead);

        var problem = await AssertProblemAsync(
            await CloseAsync(w.Supervisor, w.WorkOrderId, await WorkOrderETagAsync(w.WorkOrderId, w.Supervisor)),
            HttpStatusCode.Conflict, "STATE_CONFLICT");

        Assert.Contains("has not been reviewed", problem.GetProperty("detail").GetString());
        await AssertStillCompletedAsync(w.WorkOrderId);
    }

    [Fact]
    public async Task Close_WithoutAWorkSummary_Get409StateConflict_WithASpecificMessage()
    {
        var w = await ReadyToCloseAsync();
        await WithDbAsync(async db =>
        {
            db.WorkSummaries.RemoveRange(db.WorkSummaries.Where(item => item.WorkOrderId == w.WorkOrderId));
            await db.SaveChangesAsync();
        });

        var problem = await AssertProblemAsync(
            await CloseAsync(w.Supervisor, w.WorkOrderId, await CurrentETagFromDbAsync(w.WorkOrderId)),
            HttpStatusCode.Conflict, "STATE_CONFLICT");

        Assert.Contains("no Work Summary", problem.GetProperty("detail").GetString());
        await AssertStillCompletedAsync(w.WorkOrderId);
    }

    [Fact]
    public async Task Close_WhenTheWorkSummaryWasNeverReviewedAndSubmittedForAcceptance_Get409StateConflict()
    {
        // Close uses a non-null WO-008 acceptance_contact_id as its evidence that ST-WO-004 (submit for
        // acceptance) ran (docs/13 §4.20 Q2). The NULL state is constructed explicitly in SQL for negative-path
        // verification of that guard; this test does not assert reachability through the public APIs.
        var w = await ReadyToCloseAsync();
        await WithDbAsync(db => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE work_order SET acceptance_contact_id = NULL WHERE work_order_id = {w.WorkOrderId}"));

        var problem = await AssertProblemAsync(
            await CloseAsync(w.Supervisor, w.WorkOrderId, await CurrentETagFromDbAsync(w.WorkOrderId)),
            HttpStatusCode.Conflict, "STATE_CONFLICT");

        Assert.Contains("has not been reviewed and submitted for acceptance", problem.GetProperty("detail").GetString());
        await AssertStillCompletedAsync(w.WorkOrderId);
    }

    [Fact]
    public async Task Close_WithNoAcceptanceHistoryAtAll_Get409StateConflict()
    {
        var w = await ReadyToCloseAsync();
        await WithDbAsync(async db =>
        {
            db.CustomerAcceptances.RemoveRange(db.CustomerAcceptances.Where(item => item.WorkOrderId == w.WorkOrderId));
            await db.SaveChangesAsync();
        });

        var problem = await AssertProblemAsync(
            await CloseAsync(w.Supervisor, w.WorkOrderId, await CurrentETagFromDbAsync(w.WorkOrderId)),
            HttpStatusCode.Conflict, "STATE_CONFLICT");

        Assert.Contains("has not accepted", problem.GetProperty("detail").GetString());
        await AssertStillCompletedAsync(w.WorkOrderId);
    }

    [Fact]
    public async Task Close_WhenTheOnlyAcceptanceRoundIsAReject_Get409StateConflict()
    {
        var w = await ReadyToCloseAsync();
        await WithDbAsync(db => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE customer_acceptance SET decision = 'REJECT', decision_reason = 'Still leaking.' WHERE work_order_id = {w.WorkOrderId}"));

        await AssertProblemAsync(
            await CloseAsync(w.Supervisor, w.WorkOrderId, await CurrentETagFromDbAsync(w.WorkOrderId)),
            HttpStatusCode.Conflict, "STATE_CONFLICT");

        await AssertStillCompletedAsync(w.WorkOrderId);
    }

    [Fact]
    public async Task Close_WhenAnEarlierRoundWasAcceptButTheLatestRoundIsReject_Get409StateConflict()
    {
        // The regression this rule exists for: "any ACCEPT row in history" would pass here; only the latest round counts.
        var w = await ReadyToCloseAsync();
        await AddAcceptanceRoundsAsync(w, (2, AcceptanceDecision.Reject));

        var problem = await AssertProblemAsync(
            await CloseAsync(w.Supervisor, w.WorkOrderId, await CurrentETagFromDbAsync(w.WorkOrderId)),
            HttpStatusCode.Conflict, "STATE_CONFLICT");

        Assert.Contains("has not accepted", problem.GetProperty("detail").GetString());
        await AssertStillCompletedAsync(w.WorkOrderId);
    }

    // ---------------- Lock: nothing mutates a CLOSED Work Order ----------------

    [Fact]
    public async Task AfterClose_EveryMutationCommand_IsRejected_AndNothingChanges()
    {
        var w = await ReadyToCloseAsync();
        var closed = await CloseAsync(w.Supervisor, w.WorkOrderId, await WorkOrderETagAsync(w.WorkOrderId, w.Supervisor));
        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);
        var workOrderETag = closed.Headers.ETag!.Tag;

        var costSummaryRead = await JsonAsync(await SendAsync(HttpMethod.Get, $"{WorkOrders}/{w.WorkOrderId}/cost-summary", w.Supervisor, null, null));
        var costSummaryETag = $"\"{costSummaryRead.GetProperty("rowVersion").GetString()}\"";
        var detail = await JsonAsync(await SendAsync(HttpMethod.Get, $"{WorkOrders}/{w.WorkOrderId}", w.Supervisor, null, null));
        var visitETag = $"\"{detail.GetProperty("visits")[0].GetProperty("rowVersion").GetString()}\"";

        var attempts = new (string Name, HttpMethod Method, string Url, Caller Caller, object? Body, string ETag)[]
        {
            ("second Close", HttpMethod.Post, $"{WorkOrders}/{w.WorkOrderId}/close", w.Supervisor, null, workOrderETag),
            ("Cost Summary Prepare", HttpMethod.Put, $"{WorkOrders}/{w.WorkOrderId}/cost-summary", w.TeamLead, new { totalAmount = 5m, currencyCode = "USD", note = (string?)null }, costSummaryETag),
            ("Cost Summary Review", HttpMethod.Post, $"{WorkOrders}/{w.WorkOrderId}/review-cost-summary", w.Supervisor, null, costSummaryETag),
            ("Customer Accept", HttpMethod.Post, $"{WorkOrders}/{w.WorkOrderId}/accept", w.Owner, null, workOrderETag),
            ("Customer Reject", HttpMethod.Post, $"{WorkOrders}/{w.WorkOrderId}/reject", w.Owner, new { decisionReason = "Too late." }, workOrderETag),
            ("Submit Work Summary", HttpMethod.Post, $"{WorkOrders}/{w.WorkOrderId}/submit-work-summary", w.Technician, new { summaryText = "Again.", repairOutcomeCode = "REPAIRED" }, workOrderETag),
            ("Submit for Acceptance", HttpMethod.Post, $"{WorkOrders}/{w.WorkOrderId}/submit-for-acceptance", w.TeamLead, new { acceptanceContactId = w.Owner.UserId }, workOrderETag),
            ("Schedule", HttpMethod.Post, $"{WorkOrders}/{w.WorkOrderId}/schedule", w.Coordinator, new { ownerTeamId = Guid.NewGuid(), assignedTechnicianId = w.Technician.UserId, scheduledStartAt = "2026-10-05T08:00:00Z", scheduledEndAt = "2026-10-05T10:00:00Z" }, workOrderETag),
            ("Visit Check-in", HttpMethod.Post, $"{Visits}/{w.VisitId}/check-in", w.Technician, null, visitETag),
            ("Visit Reschedule", HttpMethod.Post, $"{Visits}/{w.VisitId}/reschedule", w.Coordinator, new { reason = "Later.", scheduledStartAt = "2026-10-06T08:00:00Z", scheduledEndAt = "2026-10-06T10:00:00Z" }, visitETag),
            ("Visit Reassign", HttpMethod.Post, $"{Visits}/{w.VisitId}/reassign", w.Coordinator, new { reason = "Swap.", assignedTeamId = Guid.NewGuid(), assignedTechnicianId = w.Technician.UserId }, visitETag),
            ("Visit Cancel", HttpMethod.Post, $"{Visits}/{w.VisitId}/cancel", w.Coordinator, new { reason = "Not needed." }, visitETag),
            ("Visit Mark Missed", HttpMethod.Post, $"{Visits}/{w.VisitId}/mark-missed", w.Coordinator, new { reason = "No show." }, visitETag),
            ("Work Session Pause", HttpMethod.Post, $"{Sessions}/{w.SessionId}/pause", w.Technician, new { reason = "Break." }, w.SessionETag),
            ("Work Session Resume", HttpMethod.Post, $"{Sessions}/{w.SessionId}/resume", w.Technician, null, w.SessionETag),
            ("Work Session Check-out", HttpMethod.Post, $"{Sessions}/{w.SessionId}/check-out", w.Technician, null, w.SessionETag)
        };

        var accepted = new List<string>();
        foreach (var (name, method, url, caller, body, etag) in attempts)
        {
            var response = await SendAsync(method, url, caller, body, etag);
            var code = response.StatusCode == HttpStatusCode.Conflict ? (await JsonAsync(response)).GetProperty("code").GetString() : null;

            // Every token above is fresh, so a 409 that is CONCURRENCY_CONFLICT would mean a stale token masked a
            // missing state guard; the lock is only proven by STATE_CONFLICT.
            if (code != "STATE_CONFLICT")
            {
                accepted.Add($"{name}: {(int)response.StatusCode} {code}");
            }
        }

        Assert.True(accepted.Count == 0, "Commands that were not rejected with 409 STATE_CONFLICT on a CLOSED Work Order: " + string.Join("; ", accepted));

        var workOrder = await WithDbAsync(db => db.WorkOrders.AsNoTracking().SingleAsync(item => item.Id == w.WorkOrderId));
        Assert.Equal(WorkOrderStatus.Closed, workOrder.Status);
        Assert.Equal(w.Supervisor.UserId, workOrder.ClosedBy);
        Assert.Equal(1, await CloseAuditCountAsync(w.WorkOrderId));
        Assert.Equal(1, await WithDbAsync(db => db.CustomerAcceptances.AsNoTracking().CountAsync(item => item.WorkOrderId == w.WorkOrderId)));
        Assert.Equal(1, await WithDbAsync(db => db.ServiceVisits.AsNoTracking().CountAsync(item => item.WorkOrderId == w.WorkOrderId)));
        var costSummary = await WithDbAsync(db => db.CostSummaries.AsNoTracking().SingleAsync(item => item.WorkOrderId == w.WorkOrderId));
        Assert.Equal(1234.56m, costSummary.TotalAmount);
    }

    [Fact]
    public async Task AfterClose_TheWorkOrderListsAsClosed_AndLeavesTheCostSummaryReviewQueue()
    {
        var w = await ReadyToCloseAsync();
        Assert.Equal(HttpStatusCode.OK, (await CloseAsync(w.Supervisor, w.WorkOrderId, await WorkOrderETagAsync(w.WorkOrderId, w.Supervisor))).StatusCode);

        var list = await JsonAsync(await SendAsync(HttpMethod.Get, $"{WorkOrders}?status=CLOSED", w.Supervisor, null, null));
        Assert.Contains(list.GetProperty("items").EnumerateArray(), item => item.GetProperty("workOrderId").GetGuid() == w.WorkOrderId);

        var completedList = await JsonAsync(await SendAsync(HttpMethod.Get, $"{WorkOrders}?status=COMPLETED", w.Supervisor, null, null));
        Assert.DoesNotContain(completedList.GetProperty("items").EnumerateArray(), item => item.GetProperty("workOrderId").GetGuid() == w.WorkOrderId);
    }

    // ---------------- Helpers ----------------

    private Task<HttpResponseMessage> CloseAsync(Caller? caller, Guid workOrderId, string? etag) =>
        SendAsync(HttpMethod.Post, $"{WorkOrders}/{workOrderId}/close", caller, null, etag);

    private async Task AssertStillCompletedAsync(Guid workOrderId)
    {
        var workOrder = await WithDbAsync(db => db.WorkOrders.AsNoTracking().SingleAsync(item => item.Id == workOrderId));
        Assert.Equal(WorkOrderStatus.Completed, workOrder.Status);
        Assert.Null(workOrder.ClosedBy);
        Assert.Null(workOrder.ClosedAt);
        Assert.Equal(0, await CloseAuditCountAsync(workOrderId));
    }

    private Task<int> CloseAuditCountAsync(Guid workOrderId) =>
        WithDbAsync(db => db.AuditHistory.AsNoTracking().CountAsync(a => a.EntityId == workOrderId && a.ActionCode == "WORK_ORDER_CLOSED"));

    /// <summary>The Work Order's current ETag as the API returns it.</summary>
    private async Task<string> WorkOrderETagAsync(Guid workOrderId, Caller caller)
    {
        var response = await SendAsync(HttpMethod.Get, $"{WorkOrders}/{workOrderId}", caller, null, null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return response.Headers.ETag!.Tag;
    }

    /// <summary>The Work Order's current RowVersion straight from the database (after a raw-SQL seed, SQL Server's rowversion has advanced).</summary>
    private async Task<string> CurrentETagFromDbAsync(Guid workOrderId)
    {
        var workOrder = await WithDbAsync(db => db.WorkOrders.AsNoTracking().SingleAsync(item => item.Id == workOrderId));
        return $"\"{Convert.ToBase64String(workOrder.RowVersion)}\"";
    }

    /// <summary>Adds acceptance rounds (round number, decision) directly, as if further customer decisions had been made.</summary>
    private Task AddAcceptanceRoundsAsync(Completed w, params (int Round, AcceptanceDecision Decision)[] rounds) =>
        WithDbAsync(async db =>
        {
            foreach (var (round, decision) in rounds)
            {
                db.CustomerAcceptances.Add(decision == AcceptanceDecision.Accept
                    ? CustomerAcceptance.Accept(w.TenantId, w.WorkOrderId, round, w.Owner.UserId, DateTime.UtcNow.AddMinutes(round))
                    : CustomerAcceptance.Reject(w.TenantId, w.WorkOrderId, round, w.Owner.UserId, "Not fixed.", DateTime.UtcNow.AddMinutes(round)));
            }

            await db.SaveChangesAsync();
        });

    /// <summary>A COMPLETED Work Order whose Cost Summary was prepared by the Team Lead and reviewed by the (distinct) Supervisor.</summary>
    private async Task<Completed> ReadyToCloseAsync()
    {
        var w = await CompletedAsync();
        var costSummaryETag = await PrepareCostSummaryAsync(w, w.TeamLead);

        var reviewed = await SendAsync(HttpMethod.Post, $"{WorkOrders}/{w.WorkOrderId}/review-cost-summary", w.Supervisor, null, costSummaryETag);
        Assert.Equal(HttpStatusCode.OK, reviewed.StatusCode);

        return w;
    }

    private async Task<string> PrepareCostSummaryAsync(Completed w, Caller preparer)
    {
        var workOrderETag = await WorkOrderETagAsync(w.WorkOrderId, preparer);
        var prepared = await SendAsync(HttpMethod.Put, $"{WorkOrders}/{w.WorkOrderId}/cost-summary", preparer,
            new { totalAmount = 1234.56m, currencyCode = "USD", note = "Parts and labor." }, workOrderETag);
        Assert.Equal(HttpStatusCode.OK, prepared.StatusCode);
        return prepared.Headers.ETag!.Tag;
    }

    /// <summary>A Work Order COMPLETED (Accept already happened), with a fresh tenant/site/Team Lead.</summary>
    private async Task<Completed> CompletedAsync(Guid? tenantId = null, Site? site = null, Caller? teamLead = null, bool needsApprovalRoute = true)
    {
        var c = await CheckedOutAndSummarySubmittedAsync(tenantId, site, teamLead, needsApprovalRoute);

        var submitted = await SendAsync(HttpMethod.Post, $"{WorkOrders}/{c.WorkOrderId}/submit-for-acceptance", c.TeamLead,
            new { acceptanceContactId = c.Owner.UserId }, c.WorkOrderETag);
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);

        var accepted = await SendAsync(HttpMethod.Post, $"{WorkOrders}/{c.WorkOrderId}/accept", c.Owner, null, submitted.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        var supervisor = await CallerAsync(c.TenantId, [c.Site], RoleCodes.Supervisor);
        return new Completed(c.TenantId, c.Site, c.Owner, c.TeamLead, c.Approver, c.Coordinator, c.Technician, supervisor, c.WorkOrderId, c.VisitId, c.SessionId, c.SessionETag);
    }

    private sealed record ReviewReady(
        Guid TenantId, Site Site, Caller Owner, Caller TeamLead, Caller Approver, Caller Coordinator, Caller Technician,
        Guid WorkOrderId, string WorkOrderETag, Guid VisitId, Guid SessionId, string SessionETag);

    /// <summary>The full flow up to a submitted Work Summary (AWAITING_SUPERVISOR_REVIEW): Schedule, Check-in, Check-out, Submit Work Summary.</summary>
    private async Task<ReviewReady> CheckedOutAndSummarySubmittedAsync(Guid? existingTenantId = null, Site? existingSite = null, Caller? existingTeamLead = null, bool needsApprovalRoute = true)
    {
        var tenantId = existingTenantId ?? Guid.NewGuid();
        var site = existingSite ?? await SiteAsync(tenantId);
        var admin = await CallerAsync(tenantId, [], RoleCodes.Administrator);
        var owner = await CallerAsync(tenantId, [site], RoleCodes.Requester);
        var approver = await CallerAsync(tenantId, [site], RoleCodes.Approver);
        var coordinator = await CallerAsync(tenantId, [site], RoleCodes.Coordinator);
        var technician = await CallerAsync(tenantId, [site], RoleCodes.Technician);
        var teamLead = existingTeamLead ?? await CallerAsync(tenantId, [site], RoleCodes.TeamLead);

        if (needsApprovalRoute)
        {
            Assert.Equal(HttpStatusCode.Created, (await SendAsync(HttpMethod.Post, Routes, admin, new { requestCategoryCode = "ELECTRICAL", siteId = site.Id, approverRoleCode = "APPROVER" }, null)).StatusCode);
        }

        var (id, draftETag) = await DraftAsync(owner, site);
        await WithDbAsync(async db =>
        {
            var file = new FileAsset(tenantId, "evidence.png", "image/png", 1024, ValidHash, $"{tenantId:N}/{Guid.NewGuid():N}", owner.UserId, DateTime.UtcNow);
            file.MarkClean();
            db.FileAssets.Add(file);
            db.RepairRequestAttachments.Add(new RepairRequestAttachment(id, file.Id, AttachmentFileRules.AccessScopeCode));
            await db.SaveChangesAsync();
        });

        var submitted = await SendAsync(HttpMethod.Post, $"{Requests}/{id}/submit", owner, new { duplicateContinuationReason = "Second reported occurrence." }, draftETag);
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
        var checkedOutBody = await JsonAsync(checkedOut);
        var workOrderETag = $"\"{checkedOutBody.GetProperty("workOrderRowVersion").GetString()}\"";
        var sessionAfterCheckOutETag = $"\"{checkedOutBody.GetProperty("rowVersion").GetString()}\"";

        var summarySubmitted = await SendAsync(HttpMethod.Post, $"{WorkOrders}/{workOrderId}/submit-work-summary", technician,
            new { summaryText = "Replaced the pump seal.", repairOutcomeCode = "REPAIRED" }, workOrderETag);
        Assert.Equal(HttpStatusCode.OK, summarySubmitted.StatusCode);
        var reviewETag = $"\"{(await JsonAsync(summarySubmitted)).GetProperty("rowVersion").GetString()}\"";

        return new ReviewReady(tenantId, site, owner, teamLead, approver, coordinator, technician, workOrderId, reviewETag, visitId, sessionId, sessionAfterCheckOutETag);
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
