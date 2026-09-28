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

/// <summary>API host with its own disposable LocalDB database for the Cost Summary Read/Review endpoint tests.</summary>
public sealed class CostSummaryReviewApiFactory : AuthApiFactory
{
    public override string ConnectionString =>
        "Server=(localdb)\\MSSQLLocalDB;Database=RepairRequestDb_CostSummaryReviewApiTest;Trusted_Connection=True;TrustServerCertificate=True";
}

/// <summary>
/// Cost Summary Read (`docs/13` §4.19, technical addition) and CST-API-002 Review (Supervisor only; BR-08;
/// `docs/13` §4.19) end to end. Review requires Separation of Duties (the reviewer must not be the preparer,
/// even holding both Team Lead and Supervisor roles), a COMPLETED Work Order, an existing not-yet-reviewed Cost
/// Summary, and a fresh Cost Summary RowVersion. This file covers Read/Review/the pending-review queue only, per
/// `docs/13` §4.19's own scope boundary: no Reject/Return/Reopen, no Work Order Close, no Corrective Action
/// lifecycle, no Cancel Work Order, no invitation/activation, no Time Correction.
/// </summary>
public sealed class CostSummaryReviewEndpointsTests : IClassFixture<CostSummaryReviewApiFactory>
{
    private const string Requests = "/api/v1/repair-requests";
    private const string Routes = "/api/v1/approval-routes";
    private const string WorkOrders = "/api/v1/work-orders";
    private const string Visits = "/api/v1/service-visits";
    private const string Sessions = "/api/v1/work-sessions";
    private const string PendingReview = "/api/v1/work-orders/pending-cost-summary-review";
    private const string ValidHash = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";

    private readonly CostSummaryReviewApiFactory _factory;

    public CostSummaryReviewEndpointsTests(CostSummaryReviewApiFactory factory)
    {
        _factory = factory;
    }

    private sealed record Caller(Guid UserId, Guid TenantId, string Token);

    private sealed record Prepared(Guid TenantId, Site Site, Caller TeamLead, Caller Supervisor, Caller Approver, Guid WorkOrderId, string CostSummaryETag, decimal TotalAmount, string CurrencyCode);

    // ---------------- Read authorization ----------------

