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

/// <summary>API host with its own disposable LocalDB database for the Work Order Cancel endpoint tests.</summary>
public sealed class CancelWorkOrderApiFactory : AuthApiFactory
{
    public override string ConnectionString =>
        "Server=(localdb)\\MSSQLLocalDB;Database=RepairRequestDb_CancelWorkOrderApiTest;Trusted_Connection=True;TrustServerCertificate=True";
}

/// <summary>
/// Work Order Cancel (ST-WO-011; WO-API-011; UC-WO-026; BR-09/BR-12; `docs/13` §4.25) end to end. Supervisor only,
/// within Site scope; the Work Order's own RowVersion is the If-Match token; `reason` is required. Allowed from
/// every non-terminal, pre-Accept status; denied once the customer has accepted, or the Work Order is COMPLETED/
/// CLOSED/CANCELLED, or while any Service Visit is IN_PROGRESS (`docs/13` §4.25 Decision: deny, unchanged, never
/// force-terminate an active Work Session). Cascades Cancel to every still-SCHEDULED Service Visit; COMPLETED/
/// MISSED/CANCELLED history and any in-flight Corrective Action row are left untouched. No SLA stop (no
/// `sla_record` subsystem exists yet) and no NTF-CANCEL outbox event — both explicit, out-of-scope gaps.
/// </summary>
public sealed class CancelWorkOrderEndpointsTests : IClassFixture<CancelWorkOrderApiFactory>
{
    private const string Requests = "/api/v1/repair-requests";
    private const string Routes = "/api/v1/approval-routes";
    private const string WorkOrders = "/api/v1/work-orders";
    private const string Visits = "/api/v1/service-visits";
    private const string Sessions = "/api/v1/work-sessions";
    private const string CorrectiveActions = "/api/v1/corrective-actions";
    private const string ValidHash = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";
    private const string StaleETag = "\"AAAAAAAAAAA=\"";

    private readonly CancelWorkOrderApiFactory _factory;

    public CancelWorkOrderEndpointsTests(CancelWorkOrderApiFactory factory)
    {
        _factory = factory;
    }

    private sealed record Caller(Guid UserId, Guid TenantId, string Token);

    private sealed record Built(
        Guid TenantId, Site Site, Caller Owner, Caller Approver, Caller Coordinator, Caller Technician, Caller TeamLead, Caller Supervisor,
        Guid WorkOrderId, Guid? VisitId, string WorkOrderETag);

    // ---------------- Success: allowed source states ----------------

    [Fact]
    public async Task Cancel_FromOpen_Success_MovesToCancelled_AndAudits()
    {
        var w = await OpenAsync();
        var before = DateTime.UtcNow.AddSeconds(-2);

        var response = await CancelAsync(w.Supervisor, w.WorkOrderId, w.WorkOrderETag, "No longer needed.");
        var after = DateTime.UtcNow.AddSeconds(2);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal("CANCELLED", body.GetProperty("status").GetString());
        Assert.Equal("No longer needed.", body.GetProperty("cancelReason").GetString());

        var workOrder = await WithDbAsync(db => db.WorkOrders.AsNoTracking().SingleAsync(item => item.Id == w.WorkOrderId));
        Assert.Equal(WorkOrderStatus.Cancelled, workOrder.Status);
        Assert.Equal("No longer needed.", workOrder.CancelReason);

        var audit = await WithDbAsync(db => db.AuditHistory.AsNoTracking().SingleAsync(a => a.EntityId == w.WorkOrderId && a.ActionCode == "WORK_ORDER_CANCELLED"));
        Assert.Equal("WORK_ORDER", audit.EntityType);
        Assert.Equal("OPEN", audit.FromState);
        Assert.Equal("CANCELLED", audit.ToState);
        Assert.Equal("No longer needed.", audit.Reason);
        Assert.Equal(w.Supervisor.UserId, audit.ActorId);
        Assert.InRange(audit.OccurredAt, before, after);
        Assert.NotEqual(Guid.Empty, audit.CorrelationId);
    }

    [Fact]
    public async Task Cancel_FromScheduled_Success_CascadesCancelToTheScheduledVisit_AndAuditsBoth()
    {
        var w = await ScheduledAsync();

        var response = await CancelAsync(w.Supervisor, w.WorkOrderId, w.WorkOrderETag, "Customer no longer needs this.");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var workOrder = await WithDbAsync(db => db.WorkOrders.AsNoTracking().SingleAsync(item => item.Id == w.WorkOrderId));
        Assert.Equal(WorkOrderStatus.Cancelled, workOrder.Status);

        var visit = await WithDbAsync(db => db.ServiceVisits.AsNoTracking().SingleAsync(item => item.Id == w.VisitId));
        Assert.Equal(ServiceVisitStatus.Cancelled, visit.Status);
        Assert.Equal("Customer no longer needs this.", visit.CancelReason);

        Assert.Equal(1, await WithDbAsync(db => db.AuditHistory.AsNoTracking().CountAsync(a => a.EntityId == w.WorkOrderId && a.ActionCode == "WORK_ORDER_CANCELLED")));
        var visitAudit = await WithDbAsync(db => db.AuditHistory.AsNoTracking().SingleAsync(a => a.EntityId == w.VisitId && a.ActionCode == "SERVICE_VISIT_CANCELLED"));
        Assert.Equal("Customer no longer needs this.", visitAudit.Reason);
        Assert.Equal(w.Supervisor.UserId, visitAudit.ActorId);
    }

    [Fact]
    public async Task Cancel_FromAwaitingCustomerAcceptance_Success_PreservesTheCompletedVisitHistoryUnchanged()
    {
        var w = await AwaitingAcceptanceAsync();
        var visitBefore = await WithDbAsync(db => db.ServiceVisits.AsNoTracking().SingleAsync(item => item.Id == w.VisitId));
        Assert.Equal(ServiceVisitStatus.Completed, visitBefore.Status);

        var response = await CancelAsync(w.Supervisor, w.WorkOrderId, w.WorkOrderETag, "Cancelled before acceptance.");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var workOrder = await WithDbAsync(db => db.WorkOrders.AsNoTracking().SingleAsync(item => item.Id == w.WorkOrderId));
        Assert.Equal(WorkOrderStatus.Cancelled, workOrder.Status);

        var visitAfter = await WithDbAsync(db => db.ServiceVisits.AsNoTracking().SingleAsync(item => item.Id == w.VisitId));
        Assert.Equal(ServiceVisitStatus.Completed, visitAfter.Status);
        Assert.Equal(visitBefore.CompletedAt, visitAfter.CompletedAt);
        Assert.Null(visitAfter.CancelReason);
        Assert.Equal(0, await WithDbAsync(db => db.AuditHistory.AsNoTracking().CountAsync(a => a.EntityId == w.VisitId && a.ActionCode == "SERVICE_VISIT_CANCELLED")));
    }

