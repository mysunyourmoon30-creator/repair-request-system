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

/// <summary>API host with its own disposable LocalDB database for the ST-WO-010 corrective-Visit Check-in tests.</summary>
public sealed class CorrectiveReworkCheckInApiFactory : AuthApiFactory
{
    public override string ConnectionString =>
        "Server=(localdb)\\MSSQLLocalDB;Database=RepairRequestDb_CorrectiveReworkCheckInApiTest;Trusted_Connection=True;TrustServerCertificate=True";
}

/// <summary>
/// ST-WO-010 "Start Rework" (`docs/13` §4.23): the existing WS-API-001 Check-in endpoint (unchanged route,
/// unchanged controller, unchanged `TechnicianCheckInService.CheckInAsync`) succeeding on a corrective Service
/// Visit created by `CA-API-003` (Schedule Rework). No new API, no new UI. This file covers only what is new or
/// at risk from that change — the correct audit dispatch (`WORK_ORDER_REWORK_STARTED`, not `WORK_ORDER_STARTED`)
/// and that every guard already proven generically for the initial-Visit case (assigned-Technician scope,
/// concurrency, BR-05 active-session overlap, duplicate-request protection) still holds for this new source
/// state. The exhaustive generic mechanics are already proven VisitType-agnostically in
/// <see cref="WorkSessionEndpointsTests"/>; this file does not re-prove them from scratch.
/// </summary>
public sealed class CorrectiveReworkCheckInEndpointsTests : IClassFixture<CorrectiveReworkCheckInApiFactory>
{
    private const string Requests = "/api/v1/repair-requests";
    private const string Routes = "/api/v1/approval-routes";
    private const string WorkOrders = "/api/v1/work-orders";
    private const string Visits = "/api/v1/service-visits";
    private const string Sessions = "/api/v1/work-sessions";
    private const string CorrectiveActions = "/api/v1/corrective-actions";
    private const string ValidHash = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";

    private readonly CorrectiveReworkCheckInApiFactory _factory;

    public CorrectiveReworkCheckInEndpointsTests(CorrectiveReworkCheckInApiFactory factory)
    {
        _factory = factory;
    }

    private sealed record Caller(Guid UserId, Guid TenantId, string Token);

    /// <summary>A Work Order CORRECTIVE_PLAN_APPROVED with its corrective Service Visit SCHEDULED and assigned to Technician.</summary>
    private sealed record ReworkScheduled(
        Guid TenantId, Site Site, Caller Owner, Caller Approver, Caller Coordinator, Caller Technician,
        Guid WorkOrderId, Guid CorrectiveServiceVisitId, string VisitETag);

    // ---------------- Success ----------------

    [Fact]
    public async Task CheckIn_OnACorrectiveVisit_Success_MovesWorkOrderToInProgress_AndWritesTheReworkStartedAudit()
    {
        var r = await ReworkScheduledAsync();
        var before = DateTime.UtcNow.AddSeconds(-2);

        var response = await SendAsync(HttpMethod.Post, $"{Visits}/{r.CorrectiveServiceVisitId}/check-in", r.Technician, null, r.VisitETag);
        var after = DateTime.UtcNow.AddSeconds(2);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal("IN_PROGRESS", body.GetProperty("status").GetString());
        // Two visits exist by now (the completed initial one plus this corrective one) — select by id, not SingleVisit.
        Assert.Equal("IN_PROGRESS", VisitById(body, r.CorrectiveServiceVisitId).GetProperty("status").GetString());

        var workOrder = await WithDbAsync(db => db.WorkOrders.AsNoTracking().SingleAsync(w => w.Id == r.WorkOrderId));
        Assert.Equal(WorkOrderStatus.InProgress, workOrder.Status);

        var session = await WithDbAsync(db => db.WorkSessions.AsNoTracking().SingleAsync(s => s.ServiceVisitId == r.CorrectiveServiceVisitId));
        Assert.Equal(WorkSessionStatus.CheckedIn, session.Status);
        Assert.Equal(r.Technician.UserId, session.TechnicianId);

        // The new, distinct audit action — never a second WORK_ORDER_STARTED, which would carry the wrong fromState.
        // Exactly one WORK_ORDER_STARTED does already exist by this point (ST-WO-002, from the Technician's
        // earlier Check-in on the *initial* Visit inside ReworkScheduledAsync's own setup) — this proves the two
        // action codes coexist correctly on the same Work Order rather than one clobbering the other.
        var reworkAudit = await WithDbAsync(db => db.AuditHistory.AsNoTracking()
            .SingleAsync(a => a.EntityId == r.WorkOrderId && a.ActionCode == "WORK_ORDER_REWORK_STARTED"));
        Assert.Equal("CORRECTIVE_PLAN_APPROVED", reworkAudit.FromState);
        Assert.Equal("IN_PROGRESS", reworkAudit.ToState);
        Assert.Equal(r.Technician.UserId, reworkAudit.ActorId);
        Assert.InRange(reworkAudit.OccurredAt, before, after);
        Assert.Equal(1, await WithDbAsync(db => db.AuditHistory.AsNoTracking().CountAsync(a => a.EntityId == r.WorkOrderId && a.ActionCode == "WORK_ORDER_STARTED")));

        var checkInAudit = await WithDbAsync(db => db.AuditHistory.AsNoTracking()
            .SingleAsync(a => a.EntityId == r.CorrectiveServiceVisitId && a.ActionCode == "SERVICE_VISIT_CHECKED_IN"));
        Assert.Equal("SCHEDULED", checkInAudit.FromState);
        Assert.Equal("IN_PROGRESS", checkInAudit.ToState);
    }

