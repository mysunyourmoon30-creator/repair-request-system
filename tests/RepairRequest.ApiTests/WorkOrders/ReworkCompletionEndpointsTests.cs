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

/// <summary>API host with its own disposable LocalDB database for the rework-completion (Submit Work Summary / Submit for Acceptance) tests.</summary>
public sealed class ReworkCompletionApiFactory : AuthApiFactory
{
    public override string ConnectionString =>
        "Server=(localdb)\\MSSQLLocalDB;Database=RepairRequestDb_ReworkCompletionApiTest;Trusted_Connection=True;TrustServerCertificate=True";
}

/// <summary>
/// Rework completion (`docs/13` §4.24; `UC-WO-024` Main Flow item 3 "submit/review summary"): `ST-WO-003`
/// Submit Work Summary and `ST-WO-004` Submit for Acceptance, reused unchanged for the corrective cycle — a
/// Work Order reaching `IN_PROGRESS` via `ST-WO-010` (corrective Check-in, PR #18) rather than `ST-WO-002`. No
/// new endpoint, no new domain method; both actions already gate on Work Order status only, which does not
/// distinguish the two source transitions. This file covers what changes or is newly at risk: a real,
/// reproducible crash in `GET .../work-summary` once a second `work_summary` row exists (fixed by ordering
/// "latest" instead of assuming a single row), and the newly-enforced "same designated contact" rule
/// (`UC-WO-024`'s own literal text). Stops at `AWAITING_CUSTOMER_ACCEPTANCE` ("New acceptance round pending") —
/// the customer's actual round-2 Accept/Reject decision is a separate, later ticket, not covered here.
/// </summary>
public sealed class ReworkCompletionEndpointsTests : IClassFixture<ReworkCompletionApiFactory>
{
    private const string Requests = "/api/v1/repair-requests";
    private const string Routes = "/api/v1/approval-routes";
    private const string WorkOrders = "/api/v1/work-orders";
    private const string Visits = "/api/v1/service-visits";
    private const string Sessions = "/api/v1/work-sessions";
    private const string CorrectiveActions = "/api/v1/corrective-actions";
    private const string ValidHash = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";
    private const string StaleETag = "\"AAAAAAAAAAA=\"";

    private readonly ReworkCompletionApiFactory _factory;

    public ReworkCompletionEndpointsTests(ReworkCompletionApiFactory factory)
    {
        _factory = factory;
    }

    private sealed record Caller(Guid UserId, Guid TenantId, string Token);

    /// <summary>A Work Order IN_PROGRESS (via ST-WO-010) with the corrective Visit checked out, ready for Submit Work Summary round 2.</summary>
    private sealed record ReworkReady(
        Guid TenantId, Site Site, Caller Owner, Caller Approver, Caller Coordinator, Caller Technician, Caller TeamLead, Caller Supervisor,
        Guid WorkOrderId, Guid CorrectiveServiceVisitId, string WorkOrderETag);

    // ---------------- Submit Work Summary (round 2): success ----------------