    [Fact]
    public async Task Cancel_FromCorrectiveActionRequired_Success_LeavesTheCorrectiveActionRowUntouched()
    {
        var w = await CorrectiveActionRequiredAsync();
        var correctiveActionBefore = await WithDbAsync(db => db.CorrectiveActions.AsNoTracking().SingleAsync(item => item.WorkOrderId == w.WorkOrderId));
        Assert.Equal(CorrectiveActionStatus.Draft, correctiveActionBefore.Status);

        var response = await CancelAsync(w.Supervisor, w.WorkOrderId, w.WorkOrderETag, "Corrective work no longer needed.");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var workOrder = await WithDbAsync(db => db.WorkOrders.AsNoTracking().SingleAsync(item => item.Id == w.WorkOrderId));
        Assert.Equal(WorkOrderStatus.Cancelled, workOrder.Status);

        var correctiveActionAfter = await WithDbAsync(db => db.CorrectiveActions.AsNoTracking().SingleAsync(item => item.Id == correctiveActionBefore.Id));
        Assert.Equal(correctiveActionBefore.Status, correctiveActionAfter.Status);
        Assert.Equal(correctiveActionBefore.RowVersion, correctiveActionAfter.RowVersion);
    }

    // ---------------- Active Service Visit / Work Session ----------------

    [Fact]
    public async Task Cancel_WhileAServiceVisitIsInProgress_Returns409StateConflict_AndWritesNoPartialMutation()
    {
        var w = await InProgressAsync();

        var response = await CancelAsync(w.Supervisor, w.WorkOrderId, w.WorkOrderETag, "Attempted mid-work.");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await JsonAsync(response);
        Assert.Equal("STATE_CONFLICT", problem.GetProperty("code").GetString());

        var workOrder = await WithDbAsync(db => db.WorkOrders.AsNoTracking().SingleAsync(item => item.Id == w.WorkOrderId));
        Assert.Equal(WorkOrderStatus.InProgress, workOrder.Status);
        Assert.Null(workOrder.CancelReason);

        var visit = await WithDbAsync(db => db.ServiceVisits.AsNoTracking().SingleAsync(item => item.Id == w.VisitId));
        Assert.Equal(ServiceVisitStatus.InProgress, visit.Status);
        Assert.Null(visit.CancelReason);

        Assert.Equal(0, await WithDbAsync(db => db.AuditHistory.AsNoTracking().CountAsync(a => a.EntityId == w.WorkOrderId && a.ActionCode == "WORK_ORDER_CANCELLED")));
    }

    // ---------------- Terminal / already-accepted states ----------------

    [Fact]
    public async Task Cancel_AfterCustomerAccepted_Returns409StateConflict_AndWritesNothing()
    {
        var w = await AwaitingAcceptanceAsync();
        var accepted = await SendAsync(HttpMethod.Post, $"{WorkOrders}/{w.WorkOrderId}/accept", w.Owner, null, w.WorkOrderETag);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var freshEtag = $"\"{(await JsonAsync(accepted)).GetProperty("rowVersion").GetString()}\"";

        var response = await CancelAsync(w.Supervisor, w.WorkOrderId, freshEtag, "Too late.");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("STATE_CONFLICT", (await JsonAsync(response)).GetProperty("code").GetString());

        var workOrder = await WithDbAsync(db => db.WorkOrders.AsNoTracking().SingleAsync(item => item.Id == w.WorkOrderId));
        Assert.Equal(WorkOrderStatus.Completed, workOrder.Status);
        Assert.Null(workOrder.CancelReason);
    }

    [Fact]
    public async Task ASecondCancel_OfAnAlreadyCancelledWorkOrder_Returns409StateConflict_AndKeepsTheOriginalReason()
    {
        var w = await OpenAsync();
        var first = await CancelAsync(w.Supervisor, w.WorkOrderId, w.WorkOrderETag, "First reason.");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var freshEtag = first.Headers.ETag!.Tag;

        var second = await CancelAsync(w.Supervisor, w.WorkOrderId, freshEtag, "Second attempt.");

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("STATE_CONFLICT", (await JsonAsync(second)).GetProperty("code").GetString());

        var workOrder = await WithDbAsync(db => db.WorkOrders.AsNoTracking().SingleAsync(item => item.Id == w.WorkOrderId));
        Assert.Equal("First reason.", workOrder.CancelReason);
        Assert.Equal(1, await WithDbAsync(db => db.AuditHistory.AsNoTracking().CountAsync(a => a.EntityId == w.WorkOrderId && a.ActionCode == "WORK_ORDER_CANCELLED")));
    }

    // ---------------- Authorization / validation / concurrency ----------------

    [Fact]
    public async Task Cancel_ByANonSupervisor_Returns403_AndWritesNothing()
    {
        var w = await OpenAsync();

        var response = await CancelAsync(w.Coordinator, w.WorkOrderId, w.WorkOrderETag, "Attempted by the wrong role.");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("ACCESS_DENIED", (await JsonAsync(response)).GetProperty("code").GetString());
        Assert.Equal(WorkOrderStatus.Open, await WithDbAsync(db => db.WorkOrders.AsNoTracking().Where(item => item.Id == w.WorkOrderId).Select(item => item.Status).SingleAsync()));
    }

    [Fact]
    public async Task Cancel_WithoutAReason_Returns422_AndWritesNothing()
    {
        var w = await OpenAsync();

        var response = await CancelAsync(w.Supervisor, w.WorkOrderId, w.WorkOrderETag, null);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal("VALIDATION_FAILED", body.GetProperty("code").GetString());
        Assert.True(body.GetProperty("errors").TryGetProperty("reason", out _));
        Assert.Equal(WorkOrderStatus.Open, await WithDbAsync(db => db.WorkOrders.AsNoTracking().Where(item => item.Id == w.WorkOrderId).Select(item => item.Status).SingleAsync()));
    }