    // ---------------- Authorization / scope ----------------

    [Fact]
    public async Task CheckIn_NonTechnician_Get403()
    {
        var r = await ReworkScheduledAsync();

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{Visits}/{r.CorrectiveServiceVisitId}/check-in", r.Coordinator, null, r.VisitETag),
            HttpStatusCode.Forbidden, "ACCESS_DENIED");
    }

    [Fact]
    public async Task CheckIn_ByADifferentTechnician_Get404_AndWritesNothing()
    {
        var r = await ReworkScheduledAsync();
        var otherTechnician = await CallerAsync(r.TenantId, [r.Site], RoleCodes.Technician);

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{Visits}/{r.CorrectiveServiceVisitId}/check-in", otherTechnician, null, r.VisitETag),
            HttpStatusCode.NotFound, "NOT_FOUND");

        Assert.Equal(0, await WithDbAsync(db => db.WorkSessions.AsNoTracking().CountAsync(s => s.ServiceVisitId == r.CorrectiveServiceVisitId)));
    }

    // ---------------- State / concurrency ----------------

    [Fact]
    public async Task CheckIn_StaleRowVersion_Returns409ConcurrencyConflict_AndWritesNothing()
    {
        var r = await ReworkScheduledAsync();

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{Visits}/{r.CorrectiveServiceVisitId}/check-in", r.Technician, null, "\"AAAAAAAAAAA=\""),
            HttpStatusCode.Conflict, "CONCURRENCY_CONFLICT");

        Assert.Equal(0, await WithDbAsync(db => db.WorkSessions.AsNoTracking().CountAsync(s => s.ServiceVisitId == r.CorrectiveServiceVisitId)));
    }

    [Fact]
    public async Task ASecondCheckIn_OnTheSameCorrectiveVisit_Returns409StateConflict_AndWritesNoSecondSession()
    {
        var r = await ReworkScheduledAsync();
        var first = await SendAsync(HttpMethod.Post, $"{Visits}/{r.CorrectiveServiceVisitId}/check-in", r.Technician, null, r.VisitETag);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var freshVisitETag = $"\"{VisitById(await JsonAsync(first), r.CorrectiveServiceVisitId).GetProperty("rowVersion").GetString()}\"";

        // The technician must check out before a second Check-in could ever be attempted on a real client, but
        // the server itself must still refuse a raw retry against the now-IN_PROGRESS Visit — the same
        // duplicate-request protection already proven generically for the initial-Visit case.
        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{Visits}/{r.CorrectiveServiceVisitId}/check-in", r.Technician, null, freshVisitETag),
            HttpStatusCode.Conflict, "STATE_CONFLICT");

        Assert.Equal(1, await WithDbAsync(db => db.WorkSessions.AsNoTracking().CountAsync(s => s.ServiceVisitId == r.CorrectiveServiceVisitId)));
        Assert.Equal(1, await WithDbAsync(db => db.AuditHistory.AsNoTracking().CountAsync(a => a.EntityId == r.WorkOrderId && a.ActionCode == "WORK_ORDER_REWORK_STARTED")));
    }

    [Fact]
    public async Task TwoConcurrentCheckIns_OnTheSameCorrectiveVisit_ExactlyOneSucceeds_WithExactlyOneSessionAndOneReworkStartedAudit()
    {
        var r = await ReworkScheduledAsync();

        var responses = await Task.WhenAll(
            Task.Run(() => SendAsync(HttpMethod.Post, $"{Visits}/{r.CorrectiveServiceVisitId}/check-in", r.Technician, null, r.VisitETag)),
            Task.Run(() => SendAsync(HttpMethod.Post, $"{Visits}/{r.CorrectiveServiceVisitId}/check-in", r.Technician, null, r.VisitETag)));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal(1, await WithDbAsync(db => db.WorkSessions.AsNoTracking().CountAsync(s => s.ServiceVisitId == r.CorrectiveServiceVisitId)));
        Assert.Equal(1, await WithDbAsync(db => db.AuditHistory.AsNoTracking().CountAsync(a => a.EntityId == r.WorkOrderId && a.ActionCode == "WORK_ORDER_REWORK_STARTED")));
    }

    // ---------------- BR-05: active Work Session overlap, unmodified guard ----------------

    [Fact]
    public async Task CheckIn_WhenTheTechnicianAlreadyHasAnActiveSessionElsewhere_Returns409StateConflict_AndWritesNothing()
    {
        var r = await ReworkScheduledAsync();

        // The same technician holds an unrelated active session on a plain initial Visit of a different Work Order.
        var elsewhere = await InitialScheduledVisitForAsync(r);
        var elsewhereCheckIn = await SendAsync(HttpMethod.Post, $"{Visits}/{elsewhere.ServiceVisitId}/check-in", r.Technician, null, elsewhere.VisitETag);
        Assert.Equal(HttpStatusCode.OK, elsewhereCheckIn.StatusCode);

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{Visits}/{r.CorrectiveServiceVisitId}/check-in", r.Technician, null, r.VisitETag),
            HttpStatusCode.Conflict, "STATE_CONFLICT");

        Assert.Equal(0, await WithDbAsync(db => db.WorkSessions.AsNoTracking().CountAsync(s => s.ServiceVisitId == r.CorrectiveServiceVisitId)));
        // Not an unqualified count: the technician already has one historical CHECKED_OUT session from the
        // initial Visit inside ReworkScheduledAsync's own setup — BR-05 only cares about *active* sessions.
        Assert.Equal(1, await WithDbAsync(db => db.WorkSessions.AsNoTracking().CountAsync(s => s.TechnicianId == r.Technician.UserId && s.Status != WorkSessionStatus.CheckedOut)));
    }

    // ---------------- Helpers ----------------

    private static JsonElement SingleVisit(JsonElement workOrderResponse) =>
        Assert.Single(workOrderResponse.GetProperty("visits").EnumerateArray());

    /// <summary>Picks one Visit out of a multi-Visit `visits` array by id — the corrective Visit's own Work Order also carries its completed initial Visit's history, so `SingleVisit` does not apply once Schedule Rework has run.</summary>
    private static JsonElement VisitById(JsonElement workOrderResponse, Guid serviceVisitId) =>
        workOrderResponse.GetProperty("visits").EnumerateArray().Single(v => v.GetProperty("serviceVisitId").GetGuid() == serviceVisitId);

    /// <summary>
    /// Builds a Work Order all the way through the real corrective cycle: Draft -&gt; Submit -&gt; Approve -&gt;
    /// Convert -&gt; Schedule -&gt; Check-in -&gt; Check-out -&gt; Submit Work Summary -&gt; Submit for Acceptance -&gt;
    /// Reject -&gt; Submit Corrective Plan -&gt; Approve Corrective Plan -&gt; Schedule Rework, ending with the
    /// corrective Visit SCHEDULED and assigned to the same Technician — ready for this ticket's Check-in.
    /// </summary>
    private async Task<ReworkScheduled> ReworkScheduledAsync()
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
        var reworkBody = await JsonAsync(reworkScheduled);
        var correctiveVisitId = reworkBody.GetProperty("correctiveServiceVisitId").GetGuid();
        var correctiveVisit = await WithDbAsync(db => db.ServiceVisits.AsNoTracking().SingleAsync(v => v.Id == correctiveVisitId));

        return new ReworkScheduled(tenantId, site, owner, approver, coordinator, technician, workOrderId, correctiveVisitId, $"\"{Convert.ToBase64String(correctiveVisit.RowVersion)}\"");
    }

    private sealed record InitialScheduled(Guid ServiceVisitId, string VisitETag);

    /// <summary>
    /// A second, independent SCHEDULED initial Visit for the given technician (distinct Repair Request/Work
    /// Order), for the BR-05 overlap test. Reuses the scenario's own Owner/Approver/Coordinator rather than
    /// minting new ones — the tenant's single approval route (set up once in <see cref="ReworkScheduledAsync"/>)
    /// designates that first Approver specifically, so a freshly-minted second Approver cannot approve here (same
    /// pitfall <see cref="WorkSessionEndpointsTests.AnotherScheduledVisitAsync"/> already documents and avoids).
    /// </summary>
    private async Task<InitialScheduled> InitialScheduledVisitForAsync(ReworkScheduled r)
    {
        var (tenantId, site, owner, approver, coordinator, technician, _, _, _) = r;
        var (id, draftETag) = await DraftAsync(owner, site);
        await WithDbAsync(async db =>
        {
            var evidence = new FileAsset(tenantId, "evidence2.png", "image/png", 1024, ValidHash, $"{tenantId:N}/{Guid.NewGuid():N}", owner.UserId, DateTime.UtcNow);
            evidence.MarkClean();
            db.FileAssets.Add(evidence);
            db.RepairRequestAttachments.Add(new RepairRequestAttachment(id, evidence.Id, AttachmentFileRules.AccessScopeCode));
            await db.SaveChangesAsync();
        });

        var submitted = await SendAsync(HttpMethod.Post, $"{Requests}/{id}/submit", owner,
            new { duplicateContinuationReason = "Second, unrelated fault on the same Site/Category for BR-05 overlap testing" }, draftETag);
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
            scheduledStartAt = "2026-10-02T08:00:00Z",
            scheduledEndAt = "2026-10-02T10:00:00Z"
        }, converted.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.OK, scheduled.StatusCode);
        var visit = SingleVisit(await JsonAsync(scheduled));
        return new InitialScheduled(visit.GetProperty("serviceVisitId").GetGuid(), $"\"{visit.GetProperty("rowVersion").GetString()}\"");
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