    [Fact]
    public async Task SubmitWorkSummary_OnTheCorrectiveCycle_Success_MovesWorkOrderToAwaitingSupervisorReview_AndReferencesTheCorrectiveVisit()
    {
        var r = await ReworkReadyAsync();
        var before = DateTime.UtcNow.AddSeconds(-2);

        var response = await SendAsync(HttpMethod.Post, $"{WorkOrders}/{r.WorkOrderId}/submit-work-summary", r.Technician,
            new { summaryText = "Replaced the fitting; retested, no leak.", repairOutcomeCode = "REPAIRED" }, r.WorkOrderETag);
        var after = DateTime.UtcNow.AddSeconds(2);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var workOrder = await WithDbAsync(db => db.WorkOrders.AsNoTracking().SingleAsync(w => w.Id == r.WorkOrderId));
        Assert.Equal(WorkOrderStatus.AwaitingSupervisorReview, workOrder.Status);

        var summaries = await WithDbAsync(db => db.WorkSummaries.AsNoTracking().Where(s => s.WorkOrderId == r.WorkOrderId).ToListAsync());
        Assert.Equal(2, summaries.Count);
        var reworkSummary = Assert.Single(summaries, s => s.ServiceVisitId == r.CorrectiveServiceVisitId);
        Assert.Equal(1, reworkSummary.RevisionNo);
        Assert.Equal("Replaced the fitting; retested, no leak.", reworkSummary.SummaryText);

        var audit = await WithDbAsync(db => db.AuditHistory.AsNoTracking()
            .Where(a => a.EntityId == r.WorkOrderId && a.ActionCode == "WORK_SUMMARY_SUBMITTED")
            .OrderByDescending(a => a.OccurredAt)
            .FirstAsync());
        Assert.InRange(audit.OccurredAt, before, after);
        Assert.Contains(reworkSummary.Id.ToString(), audit.NewValueJson!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetWorkSummary_AfterRoundTwo_ReturnsTheLatestSummary_AndDoesNotThrow()
    {
        // The regression this ticket exists to fix: before the fix, a second work_summary row for the same
        // Work Order made GetWorkSummaryAsync's SingleOrDefaultAsync throw (500), since WorkSummary is keyed
        // per-Visit, not per-Work-Order.
        var r = await ReworkReadyAsync();
        var submitted = await SendAsync(HttpMethod.Post, $"{WorkOrders}/{r.WorkOrderId}/submit-work-summary", r.Technician,
            new { summaryText = "Replaced the fitting; retested, no leak.", repairOutcomeCode = "REPAIRED" }, r.WorkOrderETag);
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);

        var response = await SendAsync(HttpMethod.Get, $"{WorkOrders}/{r.WorkOrderId}/work-summary", r.Technician, null, null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal(r.CorrectiveServiceVisitId, body.GetProperty("serviceVisitId").GetGuid());
        Assert.Equal("Replaced the fitting; retested, no leak.", body.GetProperty("summaryText").GetString());
    }

    // ---------------- Submit Work Summary (round 2): invalid actor / state ----------------

    [Fact]
    public async Task SubmitWorkSummary_ByNonTechnician_Get403()
    {
        var r = await ReworkReadyAsync();

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{WorkOrders}/{r.WorkOrderId}/submit-work-summary", r.Coordinator,
                new { summaryText = "Plan.", repairOutcomeCode = "REPAIRED" }, r.WorkOrderETag),
            HttpStatusCode.Forbidden, "ACCESS_DENIED");
    }