    [Fact]
    public async Task Cancel_WithAStaleRowVersion_Returns409ConcurrencyConflict_AndWritesNothing()
    {
        var w = await OpenAsync();

        var response = await CancelAsync(w.Supervisor, w.WorkOrderId, StaleETag, "Stale attempt.");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("CONCURRENCY_CONFLICT", (await JsonAsync(response)).GetProperty("code").GetString());
        Assert.Equal(WorkOrderStatus.Open, await WithDbAsync(db => db.WorkOrders.AsNoTracking().Where(item => item.Id == w.WorkOrderId).Select(item => item.Status).SingleAsync()));
    }

    [Fact]
    public async Task TwoConcurrentCancels_OfTheSameWorkOrder_ExactlyOneSucceeds_NoPartialUpdate()
    {
        var w = await OpenAsync();

        var responses = await Task.WhenAll(
            Task.Run(() => CancelAsync(w.Supervisor, w.WorkOrderId, w.WorkOrderETag, "Race attempt A.")),
            Task.Run(() => CancelAsync(w.Supervisor, w.WorkOrderId, w.WorkOrderETag, "Race attempt B.")));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal(1, await WithDbAsync(db => db.AuditHistory.AsNoTracking().CountAsync(a => a.EntityId == w.WorkOrderId && a.ActionCode == "WORK_ORDER_CANCELLED")));

        var workOrder = await WithDbAsync(db => db.WorkOrders.AsNoTracking().SingleAsync(item => item.Id == w.WorkOrderId));
        Assert.Equal(WorkOrderStatus.Cancelled, workOrder.Status);
    }

    // ---------------- Site scope ----------------

    [Fact]
    public async Task Cancel_ByASupervisorOutsideTheWorkOrdersSite_Returns404_AndWritesNothing()
    {
        var w = await OpenAsync();
        var otherSite = await SiteAsync(w.TenantId);
        var outsider = await CallerAsync(w.TenantId, [otherSite], RoleCodes.Supervisor);
        var before = await FootprintAsync(w);

        var response = await CancelAsync(outsider, w.WorkOrderId, w.WorkOrderETag, "Not my Site.");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("NOT_FOUND", (await JsonAsync(response)).GetProperty("code").GetString());
        var workOrder = await WithDbAsync(db => db.WorkOrders.AsNoTracking().SingleAsync(item => item.Id == w.WorkOrderId));
        Assert.Equal(WorkOrderStatus.Open, workOrder.Status);
        Assert.Null(workOrder.CancelReason);
        Assert.Equal(before, await FootprintAsync(w));
    }

    // ---------------- Success: the remaining allowed source states ----------------

    [Fact]
    public async Task Cancel_FromAwaitingSupervisorReview_Success_PreservesTheCompletedVisitHistoryUnchanged()
    {
        var w = await AwaitingReviewAsync();
        var visitBefore = await WithDbAsync(db => db.ServiceVisits.AsNoTracking().SingleAsync(item => item.Id == w.VisitId));
        Assert.Equal(ServiceVisitStatus.Completed, visitBefore.Status);

        var response = await CancelAsync(w.Supervisor, w.WorkOrderId, w.WorkOrderETag, "Cancelled during supervisor review.");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var audit = await WithDbAsync(db => db.AuditHistory.AsNoTracking().SingleAsync(a => a.EntityId == w.WorkOrderId && a.ActionCode == "WORK_ORDER_CANCELLED"));
        Assert.Equal("AWAITING_SUPERVISOR_REVIEW", audit.FromState);
        Assert.Equal("CANCELLED", audit.ToState);

        var visitAfter = await WithDbAsync(db => db.ServiceVisits.AsNoTracking().SingleAsync(item => item.Id == w.VisitId));
        Assert.Equal(ServiceVisitStatus.Completed, visitAfter.Status);
        Assert.Equal(visitBefore.CompletedAt, visitAfter.CompletedAt);
        Assert.Equal(visitBefore.RowVersion, visitAfter.RowVersion);
        Assert.Null(visitAfter.CancelReason);
    }