    [Fact]
    public async Task Read_ByThePreparingTeamLead_Succeeds()
    {
        var p = await PreparedAsync();

        var response = await SendAsync(HttpMethod.Get, $"{WorkOrders}/{p.WorkOrderId}/cost-summary", p.TeamLead, null, null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal(p.TotalAmount, body.GetProperty("totalAmount").GetDecimal());
        Assert.Equal(p.CurrencyCode, body.GetProperty("currencyCode").GetString());
    }

    [Fact]
    public async Task Read_ByASiteScopedSupervisor_Succeeds()
    {
        var p = await PreparedAsync();

        var response = await SendAsync(HttpMethod.Get, $"{WorkOrders}/{p.WorkOrderId}/cost-summary", p.Supervisor, null, null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Read_ByARequesterOrTechnician_Get403()
    {
        var p = await PreparedAsync();
        var requester = await CallerAsync(p.TenantId, [p.Site], RoleCodes.Requester);
        var technician = await CallerAsync(p.TenantId, [p.Site], RoleCodes.Technician);

        await AssertProblemAsync(await SendAsync(HttpMethod.Get, $"{WorkOrders}/{p.WorkOrderId}/cost-summary", requester, null, null), HttpStatusCode.Forbidden, "ACCESS_DENIED");
        await AssertProblemAsync(await SendAsync(HttpMethod.Get, $"{WorkOrders}/{p.WorkOrderId}/cost-summary", technician, null, null), HttpStatusCode.Forbidden, "ACCESS_DENIED");
    }

    [Fact]
    public async Task Read_ByATeamLeadOfAnotherTenant_Get404()
    {
        var p = await PreparedAsync();
        var foreign = await CallerAsync(Guid.NewGuid(), [], RoleCodes.TeamLead);

        await AssertProblemAsync(await SendAsync(HttpMethod.Get, $"{WorkOrders}/{p.WorkOrderId}/cost-summary", foreign, null, null), HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task Read_BySupervisorWithNoSiteScopeOnThisWorkOrder_Get404()
    {
        var p = await PreparedAsync();
        var outOfScope = await CallerAsync(p.TenantId, [], RoleCodes.Supervisor);

        await AssertProblemAsync(await SendAsync(HttpMethod.Get, $"{WorkOrders}/{p.WorkOrderId}/cost-summary", outOfScope, null, null), HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task Read_BeforeAnyCostSummaryIsPrepared_Get404()
    {
        var w = await CompletedAsync();

        await AssertProblemAsync(await SendAsync(HttpMethod.Get, $"{WorkOrders}/{w.WorkOrderId}/cost-summary", w.TeamLead, null, null), HttpStatusCode.NotFound, "NOT_FOUND");
    }

    // ---------------- Pending-review queue ----------------

    [Fact]
    public async Task PendingReview_ListsOnlyCompletedUnreviewedCostSummaries_SiteScoped_NewestPreparedFirst()
    {
        var p1 = await PreparedAsync();
        // A second Work Order in the SAME tenant/site, prepared later, must sort first.
        var w2 = await CompletedInSameTenantAndSiteAsync(p1.TenantId, p1.Site, p1.Approver);
        var prepared2 = await SendAsync(HttpMethod.Put, $"{WorkOrders}/{w2.WorkOrderId}/cost-summary", w2.TeamLead,
            new { totalAmount = 500m, currencyCode = "EUR", note = (string?)null }, w2.WorkOrderETag);
        Assert.Equal(HttpStatusCode.OK, prepared2.StatusCode);

        // A third Work Order, same tenant, but the Supervisor has no Site scope on it — must not appear.
        var otherSite = await SiteAsync(p1.TenantId);
        var w3TeamLead = await CallerAsync(p1.TenantId, [otherSite], RoleCodes.TeamLead);
        var w3 = await CompletedForCallersAsync(p1.TenantId, otherSite, w3TeamLead, needsApprovalRoute: true);
        var prepared3 = await SendAsync(HttpMethod.Put, $"{WorkOrders}/{w3.WorkOrderId}/cost-summary", w3TeamLead,
            new { totalAmount = 999m, currencyCode = "GBP", note = (string?)null }, w3.WorkOrderETag);
        Assert.Equal(HttpStatusCode.OK, prepared3.StatusCode);

        var response = await SendAsync(HttpMethod.Get, PendingReview, p1.Supervisor, null, null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        var items = body.GetProperty("items").EnumerateArray().ToList();

        Assert.Equal(2, items.Count);
        Assert.Equal(w2.WorkOrderId, items[0].GetProperty("workOrderId").GetGuid());
        Assert.Equal(p1.WorkOrderId, items[1].GetProperty("workOrderId").GetGuid());
        Assert.DoesNotContain(items, item => item.GetProperty("workOrderId").GetGuid() == w3.WorkOrderId);
    }

    [Fact]
    public async Task PendingReview_ExcludesAnAlreadyReviewedCostSummary()
    {
        var p = await PreparedAsync();
        var reviewed = await SendAsync(HttpMethod.Post, $"{WorkOrders}/{p.WorkOrderId}/review-cost-summary", p.Supervisor, null, p.CostSummaryETag);
        Assert.Equal(HttpStatusCode.OK, reviewed.StatusCode);

        var response = await SendAsync(HttpMethod.Get, PendingReview, p.Supervisor, null, null);
        var body = await JsonAsync(response);
        Assert.Empty(body.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task PendingReview_Paginates()
    {
        var p1 = await PreparedAsync();
        var w2 = await CompletedInSameTenantAndSiteAsync(p1.TenantId, p1.Site, p1.Approver);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Put, $"{WorkOrders}/{w2.WorkOrderId}/cost-summary", w2.TeamLead,
            new { totalAmount = 500m, currencyCode = "EUR", note = (string?)null }, w2.WorkOrderETag)).StatusCode);

        var firstPage = await JsonAsync(await SendAsync(HttpMethod.Get, $"{PendingReview}?page=1&pageSize=1", p1.Supervisor, null, null));
        Assert.Equal(1, firstPage.GetProperty("items").GetArrayLength());
        Assert.Equal(2, firstPage.GetProperty("totalCount").GetInt32());

        var secondPage = await JsonAsync(await SendAsync(HttpMethod.Get, $"{PendingReview}?page=2&pageSize=1", p1.Supervisor, null, null));
        Assert.Equal(1, secondPage.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task PendingReview_ByANonSupervisor_Get403()
    {
        var p = await PreparedAsync();

        await AssertProblemAsync(await SendAsync(HttpMethod.Get, PendingReview, p.TeamLead, null, null), HttpStatusCode.Forbidden, "ACCESS_DENIED");
    }

    // ---------------- Review: success ----------------

    [Fact]
    public async Task Review_Success_SetsReviewedByAndAt_AndAudits_InOneTransaction_WithoutChangingWorkOrderStatus()
    {
        var p = await PreparedAsync();
        var before = DateTime.UtcNow.AddSeconds(-2);

        var response = await SendAsync(HttpMethod.Post, $"{WorkOrders}/{p.WorkOrderId}/review-cost-summary", p.Supervisor, null, p.CostSummaryETag);
        var after = DateTime.UtcNow.AddSeconds(2);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal(p.Supervisor.UserId, body.GetProperty("reviewedBy").GetGuid());
        Assert.False(body.GetProperty("reviewedAt").ValueKind == JsonValueKind.Null);

        var costSummary = await WithDbAsync(db => db.CostSummaries.AsNoTracking().SingleAsync(item => item.WorkOrderId == p.WorkOrderId));
        Assert.Equal(p.Supervisor.UserId, costSummary.ReviewedBy);
        Assert.NotNull(costSummary.ReviewedAt);
        Assert.InRange(costSummary.ReviewedAt!.Value, before, after);

        var workOrder = await WithDbAsync(db => db.WorkOrders.AsNoTracking().SingleAsync(item => item.Id == p.WorkOrderId));
        Assert.Equal(WorkOrderStatus.Completed, workOrder.Status);

        var audit = await WithDbAsync(db => db.AuditHistory.AsNoTracking().SingleAsync(a => a.EntityId == p.WorkOrderId && a.ActionCode == "COST_SUMMARY_REVIEWED"));
        Assert.Equal("WORK_ORDER", audit.EntityType);
        Assert.Equal("COMPLETED", audit.FromState);
        Assert.Equal("COMPLETED", audit.ToState);
        Assert.Equal(p.Supervisor.UserId, audit.ActorId);
        Assert.InRange(audit.OccurredAt, before, after);
    }

    // ---------------- Review: authorization / Separation of Duties ----------------

    [Fact]
    public async Task Review_ByANonSupervisor_Get403()
    {
        var p = await PreparedAsync();
        var requester = await CallerAsync(p.TenantId, [p.Site], RoleCodes.Requester);

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{WorkOrders}/{p.WorkOrderId}/review-cost-summary", requester, null, p.CostSummaryETag),
            HttpStatusCode.Forbidden, "ACCESS_DENIED");
        await AssertNotReviewedAsync(p.WorkOrderId);
    }

    [Fact]
    public async Task Review_BySameUserWhoPrepared_HoldingBothRoles_Get403()
    {
        var tenantId = Guid.NewGuid();
        var site = await SiteAsync(tenantId);
        // A single user holding BOTH Team Lead and Supervisor roles simultaneously.
        var multiRole = await CallerAsync(tenantId, [site], RoleCodes.TeamLead, RoleCodes.Supervisor);
        var w = await CompletedForCallersAsync(tenantId, site, multiRole, needsApprovalRoute: true);

        var prepared = await SendAsync(HttpMethod.Put, $"{WorkOrders}/{w.WorkOrderId}/cost-summary", multiRole,
            new { totalAmount = 1m, currencyCode = "USD", note = (string?)null }, w.WorkOrderETag);
        Assert.Equal(HttpStatusCode.OK, prepared.StatusCode);
        var costSummaryETag = prepared.Headers.ETag!.Tag;

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{WorkOrders}/{w.WorkOrderId}/review-cost-summary", multiRole, null, costSummaryETag),
            HttpStatusCode.Forbidden, "ACCESS_DENIED");
        await AssertNotReviewedAsync(w.WorkOrderId);
    }

    [Fact]
    public async Task Review_ByASupervisorOfAnotherTenant_Get404()
    {
        var p = await PreparedAsync();
        var foreign = await CallerAsync(Guid.NewGuid(), [], RoleCodes.Supervisor);

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{WorkOrders}/{p.WorkOrderId}/review-cost-summary", foreign, null, p.CostSummaryETag),
            HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task Review_BySupervisorWithNoSiteScopeOnThisWorkOrder_Get404()
    {
        var p = await PreparedAsync();
        var outOfScope = await CallerAsync(p.TenantId, [], RoleCodes.Supervisor);

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{WorkOrders}/{p.WorkOrderId}/review-cost-summary", outOfScope, null, p.CostSummaryETag),
            HttpStatusCode.NotFound, "NOT_FOUND");
        await AssertNotReviewedAsync(p.WorkOrderId);
    }

    [Fact]
    public async Task Review_WithoutIfMatch_Returns400_AndWithoutAToken_Returns401()
    {
        var p = await PreparedAsync();

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{WorkOrders}/{p.WorkOrderId}/review-cost-summary", p.Supervisor, null, null),
            HttpStatusCode.BadRequest, "BAD_REQUEST");
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await SendAsync(HttpMethod.Post, $"{WorkOrders}/{p.WorkOrderId}/review-cost-summary", null, null, p.CostSummaryETag)).StatusCode);
    }

    // ---------------- Review: state guards ----------------

    [Fact]
    public async Task Review_WhenNoCostSummaryHasBeenPreparedYet_Get404()
    {
        var w = await CompletedAsync();
        var supervisor = await CallerAsync(w.TenantId, [w.Site], RoleCodes.Supervisor);

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{WorkOrders}/{w.WorkOrderId}/review-cost-summary", supervisor, null, "\"AAAAAAAAAAA=\""),
            HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task Review_WhenTheWorkOrderIsNotCompleted_Get409StateConflict()
    {
        // Not reachable via any real API sequence (Prepare itself requires COMPLETED, and nothing currently
        // transitions a Work Order away from COMPLETED while a Cost Summary exists) — seeded directly to prove
        // this defense-in-depth guard, required by the approved decision, fires correctly if it is ever reached.
        var p = await PreparedAsync();
        await WithDbAsync(db => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE work_order SET status = 'AWAITING_CUSTOMER_ACCEPTANCE' WHERE work_order_id = {p.WorkOrderId}"));

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{WorkOrders}/{p.WorkOrderId}/review-cost-summary", p.Supervisor, null, p.CostSummaryETag),
            HttpStatusCode.Conflict, "STATE_CONFLICT");
        await AssertNotReviewedAsync(p.WorkOrderId);
    }

    [Fact]
    public async Task Review_AfterAlreadyReviewed_Get409StateConflict_AndWritesNothingMore()
    {
        var p = await PreparedAsync();
        var first = await SendAsync(HttpMethod.Post, $"{WorkOrders}/{p.WorkOrderId}/review-cost-summary", p.Supervisor, null, p.CostSummaryETag);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var freshETag = first.Headers.ETag!.Tag;

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{WorkOrders}/{p.WorkOrderId}/review-cost-summary", p.Supervisor, null, freshETag),
            HttpStatusCode.Conflict, "STATE_CONFLICT");

        Assert.Equal(1, await WithDbAsync(db => db.AuditHistory.AsNoTracking().CountAsync(a => a.EntityId == p.WorkOrderId && a.ActionCode == "COST_SUMMARY_REVIEWED")));
    }

    // ---------------- Review: concurrency ----------------

    [Fact]
    public async Task Review_WithAStaleRowVersion_Returns409ConcurrencyConflict_AndWritesNothing()
    {
        var p = await PreparedAsync();

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{WorkOrders}/{p.WorkOrderId}/review-cost-summary", p.Supervisor, null, "\"AAAAAAAAAAA=\""),
            HttpStatusCode.Conflict, "CONCURRENCY_CONFLICT");
        await AssertNotReviewedAsync(p.WorkOrderId);
    }

    [Fact]
    public async Task TwoConcurrentReviews_OfTheSameWorkOrder_ExactlyOneSucceeds_NoPartialUpdate()
    {
        var p = await PreparedAsync();

        var responses = await Task.WhenAll(
            Task.Run(() => SendAsync(HttpMethod.Post, $"{WorkOrders}/{p.WorkOrderId}/review-cost-summary", p.Supervisor, null, p.CostSummaryETag)),
            Task.Run(() => SendAsync(HttpMethod.Post, $"{WorkOrders}/{p.WorkOrderId}/review-cost-summary", p.Supervisor, null, p.CostSummaryETag)));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal(1, await WithDbAsync(db => db.AuditHistory.AsNoTracking().CountAsync(a => a.EntityId == p.WorkOrderId && a.ActionCode == "COST_SUMMARY_REVIEWED")));

        var costSummary = await WithDbAsync(db => db.CostSummaries.AsNoTracking().SingleAsync(item => item.WorkOrderId == p.WorkOrderId));
        Assert.NotNull(costSummary.ReviewedAt);
    }

    // ---------------- Team Lead locked out after Review ----------------

    [Fact]
    public async Task Prepare_AfterReview_Get409StateConflict()
    {
        var p = await PreparedAsync();
        var reviewed = await SendAsync(HttpMethod.Post, $"{WorkOrders}/{p.WorkOrderId}/review-cost-summary", p.Supervisor, null, p.CostSummaryETag);
        Assert.Equal(HttpStatusCode.OK, reviewed.StatusCode);
        var reviewedETag = reviewed.Headers.ETag!.Tag;

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Put, $"{WorkOrders}/{p.WorkOrderId}/cost-summary", p.TeamLead,
                new { totalAmount = 2m, currencyCode = "USD", note = (string?)null }, reviewedETag),
            HttpStatusCode.Conflict, "STATE_CONFLICT");
    }

    // ---------------- Helpers ----------------

    private async Task AssertNotReviewedAsync(Guid workOrderId)
    {
        var costSummary = await WithDbAsync(db => db.CostSummaries.AsNoTracking().SingleAsync(item => item.WorkOrderId == workOrderId));
        Assert.Null(costSummary.ReviewedBy);
        Assert.Null(costSummary.ReviewedAt);
        Assert.Equal(0, await WithDbAsync(db => db.AuditHistory.AsNoTracking().CountAsync(a => a.EntityId == workOrderId && a.ActionCode == "COST_SUMMARY_REVIEWED")));
    }

    /// <summary>A Work Order COMPLETED with a Cost Summary already prepared, plus a site-scoped Supervisor distinct from the preparing Team Lead.</summary>
    private async Task<Prepared> PreparedAsync()
    {
        var w = await CompletedAsync();
        var supervisor = await CallerAsync(w.TenantId, [w.Site], RoleCodes.Supervisor);

        const decimal totalAmount = 1234.56m;
        const string currencyCode = "USD";
        var prepared = await SendAsync(HttpMethod.Put, $"{WorkOrders}/{w.WorkOrderId}/cost-summary", w.TeamLead,
            new { totalAmount, currencyCode, note = "Parts and labor." }, w.WorkOrderETag);
        Assert.Equal(HttpStatusCode.OK, prepared.StatusCode);

        return new Prepared(w.TenantId, w.Site, w.TeamLead, supervisor, w.Approver, w.WorkOrderId, prepared.Headers.ETag!.Tag, totalAmount, currencyCode);
    }

    private sealed record CompletedWorkOrder(Guid TenantId, Site Site, Caller TeamLead, Caller Approver, Guid WorkOrderId, string WorkOrderETag);

    /// <summary>A Work Order COMPLETED (Accept already happened), with a fresh tenant/site/Team Lead.</summary>
    private async Task<CompletedWorkOrder> CompletedAsync()
    {
        var c = await CheckedOutAndSummarySubmittedAsync(needsApprovalRoute: true);
        return await AcceptedAsync(c);
    }

    /// <summary>
    /// A second COMPLETED Work Order in an already-existing tenant/site (a route already exists for that site),
    /// with its own fresh Team Lead. Reuses <paramref name="approver"/> from the first Work Order at this Site —
    /// a second Approver-role user at the same Site makes routing ambiguous (ROUTE-API's own auto-routing
    /// expects exactly one match), which is a routing-engine concern, not anything specific to Cost Summary.
    /// </summary>
    private async Task<CompletedWorkOrder> CompletedInSameTenantAndSiteAsync(Guid tenantId, Site site, Caller approver)
    {
        var teamLead = await CallerAsync(tenantId, [site], RoleCodes.TeamLead);
        return await CompletedForCallersAsync(tenantId, site, teamLead, needsApprovalRoute: false, existingApprover: approver);
    }

    /// <summary>
    /// A COMPLETED Work Order for an already-created tenant/site/Team Lead (used to control exactly who is
    /// site-scoped where). <paramref name="needsApprovalRoute"/> must be true whenever <paramref name="site"/>
    /// has never had a route created for it before (routes are scoped per Site, not per tenant) — the caller
    /// decides this explicitly rather than it being inferred from whether the tenant id is "new". <paramref
    /// name="existingApprover"/>, when supplied, avoids a second Approver-role user at the same Site (see
    /// <see cref="CompletedInSameTenantAndSiteAsync"/>'s own remarks).
    /// </summary>
    private async Task<CompletedWorkOrder> CompletedForCallersAsync(Guid tenantId, Site site, Caller teamLead, bool needsApprovalRoute, Caller? existingApprover = null)
    {
        var c = await CheckedOutAndSummarySubmittedAsync(tenantId, site, teamLead, needsApprovalRoute, existingApprover);
        return await AcceptedAsync(c);
    }

    private async Task<CompletedWorkOrder> AcceptedAsync(ReviewReady c)
    {
        var submitted = await SendAsync(HttpMethod.Post, $"{WorkOrders}/{c.WorkOrderId}/submit-for-acceptance", c.TeamLead,
            new { acceptanceContactId = c.Owner.UserId }, c.WorkOrderETag);
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
        var awaitingETag = $"\"{(await JsonAsync(submitted)).GetProperty("rowVersion").GetString()}\"";

        var accepted = await SendAsync(HttpMethod.Post, $"{WorkOrders}/{c.WorkOrderId}/accept", c.Owner, null, awaitingETag);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var completedETag = $"\"{(await JsonAsync(accepted)).GetProperty("rowVersion").GetString()}\"";

        return new CompletedWorkOrder(c.TenantId, c.Site, c.TeamLead, c.Approver, c.WorkOrderId, completedETag);
    }

    private sealed record ReviewReady(Guid TenantId, Site Site, Caller Owner, Caller TeamLead, Caller Approver, Guid WorkOrderId, string WorkOrderETag);

    /// <summary>The full flow up to a submitted Work Summary (AWAITING_SUPERVISOR_REVIEW): Schedule, Check-in, Check-out, Submit Work Summary. Uses a fresh tenant/site/Team Lead/Approver unless one is supplied.</summary>
    private async Task<ReviewReady> CheckedOutAndSummarySubmittedAsync(Guid? existingTenantId = null, Site? existingSite = null, Caller? existingTeamLead = null, bool needsApprovalRoute = true, Caller? existingApprover = null)
    {
        var tenantId = existingTenantId ?? Guid.NewGuid();
        var site = existingSite ?? await SiteAsync(tenantId);
        var admin = await CallerAsync(tenantId, [], RoleCodes.Administrator);
        var owner = await CallerAsync(tenantId, [site], RoleCodes.Requester);
        var approver = existingApprover ?? await CallerAsync(tenantId, [site], RoleCodes.Approver);
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

        // A fixed reason is always sent, whether or not BR-14 actually finds a duplicate in this tenant/site
        // (harmless when there is none) — several tests deliberately reuse the same tenant/site/description
        // shape for a second Work Order, which would otherwise trip the duplicate-continuation-reason guard.
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
        var workOrderETag = $"\"{(await JsonAsync(checkedOut)).GetProperty("workOrderRowVersion").GetString()}\"";

        var summarySubmitted = await SendAsync(HttpMethod.Post, $"{WorkOrders}/{workOrderId}/submit-work-summary", technician,
            new { summaryText = "Replaced the pump seal.", repairOutcomeCode = "REPAIRED" }, workOrderETag);
        Assert.Equal(HttpStatusCode.OK, summarySubmitted.StatusCode);
        var reviewEtag = $"\"{(await JsonAsync(summarySubmitted)).GetProperty("rowVersion").GetString()}\"";

        return new ReviewReady(tenantId, site, owner, teamLead, approver, workOrderId, reviewEtag);
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