    [Fact]
    public async Task SubmitWorkSummary_BeforeTheCorrectiveVisitIsCheckedOut_Returns409StateConflict_AndWritesNoSecondSummary()
    {
        var r = await ReworkCheckedInOnlyAsync();

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{WorkOrders}/{r.WorkOrderId}/submit-work-summary", r.Technician,
                new { summaryText = "Too early.", repairOutcomeCode = "REPAIRED" }, r.WorkOrderETag),
            HttpStatusCode.Conflict, "STATE_CONFLICT");

        Assert.Equal(1, await WithDbAsync(db => db.WorkSummaries.AsNoTracking().CountAsync(s => s.WorkOrderId == r.WorkOrderId)));
    }

    // ---------------- Submit Work Summary (round 2): duplicate / concurrent ----------------

    [Fact]
    public async Task ASecondSubmitWorkSummary_OnTheCorrectiveCycle_Returns409StateConflict_AndWritesNoThirdSummary()
    {
        var r = await ReworkReadyAsync();
        var first = await SendAsync(HttpMethod.Post, $"{WorkOrders}/{r.WorkOrderId}/submit-work-summary", r.Technician,
            new { summaryText = "First rework summary.", repairOutcomeCode = "REPAIRED" }, r.WorkOrderETag);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var freshEtag = $"\"{(await JsonAsync(first)).GetProperty("rowVersion").GetString()}\"";

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{WorkOrders}/{r.WorkOrderId}/submit-work-summary", r.Technician,
                new { summaryText = "Second attempt.", repairOutcomeCode = "REPAIRED" }, freshEtag),
            HttpStatusCode.Conflict, "STATE_CONFLICT");

        Assert.Equal(2, await WithDbAsync(db => db.WorkSummaries.AsNoTracking().CountAsync(s => s.WorkOrderId == r.WorkOrderId)));
    }

    [Fact]
    public async Task TwoConcurrentSubmitWorkSummary_OnTheCorrectiveCycle_ExactlyOneSucceeds()
    {
        var r = await ReworkReadyAsync();
        var body = new { summaryText = "Replaced the fitting.", repairOutcomeCode = "REPAIRED" };

        var responses = await Task.WhenAll(
            Task.Run(() => SendAsync(HttpMethod.Post, $"{WorkOrders}/{r.WorkOrderId}/submit-work-summary", r.Technician, body, r.WorkOrderETag)),
            Task.Run(() => SendAsync(HttpMethod.Post, $"{WorkOrders}/{r.WorkOrderId}/submit-work-summary", r.Technician, body, r.WorkOrderETag)));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal(2, await WithDbAsync(db => db.WorkSummaries.AsNoTracking().CountAsync(s => s.WorkOrderId == r.WorkOrderId)));
    }

    // ---------------- Submit for Acceptance (round 2): same designated contact ----------------

    [Fact]
    public async Task SubmitForAcceptance_OnTheCorrectiveCycle_WithTheSameContactAsRoundOne_Success()
    {
        var r = await ReworkReadyAsync();
        var summarized = await SendAsync(HttpMethod.Post, $"{WorkOrders}/{r.WorkOrderId}/submit-work-summary", r.Technician,
            new { summaryText = "Replaced the fitting.", repairOutcomeCode = "REPAIRED" }, r.WorkOrderETag);
        Assert.Equal(HttpStatusCode.OK, summarized.StatusCode);
        var reviewEtag = $"\"{(await JsonAsync(summarized)).GetProperty("rowVersion").GetString()}\"";

        // Round 1 designated r.Owner as the Acceptance Contact — round 2 must reuse the same one.
        var response = await SendAsync(HttpMethod.Post, $"{WorkOrders}/{r.WorkOrderId}/submit-for-acceptance", r.TeamLead,
            new { acceptanceContactId = r.Owner.UserId }, reviewEtag);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var workOrder = await WithDbAsync(db => db.WorkOrders.AsNoTracking().SingleAsync(w => w.Id == r.WorkOrderId));
        Assert.Equal(WorkOrderStatus.AwaitingCustomerAcceptance, workOrder.Status);
        Assert.Equal(r.Owner.UserId, workOrder.AcceptanceContactId);
    }

    [Fact]
    public async Task SubmitForAcceptance_OnTheCorrectiveCycle_WithADifferentContactThanRoundOne_Returns422_AndWritesNothing()
    {
        var r = await ReworkReadyAsync();
        var summarized = await SendAsync(HttpMethod.Post, $"{WorkOrders}/{r.WorkOrderId}/submit-work-summary", r.Technician,
            new { summaryText = "Replaced the fitting.", repairOutcomeCode = "REPAIRED" }, r.WorkOrderETag);
        Assert.Equal(HttpStatusCode.OK, summarized.StatusCode);
        var reviewEtag = $"\"{(await JsonAsync(summarized)).GetProperty("rowVersion").GetString()}\"";
        var differentContact = await CallerAsync(r.TenantId, [r.Site], RoleCodes.Requester);

        var body = await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{WorkOrders}/{r.WorkOrderId}/submit-for-acceptance", r.TeamLead,
                new { acceptanceContactId = differentContact.UserId }, reviewEtag),
            HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
        Assert.True(body.GetProperty("errors").TryGetProperty("acceptanceContactId", out _));

        var workOrder = await WithDbAsync(db => db.WorkOrders.AsNoTracking().SingleAsync(w => w.Id == r.WorkOrderId));
        Assert.Equal(WorkOrderStatus.AwaitingSupervisorReview, workOrder.Status);
        Assert.Equal(r.Owner.UserId, workOrder.AcceptanceContactId);
    }

    // ---------------- Helpers ----------------

    private static JsonElement SingleVisit(JsonElement workOrderResponse) =>
        Assert.Single(workOrderResponse.GetProperty("visits").EnumerateArray());

    /// <summary>Builds the full corrective cycle up to Check-in on the corrective Visit, but not yet checked out — for the "too early" state-conflict test.</summary>
    private async Task<ReworkReady> ReworkCheckedInOnlyAsync()
    {
        var (tenantId, site, owner, approver, coordinator, technician, teamLead, supervisor, workOrderId, correctiveVisitId) = await BuildToCorrectiveCheckInAsync();
        var workOrder = await WithDbAsync(db => db.WorkOrders.AsNoTracking().SingleAsync(w => w.Id == workOrderId));
        return new ReworkReady(tenantId, site, owner, approver, coordinator, technician, teamLead, supervisor, workOrderId, correctiveVisitId, $"\"{Convert.ToBase64String(workOrder.RowVersion)}\"");
    }

    /// <summary>Builds the full corrective cycle through Check-out on the corrective Visit — ready for Submit Work Summary round 2.</summary>
    private async Task<ReworkReady> ReworkReadyAsync()
    {
        var (tenantId, site, owner, approver, coordinator, technician, teamLead, supervisor, workOrderId, correctiveVisitId) = await BuildToCorrectiveCheckInAsync();

        var current = await JsonAsync(await SendAsync(HttpMethod.Get, $"{Sessions}/current", technician, null, null));
        var sessionId = current.GetProperty("workSessionId").GetGuid();
        var sessionETag = $"\"{current.GetProperty("rowVersion").GetString()}\"";

        var checkedOut = await SendAsync(HttpMethod.Post, $"{Sessions}/{sessionId}/check-out", technician, null, sessionETag);
        Assert.Equal(HttpStatusCode.OK, checkedOut.StatusCode);
        var workOrderETag = $"\"{(await JsonAsync(checkedOut)).GetProperty("workOrderRowVersion").GetString()}\"";

        return new ReworkReady(tenantId, site, owner, approver, coordinator, technician, teamLead, supervisor, workOrderId, correctiveVisitId, workOrderETag);
    }

    private sealed record BuiltToCheckIn(
        Guid TenantId, Site Site, Caller Owner, Caller Approver, Caller Coordinator, Caller Technician, Caller TeamLead, Caller Supervisor,
        Guid WorkOrderId, Guid CorrectiveServiceVisitId);

    /// <summary>
    /// Builds a Work Order all the way through the real corrective cycle: Draft -&gt; Submit -&gt; Approve -&gt;
    /// Convert -&gt; Schedule -&gt; Check-in -&gt; Check-out -&gt; Submit Work Summary -&gt; Submit for Acceptance -&gt;
    /// Reject -&gt; Submit Corrective Plan -&gt; Approve Corrective Plan -&gt; Schedule Rework -&gt; Check-in on the
    /// corrective Visit — mirrors <c>CorrectiveReworkCheckInEndpointsTests.ReworkScheduledAsync</c> exactly.
    /// </summary>
    private async Task<BuiltToCheckIn> BuildToCorrectiveCheckInAsync()
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

        var scheduled = await SendAsync(HttpMethod.Post, $"{WorkOrders}/{workOrderId}/schedule", coordinator, new
        {
            ownerTeamId = Guid.NewGuid(),
            assignedTechnicianId = technician.UserId,
            scheduledStartAt = "2026-10-01T08:00:00Z",
            scheduledEndAt = "2026-10-01T10:00:00Z"
        }, converted.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.OK, scheduled.StatusCode);
        var initialVisit = SingleVisit(await JsonAsync(scheduled));
        var initialVisitId = initialVisit.GetProperty("serviceVisitId").GetGuid();
        var initialVisitETag = $"\"{initialVisit.GetProperty("rowVersion").GetString()}\"";

        var checkedIn = await SendAsync(HttpMethod.Post, $"{Visits}/{initialVisitId}/check-in", technician, null, initialVisitETag);
        Assert.Equal(HttpStatusCode.OK, checkedIn.StatusCode);
        var current = await JsonAsync(await SendAsync(HttpMethod.Get, $"{Sessions}/current", technician, null, null));
        var sessionId = current.GetProperty("workSessionId").GetGuid();
        var sessionETag = $"\"{current.GetProperty("rowVersion").GetString()}\"";

        var checkedOut = await SendAsync(HttpMethod.Post, $"{Sessions}/{sessionId}/check-out", technician, null, sessionETag);
        Assert.Equal(HttpStatusCode.OK, checkedOut.StatusCode);
        var workOrderETag = $"\"{(await JsonAsync(checkedOut)).GetProperty("workOrderRowVersion").GetString()}\"";

        var summarySubmitted = await SendAsync(HttpMethod.Post, $"{WorkOrders}/{workOrderId}/submit-work-summary", technician,
            new { summaryText = "Replaced the pump seal.", repairOutcomeCode = "REPAIRED" }, workOrderETag);
        Assert.Equal(HttpStatusCode.OK, summarySubmitted.StatusCode);
        var reviewEtag = $"\"{(await JsonAsync(summarySubmitted)).GetProperty("rowVersion").GetString()}\"";

        var submittedForAcceptance = await SendAsync(HttpMethod.Post, $"{WorkOrders}/{workOrderId}/submit-for-acceptance", teamLead,
            new { acceptanceContactId = owner.UserId }, reviewEtag);
        Assert.Equal(HttpStatusCode.OK, submittedForAcceptance.StatusCode);
        var awaitingEtag = $"\"{(await JsonAsync(submittedForAcceptance)).GetProperty("rowVersion").GetString()}\"";

        var rejected = await SendAsync(HttpMethod.Post, $"{WorkOrders}/{workOrderId}/reject", owner, new { decisionReason = "Leak persists." }, awaitingEtag);
        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);

        var correctiveActionId = await WithDbAsync(db => db.CorrectiveActions.AsNoTracking().Where(item => item.WorkOrderId == workOrderId).Select(item => item.Id).SingleAsync());
        var planFileAssetId = await WithDbAsync(async db =>
        {
            var plan = new FileAsset(tenantId, "plan.pdf", "application/pdf", 512, ValidHash, $"{tenantId:N}/{Guid.NewGuid():N}", teamLead.UserId, DateTime.UtcNow);
            plan.MarkClean();
            db.FileAssets.Add(plan);
            await db.SaveChangesAsync();
            return plan.Id;
        });

        var currentWorkOrderEtag = await WorkOrderETagAsync(workOrderId, teamLead);
        var submittedPlan = await SendAsync(HttpMethod.Post, $"{CorrectiveActions}/{correctiveActionId}/submit-plan", teamLead,
            new { planText = "Replace the fitting and retest.", planFileAssetId }, currentWorkOrderEtag);
        Assert.Equal(HttpStatusCode.OK, submittedPlan.StatusCode);

        currentWorkOrderEtag = await WorkOrderETagAsync(workOrderId, supervisor);
        var approvedPlan = await SendAsync(HttpMethod.Post, $"{CorrectiveActions}/{correctiveActionId}/approve-plan", supervisor, null, currentWorkOrderEtag);
        Assert.Equal(HttpStatusCode.OK, approvedPlan.StatusCode);

        var correctiveActionEtag = await CorrectiveActionETagAsync(workOrderId, coordinator);
        var reworkScheduled = await SendAsync(HttpMethod.Post, $"{CorrectiveActions}/{correctiveActionId}/schedule-rework", coordinator, new
        {
            assignedTeamId = Guid.NewGuid(),
            assignedTechnicianId = technician.UserId,
            scheduledStartAt = "2026-10-05T08:00:00Z",
            scheduledEndAt = "2026-10-05T10:00:00Z"
        }, correctiveActionEtag);
        Assert.Equal(HttpStatusCode.OK, reworkScheduled.StatusCode);
        var correctiveVisitId = (await JsonAsync(reworkScheduled)).GetProperty("correctiveServiceVisitId").GetGuid();
        var correctiveVisit = await WithDbAsync(db => db.ServiceVisits.AsNoTracking().SingleAsync(v => v.Id == correctiveVisitId));

        var correctiveCheckIn = await SendAsync(HttpMethod.Post, $"{Visits}/{correctiveVisitId}/check-in", technician, null,
            $"\"{Convert.ToBase64String(correctiveVisit.RowVersion)}\"");
        Assert.Equal(HttpStatusCode.OK, correctiveCheckIn.StatusCode);

        return new BuiltToCheckIn(tenantId, site, owner, approver, coordinator, technician, teamLead, supervisor, workOrderId, correctiveVisitId);
    }

    private async Task<string> WorkOrderETagAsync(Guid workOrderId, Caller caller)
    {
        var response = await SendAsync(HttpMethod.Get, $"{WorkOrders}/{workOrderId}", caller, null, null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return response.Headers.ETag!.Tag;
    }

    private async Task<string> CorrectiveActionETagAsync(Guid workOrderId, Caller caller)
    {
        var body = await JsonAsync(await SendAsync(HttpMethod.Get, $"{WorkOrders}/{workOrderId}", caller, null, null));
        return $"\"{body.GetProperty("correctiveActionRowVersion").GetString()}\"";
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