    [Fact]
    public async Task Cancel_FromCorrectivePlanPending_Success_LeavesThePendingCorrectiveActionUntouched()
    {
        var c = await PlanPendingAsync();
        var before = await WithDbAsync(db => db.CorrectiveActions.AsNoTracking().SingleAsync(item => item.Id == c.CorrectiveActionId));
        Assert.Equal(CorrectiveActionStatus.PendingPlanApproval, before.Status);

        var response = await CancelAsync(c.W.Supervisor, c.W.WorkOrderId, c.W.WorkOrderETag, "Plan no longer needed.");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var audit = await WithDbAsync(db => db.AuditHistory.AsNoTracking().SingleAsync(a => a.EntityId == c.W.WorkOrderId && a.ActionCode == "WORK_ORDER_CANCELLED"));
        Assert.Equal("CORRECTIVE_PLAN_PENDING", audit.FromState);

        var after = await WithDbAsync(db => db.CorrectiveActions.AsNoTracking().SingleAsync(item => item.Id == c.CorrectiveActionId));
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.RowVersion, after.RowVersion);
        Assert.Equal(before.CorrectiveServiceVisitId, after.CorrectiveServiceVisitId);
    }

    [Fact]
    public async Task Cancel_FromCorrectivePlanApproved_Success_LeavesTheApprovedCorrectiveActionUntouched_AndCreatesNoVisit()
    {
        var c = await PlanApprovedAsync();
        var before = await WithDbAsync(db => db.CorrectiveActions.AsNoTracking().SingleAsync(item => item.Id == c.CorrectiveActionId));
        Assert.Equal(CorrectiveActionStatus.Approved, before.Status);
        var visitCountBefore = await WithDbAsync(db => db.ServiceVisits.AsNoTracking().CountAsync(v => v.WorkOrderId == c.W.WorkOrderId));

        var response = await CancelAsync(c.W.Supervisor, c.W.WorkOrderId, c.W.WorkOrderETag, "Rework no longer needed.");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var audit = await WithDbAsync(db => db.AuditHistory.AsNoTracking().SingleAsync(a => a.EntityId == c.W.WorkOrderId && a.ActionCode == "WORK_ORDER_CANCELLED"));
        Assert.Equal("CORRECTIVE_PLAN_APPROVED", audit.FromState);

        var after = await WithDbAsync(db => db.CorrectiveActions.AsNoTracking().SingleAsync(item => item.Id == c.CorrectiveActionId));
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.RowVersion, after.RowVersion);
        Assert.Null(after.CorrectiveServiceVisitId);
        Assert.Equal(visitCountBefore, await WithDbAsync(db => db.ServiceVisits.AsNoTracking().CountAsync(v => v.WorkOrderId == c.W.WorkOrderId)));
    }

    // ---------------- Mixed Service Visit history ----------------

    [Fact]
    public async Task Cancel_WithMixedVisitHistory_CancelsOnlyTheScheduledVisit_AndPreservesCompletedAndMissedHistory()
    {
        // V1: SCHEDULED -> MISSED -> FOLLOW_UP decision creates V2. V2: checked in/out -> COMPLETED -> Reject -> Corrective
        // plan submitted/approved -> Schedule Rework creates V3 (SCHEDULED). Cancel then meets MISSED + COMPLETED + SCHEDULED.
        var w = await ScheduledAsync();
        var v1 = w.VisitId!.Value;
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Post, $"{Visits}/{v1}/mark-missed", w.Coordinator,
            new { reason = "Technician could not reach the site" }, await VisitETagAsync(v1, w.Supervisor))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Post, $"{Visits}/{v1}/missed-decision", w.Coordinator, new
        {
            decision = "FOLLOW_UP",
            reason = "Follow up with a new visit",
            newSchedule = new
            {
                assignedTeamId = Guid.NewGuid(),
                assignedTechnicianId = w.Technician.UserId,
                scheduledStartAt = "2026-10-05T08:00:00Z",
                scheduledEndAt = "2026-10-05T10:00:00Z"
            }
        }, await VisitETagAsync(v1, w.Supervisor))).StatusCode);
        var v2 = await WithDbAsync(db => db.ServiceVisits.AsNoTracking().Where(v => v.WorkOrderId == w.WorkOrderId && v.SourceMissedVisitId == v1).Select(v => v.Id).SingleAsync());

        var rejected = await RejectAsync(await ToAwaitingAcceptanceAsync(await ToAwaitingReviewAsync(await CheckInAsync(w with { VisitId = v2 }))));
        var corrective = await ApprovePlanFromAsync(await SubmitPlanFromAsync(await DraftCorrectiveFromAsync(rejected)));
        var v3 = await ScheduleReworkAsync(corrective);
        var etag = await WorkOrderETagAsync(w.WorkOrderId, w.Supervisor);

        var missedBefore = await WithDbAsync(db => db.ServiceVisits.AsNoTracking().SingleAsync(v => v.Id == v1));
        var completedBefore = await WithDbAsync(db => db.ServiceVisits.AsNoTracking().SingleAsync(v => v.Id == v2));
        var scheduledBefore = await WithDbAsync(db => db.ServiceVisits.AsNoTracking().SingleAsync(v => v.Id == v3));
        Assert.Equal(ServiceVisitStatus.Missed, missedBefore.Status);
        Assert.Equal(ServiceVisitStatus.Completed, completedBefore.Status);
        Assert.Equal(ServiceVisitStatus.Scheduled, scheduledBefore.Status);
        var correctiveBefore = await WithDbAsync(db => db.CorrectiveActions.AsNoTracking().SingleAsync(item => item.Id == corrective.CorrectiveActionId));
        var historyBefore = await WithDbAsync(db => db.AuditHistory.AsNoTracking().CountAsync(a => a.TenantId == w.TenantId && (a.EntityId == v1 || a.EntityId == v2)));
        Assert.True(historyBefore > 0);

        var response = await CancelAsync(w.Supervisor, w.WorkOrderId, etag, "Cancelled with mixed visit history.");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var scheduledAfter = await WithDbAsync(db => db.ServiceVisits.AsNoTracking().SingleAsync(v => v.Id == v3));
        Assert.Equal(ServiceVisitStatus.Cancelled, scheduledAfter.Status);
        Assert.Equal("Cancelled with mixed visit history.", scheduledAfter.CancelReason);
        Assert.Equal(1, await WithDbAsync(db => db.AuditHistory.AsNoTracking().CountAsync(a => a.EntityId == v3 && a.ActionCode == "SERVICE_VISIT_CANCELLED")));

        var missedAfter = await WithDbAsync(db => db.ServiceVisits.AsNoTracking().SingleAsync(v => v.Id == v1));
        Assert.Equal(ServiceVisitStatus.Missed, missedAfter.Status);
        Assert.Equal(missedBefore.RowVersion, missedAfter.RowVersion);
        Assert.Equal(missedBefore.MissedDecisionCode, missedAfter.MissedDecisionCode);
        Assert.Equal(missedBefore.MissedReason, missedAfter.MissedReason);
        Assert.Null(missedAfter.CancelReason);

        var completedAfter = await WithDbAsync(db => db.ServiceVisits.AsNoTracking().SingleAsync(v => v.Id == v2));
        Assert.Equal(ServiceVisitStatus.Completed, completedAfter.Status);
        Assert.Equal(completedBefore.RowVersion, completedAfter.RowVersion);
        Assert.Equal(completedBefore.CompletedAt, completedAfter.CompletedAt);
        Assert.Null(completedAfter.CancelReason);

        Assert.Equal(historyBefore, await WithDbAsync(db => db.AuditHistory.AsNoTracking().CountAsync(a => a.TenantId == w.TenantId && (a.EntityId == v1 || a.EntityId == v2))));
        Assert.Equal(0, await WithDbAsync(db => db.AuditHistory.AsNoTracking().CountAsync(a => (a.EntityId == v1 || a.EntityId == v2) && a.ActionCode == "SERVICE_VISIT_CANCELLED")));

        var correctiveAfter = await WithDbAsync(db => db.CorrectiveActions.AsNoTracking().SingleAsync(item => item.Id == corrective.CorrectiveActionId));
        Assert.Equal(correctiveBefore.Status, correctiveAfter.Status);
        Assert.Equal(correctiveBefore.RowVersion, correctiveAfter.RowVersion);
        Assert.Equal(correctiveBefore.CorrectiveServiceVisitId, correctiveAfter.CorrectiveServiceVisitId);
    }

    // ---------------- After CANCELLED, every mutation command is rejected (UC-WO-026 Postcondition: terminal) ----------------

    [Fact]
    public async Task AfterCancel_FromAwaitingCustomerAcceptance_EveryWorkOrderVisitAndWorkSessionCommandIsRejectedWith409()
    {
        var w = await AwaitingAcceptanceAsync();
        var eTag = await CancelOkAsync(w, w.WorkOrderETag);
        var visitId = w.VisitId!.Value;
        var visitETag = await VisitETagAsync(visitId, w.Supervisor);
        var session = await SessionAsync(visitId);
        var wo = $"{WorkOrders}/{w.WorkOrderId}";
        var visit = $"{Visits}/{visitId}";
        var work = $"{Sessions}/{session.Id}";

        await AssertAllRejectedAsync(w,
            ("work-order schedule", () => SendAsync(HttpMethod.Post, $"{wo}/schedule", w.Coordinator, new
            {
                ownerTeamId = Guid.NewGuid(),
                assignedTechnicianId = w.Technician.UserId,
                scheduledStartAt = "2026-10-01T08:00:00Z",
                scheduledEndAt = "2026-10-01T10:00:00Z"
            }, eTag)),
            ("submit-work-summary", () => SendAsync(HttpMethod.Post, $"{wo}/submit-work-summary", w.Technician, new { summaryText = "Again.", repairOutcomeCode = "REPAIRED" }, eTag)),
            ("submit-for-acceptance", () => SendAsync(HttpMethod.Post, $"{wo}/submit-for-acceptance", w.TeamLead, new { acceptanceContactId = w.Owner.UserId }, eTag)),
            ("accept", () => SendAsync(HttpMethod.Post, $"{wo}/accept", w.Owner, null, eTag)),
            ("reject", () => SendAsync(HttpMethod.Post, $"{wo}/reject", w.Owner, new { decisionReason = "Too late." }, eTag)),
            ("cost-summary", () => SendAsync(HttpMethod.Put, $"{wo}/cost-summary", w.TeamLead, new { totalAmount = 10m, currencyCode = "USD", note = "x" }, eTag)),
            ("close", () => SendAsync(HttpMethod.Post, $"{wo}/close", w.Supervisor, null, eTag)),
            ("cancel again", () => CancelAsync(w.Supervisor, w.WorkOrderId, eTag, "Again.")),
            ("visit check-in", () => SendAsync(HttpMethod.Post, $"{visit}/check-in", w.Technician, null, visitETag)),
            ("visit reschedule", () => SendAsync(HttpMethod.Post, $"{visit}/reschedule", w.Coordinator, new { reason = "x", scheduledStartAt = "2026-10-02T08:00:00Z", scheduledEndAt = "2026-10-02T10:00:00Z" }, visitETag)),
            ("visit reassign", () => SendAsync(HttpMethod.Post, $"{visit}/reassign", w.Coordinator, new { reason = "x", assignedTeamId = Guid.NewGuid(), assignedTechnicianId = w.Technician.UserId }, visitETag)),
            ("visit cancel", () => SendAsync(HttpMethod.Post, $"{visit}/cancel", w.Coordinator, new { reason = "x" }, visitETag)),
            ("visit mark-missed", () => SendAsync(HttpMethod.Post, $"{visit}/mark-missed", w.Coordinator, new { reason = "x" }, visitETag)),
            ("work-session pause", () => SendAsync(HttpMethod.Post, $"{work}/pause", w.Technician, new { reason = "x" }, session.ETag)),
            ("work-session resume", () => SendAsync(HttpMethod.Post, $"{work}/resume", w.Technician, null, session.ETag)),
            ("work-session check-out", () => SendAsync(HttpMethod.Post, $"{work}/check-out", w.Technician, null, session.ETag)));

        // A Cost Summary only ever exists for a COMPLETED Work Order, and a Work Order cancelled before Accept never
        // reaches COMPLETED — so Review has nothing to act on and answers 404 (still a rejection, nothing written).
        var review = await SendAsync(HttpMethod.Post, $"{wo}/review-cost-summary", w.Supervisor, null, eTag);
        Assert.Equal(HttpStatusCode.NotFound, review.StatusCode);
        Assert.Equal("NOT_FOUND", (await JsonAsync(review)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task AfterCancel_FromScheduled_EveryVisitCommandOnTheCancelledVisitIsRejectedWith409()
    {
        var w = await ScheduledAsync();
        await CancelOkAsync(w, w.WorkOrderETag);
        var visitETag = await VisitETagAsync(w.VisitId!.Value, w.Supervisor);
        var visit = $"{Visits}/{w.VisitId}";

        await AssertAllRejectedAsync(w,
            ("visit check-in", () => SendAsync(HttpMethod.Post, $"{visit}/check-in", w.Technician, null, visitETag)),
            ("visit reschedule", () => SendAsync(HttpMethod.Post, $"{visit}/reschedule", w.Coordinator, new { reason = "x", scheduledStartAt = "2026-10-02T08:00:00Z", scheduledEndAt = "2026-10-02T10:00:00Z" }, visitETag)),
            ("visit reassign", () => SendAsync(HttpMethod.Post, $"{visit}/reassign", w.Coordinator, new { reason = "x", assignedTeamId = Guid.NewGuid(), assignedTechnicianId = w.Technician.UserId }, visitETag)),
            ("visit cancel", () => SendAsync(HttpMethod.Post, $"{visit}/cancel", w.Coordinator, new { reason = "x" }, visitETag)),
            ("visit mark-missed", () => SendAsync(HttpMethod.Post, $"{visit}/mark-missed", w.Coordinator, new { reason = "x" }, visitETag)),
            ("visit missed-decision", () => SendAsync(HttpMethod.Post, $"{visit}/missed-decision", w.Coordinator, new { decision = "NO_FOLLOW_UP", reason = "x" }, visitETag)));
    }

    [Fact]
    public async Task AfterCancel_WithAnUndecidedMissedVisit_EveryMissedDecisionIsRejectedWith409_AndCreatesNoVisit()
    {
        var w = await MissedUndecidedAsync();
        await CancelOkAsync(w, w.WorkOrderETag);
        var visit = $"{Visits}/{w.VisitId}";
        var visitETag = await VisitETagAsync(w.VisitId!.Value, w.Supervisor);
        var newSchedule = new
        {
            assignedTeamId = Guid.NewGuid(),
            assignedTechnicianId = w.Technician.UserId,
            scheduledStartAt = "2026-10-05T08:00:00Z",
            scheduledEndAt = "2026-10-05T10:00:00Z"
        };

        await AssertAllRejectedAsync(w,
            ("missed-decision RESCHEDULE", () => SendAsync(HttpMethod.Post, $"{visit}/missed-decision", w.Coordinator, new { decision = "RESCHEDULE", reason = "x", newSchedule }, visitETag)),
            ("missed-decision FOLLOW_UP", () => SendAsync(HttpMethod.Post, $"{visit}/missed-decision", w.Coordinator, new { decision = "FOLLOW_UP", reason = "x", newSchedule }, visitETag)),
            ("missed-decision REASSIGN", () => SendAsync(HttpMethod.Post, $"{visit}/missed-decision", w.Coordinator, new { decision = "REASSIGN", reason = "x", newSchedule }, visitETag)),
            ("missed-decision NO_FOLLOW_UP", () => SendAsync(HttpMethod.Post, $"{visit}/missed-decision", w.Coordinator, new { decision = "NO_FOLLOW_UP", reason = "x" }, visitETag)));

        var missed = await WithDbAsync(db => db.ServiceVisits.AsNoTracking().SingleAsync(v => v.Id == w.VisitId));
        Assert.Equal(ServiceVisitStatus.Missed, missed.Status);
        Assert.Null(missed.MissedDecisionCode);
        Assert.Equal(1, await WithDbAsync(db => db.ServiceVisits.AsNoTracking().CountAsync(v => v.WorkOrderId == w.WorkOrderId)));
    }

    [Fact]
    public async Task AfterCancel_FromCorrectiveActionRequired_SubmitAndApprovePlanAndScheduleReworkAreRejectedWith409_NotA500()
    {
        var c = await DraftCorrectiveAsync();
        var eTag = await CancelOkAsync(c.W, c.W.WorkOrderETag);

        await AssertAllRejectedAsync(c.W, CorrectiveCommands(c, eTag));
        Assert.Equal(CorrectiveActionStatus.Draft, (await WithDbAsync(db => db.CorrectiveActions.AsNoTracking().SingleAsync(item => item.Id == c.CorrectiveActionId))).Status);
    }

    [Fact]
    public async Task AfterCancel_FromCorrectivePlanPending_SubmitAndApprovePlanAndScheduleReworkAreRejectedWith409_NotA500()
    {
        var c = await PlanPendingAsync();
        var eTag = await CancelOkAsync(c.W, c.W.WorkOrderETag);

        await AssertAllRejectedAsync(c.W, CorrectiveCommands(c, eTag));
        Assert.Equal(CorrectiveActionStatus.PendingPlanApproval, (await WithDbAsync(db => db.CorrectiveActions.AsNoTracking().SingleAsync(item => item.Id == c.CorrectiveActionId))).Status);
    }

    [Fact]
    public async Task AfterCancel_FromCorrectivePlanApproved_ScheduleReworkSubmitAndApprovePlanAreRejectedWith409_AndCreateNoCorrectiveVisit()
    {
        var c = await PlanApprovedAsync();
        var eTag = await CancelOkAsync(c.W, c.W.WorkOrderETag);

        await AssertAllRejectedAsync(c.W, CorrectiveCommands(c, eTag));

        var correctiveAction = await WithDbAsync(db => db.CorrectiveActions.AsNoTracking().SingleAsync(item => item.Id == c.CorrectiveActionId));
        Assert.Equal(CorrectiveActionStatus.Approved, correctiveAction.Status);
        Assert.Null(correctiveAction.CorrectiveServiceVisitId);
        Assert.Equal(0, await WithDbAsync(db => db.ServiceVisits.AsNoTracking().CountAsync(v => v.WorkOrderId == c.W.WorkOrderId && v.VisitType == ServiceVisitType.Corrective)));
    }

    private (string Name, Func<Task<HttpResponseMessage>> Send)[] CorrectiveCommands(Corrective c, string workOrderETag) =>
    [
        ("corrective submit-plan", () => SendAsync(HttpMethod.Post, $"{CorrectiveActions}/{c.CorrectiveActionId}/submit-plan", c.W.TeamLead,
            new { planText = "Late plan.", planFileAssetId = c.PlanFileId }, workOrderETag)),
        ("corrective approve-plan", () => SendAsync(HttpMethod.Post, $"{CorrectiveActions}/{c.CorrectiveActionId}/approve-plan", c.W.Supervisor, null, workOrderETag)),
        ("corrective schedule-rework", async () => await SendAsync(HttpMethod.Post, $"{CorrectiveActions}/{c.CorrectiveActionId}/schedule-rework", c.W.Coordinator,
            ReworkBody(c.W), await CorrectiveActionETagAsync(c.CorrectiveActionId)))
    ];

    // ---------------- Helpers ----------------

    private static JsonElement SingleVisit(JsonElement workOrderResponse) =>
        Assert.Single(workOrderResponse.GetProperty("visits").EnumerateArray());

    /// <summary>Draft -&gt; Submit -&gt; Approve -&gt; Convert. The Work Order is OPEN, no Visits yet.</summary>
    private async Task<Built> OpenAsync()
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
            var evidence = new FileAsset(tenantId, "evidence.png", "image/png", 1024, ValidHash, $"{tenantId:N}/{Guid.NewGuid():N}", owner.UserId, DateTime.UtcNow);
            evidence.MarkClean();
            db.FileAssets.Add(evidence);
            db.RepairRequestAttachments.Add(new RepairRequestAttachment(id, evidence.Id, AttachmentFileRules.AccessScopeCode));
            await db.SaveChangesAsync();
        });

        var submitted = await SendAsync(HttpMethod.Post, $"{Requests}/{id}/submit", owner, new { duplicateContinuationReason = (string?)null }, draftETag);
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
        var approved = await SendAsync(HttpMethod.Post, $"{Requests}/{id}/approve", approver, null, submitted.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        var converted = await SendAsync(HttpMethod.Post, $"{Requests}/{id}/convert-to-work-order", coordinator, null, approved.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.OK, converted.StatusCode);
        var workOrderId = (await JsonAsync(converted)).GetProperty("workOrderId").GetGuid();

        var etag = await WorkOrderETagAsync(workOrderId, supervisor);
        return new Built(tenantId, site, owner, approver, coordinator, technician, teamLead, supervisor, workOrderId, null, etag);
    }

    /// <summary>OpenAsync() + Schedule. The Work Order is SCHEDULED with one SCHEDULED Visit.</summary>
    private async Task<Built> ScheduledAsync()
    {
        var w = await OpenAsync();

        var scheduled = await SendAsync(HttpMethod.Post, $"{WorkOrders}/{w.WorkOrderId}/schedule", w.Coordinator, new
        {
            ownerTeamId = Guid.NewGuid(),
            assignedTechnicianId = w.Technician.UserId,
            scheduledStartAt = "2026-10-01T08:00:00Z",
            scheduledEndAt = "2026-10-01T10:00:00Z"
        }, w.WorkOrderETag);
        Assert.Equal(HttpStatusCode.OK, scheduled.StatusCode);
        var visit = SingleVisit(await JsonAsync(scheduled));
        var visitId = visit.GetProperty("serviceVisitId").GetGuid();

        var etag = await WorkOrderETagAsync(w.WorkOrderId, w.Supervisor);
        return w with { VisitId = visitId, WorkOrderETag = etag };
    }

    /// <summary>ScheduledAsync() + Check-in. The Work Order is IN_PROGRESS; the Visit is IN_PROGRESS, not yet checked out.</summary>
    private async Task<Built> InProgressAsync() => await CheckInAsync(await ScheduledAsync());

    /// <summary>Checks the Technician into <c>w.VisitId</c> (a SCHEDULED Visit). Returns the Work Order with a fresh ETag.</summary>
    private async Task<Built> CheckInAsync(Built w)
    {
        var visitEtag = await VisitETagAsync(w.VisitId!.Value, w.Supervisor);

        var checkedIn = await SendAsync(HttpMethod.Post, $"{Visits}/{w.VisitId}/check-in", w.Technician, null, visitEtag);
        Assert.Equal(HttpStatusCode.OK, checkedIn.StatusCode);

        var etag = await WorkOrderETagAsync(w.WorkOrderId, w.Supervisor);
        return w with { WorkOrderETag = etag };
    }

    /// <summary>InProgressAsync() + Check-out + Submit Work Summary. AWAITING_SUPERVISOR_REVIEW; the Visit is COMPLETED.</summary>
    private async Task<Built> AwaitingReviewAsync() => await ToAwaitingReviewAsync(await InProgressAsync());

    private async Task<Built> ToAwaitingReviewAsync(Built w)
    {
        var current = await JsonAsync(await SendAsync(HttpMethod.Get, $"{Sessions}/current", w.Technician, null, null));
        var sessionId = current.GetProperty("workSessionId").GetGuid();
        var sessionETag = $"\"{current.GetProperty("rowVersion").GetString()}\"";

        var checkedOut = await SendAsync(HttpMethod.Post, $"{Sessions}/{sessionId}/check-out", w.Technician, null, sessionETag);
        Assert.Equal(HttpStatusCode.OK, checkedOut.StatusCode);
        var workOrderETag = $"\"{(await JsonAsync(checkedOut)).GetProperty("workOrderRowVersion").GetString()}\"";

        var summarySubmitted = await SendAsync(HttpMethod.Post, $"{WorkOrders}/{w.WorkOrderId}/submit-work-summary", w.Technician,
            new { summaryText = "Replaced the pump seal.", repairOutcomeCode = "REPAIRED" }, workOrderETag);
        Assert.Equal(HttpStatusCode.OK, summarySubmitted.StatusCode);
        var reviewEtag = $"\"{(await JsonAsync(summarySubmitted)).GetProperty("rowVersion").GetString()}\"";

        return w with { WorkOrderETag = reviewEtag };
    }

    /// <summary>AwaitingReviewAsync() + Submit for Acceptance. AWAITING_CUSTOMER_ACCEPTANCE; the Visit is COMPLETED.</summary>
    private async Task<Built> AwaitingAcceptanceAsync() => await ToAwaitingAcceptanceAsync(await AwaitingReviewAsync());

    private async Task<Built> ToAwaitingAcceptanceAsync(Built w)
    {
        var submittedForAcceptance = await SendAsync(HttpMethod.Post, $"{WorkOrders}/{w.WorkOrderId}/submit-for-acceptance", w.TeamLead,
            new { acceptanceContactId = w.Owner.UserId }, w.WorkOrderETag);
        Assert.Equal(HttpStatusCode.OK, submittedForAcceptance.StatusCode);
        var awaitingEtag = $"\"{(await JsonAsync(submittedForAcceptance)).GetProperty("rowVersion").GetString()}\"";

        return w with { WorkOrderETag = awaitingEtag };
    }

    /// <summary>AwaitingAcceptanceAsync() + Reject. CORRECTIVE_ACTION_REQUIRED, with a DRAFT Corrective Action row.</summary>
    private async Task<Built> CorrectiveActionRequiredAsync() => await RejectAsync(await AwaitingAcceptanceAsync());

    private async Task<Built> RejectAsync(Built awaiting)
    {
        var rejected = await SendAsync(HttpMethod.Post, $"{WorkOrders}/{awaiting.WorkOrderId}/reject", awaiting.Owner, new { decisionReason = "Leak persists." }, awaiting.WorkOrderETag);
        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);

        var etag = await WorkOrderETagAsync(awaiting.WorkOrderId, awaiting.Supervisor);
        return awaiting with { WorkOrderETag = etag };
    }

    private sealed record Corrective(Built W, Guid CorrectiveActionId, Guid PlanFileId);

    /// <summary>CorrectiveActionRequiredAsync() + the Corrective Action's id and a CLEAN plan file. The Corrective Action is DRAFT.</summary>
    private async Task<Corrective> DraftCorrectiveAsync() => await DraftCorrectiveFromAsync(await CorrectiveActionRequiredAsync());

    private async Task<Corrective> DraftCorrectiveFromAsync(Built w)
    {
        var correctiveActionId = await WithDbAsync(db => db.CorrectiveActions.AsNoTracking().Where(item => item.WorkOrderId == w.WorkOrderId).Select(item => item.Id).SingleAsync());
        var planFileId = await WithDbAsync(async db =>
        {
            var file = new FileAsset(w.TenantId, "plan.pdf", "application/pdf", 512, ValidHash, $"{w.TenantId:N}/{Guid.NewGuid():N}", w.TeamLead.UserId, DateTime.UtcNow);
            file.MarkClean();
            db.FileAssets.Add(file);
            await db.SaveChangesAsync();
            return file.Id;
        });
        return new Corrective(w, correctiveActionId, planFileId);
    }

    /// <summary>DraftCorrectiveAsync() + Submit Plan. CORRECTIVE_PLAN_PENDING; the Corrective Action is PENDING_PLAN_APPROVAL.</summary>
    private async Task<Corrective> PlanPendingAsync() => await SubmitPlanFromAsync(await DraftCorrectiveAsync());

    private async Task<Corrective> SubmitPlanFromAsync(Corrective c)
    {
        var submitted = await SendAsync(HttpMethod.Post, $"{CorrectiveActions}/{c.CorrectiveActionId}/submit-plan", c.W.TeamLead,
            new { planText = "Replace the seal and retest.", planFileAssetId = c.PlanFileId }, c.W.WorkOrderETag);
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);

        return c with { W = c.W with { WorkOrderETag = await WorkOrderETagAsync(c.W.WorkOrderId, c.W.Supervisor) } };
    }

    /// <summary>PlanPendingAsync() + Approve Plan. CORRECTIVE_PLAN_APPROVED; the Corrective Action is APPROVED with no rework Visit yet.</summary>
    private async Task<Corrective> PlanApprovedAsync() => await ApprovePlanFromAsync(await PlanPendingAsync());

    private async Task<Corrective> ApprovePlanFromAsync(Corrective c)
    {
        var approved = await SendAsync(HttpMethod.Post, $"{CorrectiveActions}/{c.CorrectiveActionId}/approve-plan", c.W.Supervisor, null, c.W.WorkOrderETag);
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);

        return c with { W = c.W with { WorkOrderETag = await WorkOrderETagAsync(c.W.WorkOrderId, c.W.Supervisor) } };
    }

    /// <summary>Schedule Rework (Coordinator) on an APPROVED Corrective Action; returns the new corrective Visit's id.</summary>
    private async Task<Guid> ScheduleReworkAsync(Corrective c)
    {
        var response = await SendAsync(HttpMethod.Post, $"{CorrectiveActions}/{c.CorrectiveActionId}/schedule-rework", c.W.Coordinator, ReworkBody(c.W), await CorrectiveActionETagAsync(c.CorrectiveActionId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await WithDbAsync(db => db.ServiceVisits.AsNoTracking().Where(v => v.WorkOrderId == c.W.WorkOrderId && v.VisitType == ServiceVisitType.Corrective).Select(v => v.Id).SingleAsync());
    }

    private static object ReworkBody(Built w) => new
    {
        assignedTeamId = Guid.NewGuid(),
        assignedTechnicianId = w.Technician.UserId,
        scheduledStartAt = "2026-10-05T08:00:00Z",
        scheduledEndAt = "2026-10-05T10:00:00Z"
    };

    private async Task<string> CorrectiveActionETagAsync(Guid correctiveActionId)
    {
        var correctiveAction = await WithDbAsync(db => db.CorrectiveActions.AsNoTracking().SingleAsync(item => item.Id == correctiveActionId));
        return $"\"{Convert.ToBase64String(correctiveAction.RowVersion)}\"";
    }

    /// <summary>A Work Order with a MISSED Visit whose follow-up decision is still undecided (the WO is SCHEDULED, no SCHEDULED Visit remains).</summary>
    private async Task<Built> MissedUndecidedAsync()
    {
        var w = await ScheduledAsync();
        var missed = await SendAsync(HttpMethod.Post, $"{Visits}/{w.VisitId}/mark-missed", w.Coordinator, new { reason = "Technician could not reach the site" }, await VisitETagAsync(w.VisitId!.Value, w.Supervisor));
        Assert.Equal(HttpStatusCode.OK, missed.StatusCode);
        return w with { WorkOrderETag = await WorkOrderETagAsync(w.WorkOrderId, w.Supervisor) };
    }

    private async Task<(Guid Id, string ETag)> SessionAsync(Guid visitId)
    {
        var session = await WithDbAsync(db => db.WorkSessions.AsNoTracking().SingleAsync(item => item.ServiceVisitId == visitId));
        return (session.Id, $"\"{Convert.ToBase64String(session.RowVersion)}\"");
    }

    /// <summary>Cancels the Work Order (must succeed) and returns the fresh Work Order ETag, so every later 409 is a state guard, never a stale token.</summary>
    private async Task<string> CancelOkAsync(Built w, string ifMatch, string reason = "Cancelled for the post-cancel checks.")
    {
        var response = await CancelAsync(w.Supervisor, w.WorkOrderId, ifMatch, reason);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await WorkOrderETagAsync(w.WorkOrderId, w.Supervisor);
    }

    private async Task<(int Audits, int Visits, int CorrectiveActions)> FootprintAsync(Built w) =>
        await WithDbAsync(async db => (
            await db.AuditHistory.AsNoTracking().CountAsync(a => a.TenantId == w.TenantId),
            await db.ServiceVisits.AsNoTracking().CountAsync(v => v.WorkOrderId == w.WorkOrderId),
            await db.CorrectiveActions.AsNoTracking().CountAsync(c => c.WorkOrderId == w.WorkOrderId)));

    /// <summary>Every command must answer 409 STATE_CONFLICT (never 200, 404, 422 or 500) and, together, write nothing.</summary>
    private async Task AssertAllRejectedAsync(Built w, params (string Name, Func<Task<HttpResponseMessage>> Send)[] commands)
    {
        var before = await FootprintAsync(w);
        var failures = new List<string>();
        foreach (var (name, send) in commands)
        {
            var response = await send();
            string? code = null;
            try
            {
                code = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString();
            }
            catch (Exception)
            {
                // Not a problem+json body: reported below through the status code.
            }

            if (response.StatusCode != HttpStatusCode.Conflict || code != "STATE_CONFLICT")
            {
                failures.Add($"{name} -> {(int)response.StatusCode} {code}");
            }
        }

        Assert.True(failures.Count == 0, "Expected 409 STATE_CONFLICT on a CANCELLED Work Order, but got: " + string.Join("; ", failures));
        Assert.Equal(before, await FootprintAsync(w));
        Assert.Equal(WorkOrderStatus.Cancelled, await WithDbAsync(db => db.WorkOrders.AsNoTracking().Where(item => item.Id == w.WorkOrderId).Select(item => item.Status).SingleAsync()));
    }

    private async Task<HttpResponseMessage> CancelAsync(Caller caller, Guid workOrderId, string ifMatch, string? reason) =>
        await SendAsync(HttpMethod.Post, $"{WorkOrders}/{workOrderId}/cancel", caller, new { reason }, ifMatch);

    private async Task<string> WorkOrderETagAsync(Guid workOrderId, Caller caller)
    {
        var response = await SendAsync(HttpMethod.Get, $"{WorkOrders}/{workOrderId}", caller, null, null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return response.Headers.ETag!.Tag;
    }

    private async Task<string> VisitETagAsync(Guid visitId, Caller caller)
    {
        var visit = await WithDbAsync(db => db.ServiceVisits.AsNoTracking().SingleAsync(item => item.Id == visitId));
        return $"\"{Convert.ToBase64String(visit.RowVersion)}\"";
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
}
