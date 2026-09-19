using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RepairRequest.Application.Attachments;
using RepairRequest.Domain.Auditing;
using RepairRequest.Domain.Files;
using RepairRequest.Domain.MasterData;
using RepairRequest.Domain.RepairRequests;
using RepairRequest.Domain.Security;
using RepairRequest.Domain.WorkOrders;
using RepairRequest.Infrastructure.Identity;
using RepairRequest.Infrastructure.Persistence;
using RepairRequest.Infrastructure.RepairRequests;

namespace RepairRequest.ApiTests.WorkOrders;

/// <summary>API host with its own disposable LocalDB database for the S3-001 My Visits / Check-in endpoint tests.</summary>
public sealed class WorkSessionApiFactory : AuthApiFactory
{
    public override string ConnectionString =>
        "Server=(localdb)\\MSSQLLocalDB;Database=RepairRequestDb_WorkSessionApiTest;Trusted_Connection=True;TrustServerCertificate=True";
}

/// <summary>
/// WS-API-001 Check-in and "My Visits" (GET /service-visits/mine) end to end (ST-WS-001; ST-SV-002; ST-WO-002;
/// BR-05; UC-WO-016; S3-001). Technician-only; only the assigned Technician on a SCHEDULED Visit within current
/// tenant/site scope; If-Match required and checked against the Visit's own RowVersion; no active Work Session
/// elsewhere is enforced both at the application level and, for true concurrent requests, by a DB-level unique
/// filtered index. Success moves the Visit and its Work Order to IN_PROGRESS and opens a Work Session — the
/// client supplies neither status nor time.
/// </summary>
public sealed class WorkSessionEndpointsTests : IClassFixture<WorkSessionApiFactory>
{
    private const string Requests = "/api/v1/repair-requests";
    private const string Routes = "/api/v1/approval-routes";
    private const string WorkOrders = "/api/v1/work-orders";
    private const string Visits = "/api/v1/service-visits";
    private const string ValidHash = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";

    private readonly WorkSessionApiFactory _factory;

    public WorkSessionEndpointsTests(WorkSessionApiFactory factory)
    {
        _factory = factory;
    }

    private sealed record Caller(Guid UserId, Guid TenantId, string Token);

    private sealed record Scenario(
        Guid TenantId, Site Site, Caller Owner, Caller Approver, Caller Coordinator, Caller Technician,
        Guid WorkOrderId, Guid ServiceVisitId, string VisitETag);

    // ---------------- Check-in success ----------------

    [Fact]
    public async Task CheckIn_Success_MovesVisitAndWorkOrderToInProgress_AndOpensASession()
    {
        var scenario = await ScheduledVisitAsync();

        var response = await SendAsync(HttpMethod.Post, $"{Visits}/{scenario.ServiceVisitId}/check-in", scenario.Technician, null, scenario.VisitETag);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal("IN_PROGRESS", body.GetProperty("status").GetString());
        Assert.Equal("IN_PROGRESS", SingleVisit(body).GetProperty("status").GetString());

        var session = await WithDbAsync(db => db.WorkSessions.AsNoTracking().SingleAsync(s => s.ServiceVisitId == scenario.ServiceVisitId));
        Assert.Equal(WorkSessionStatus.CheckedIn, session.Status);
        Assert.Equal(scenario.Technician.UserId, session.TechnicianId);
        Assert.NotEqual(default, session.CheckInAt);

        var auditActions = await WithDbAsync(db => db.AuditHistory.AsNoTracking()
            .Where(a => a.EntityId == scenario.WorkOrderId || a.EntityId == scenario.ServiceVisitId)
            .Select(a => a.ActionCode)
            .ToListAsync());
        Assert.Contains("WORK_ORDER_STARTED", auditActions);
        Assert.Contains("SERVICE_VISIT_CHECKED_IN", auditActions);

        // The Check-in audit must reference the real session, the actor must be the technician, and the times
        // are server-derived (occurred_at equals the session's check_in_at, never a client value).
        var checkInAudit = await WithDbAsync(db => db.AuditHistory.AsNoTracking()
            .SingleAsync(a => a.EntityId == scenario.ServiceVisitId && a.ActionCode == "SERVICE_VISIT_CHECKED_IN"));
        Assert.Equal(scenario.Technician.UserId, checkInAudit.ActorId);
        Assert.Equal("SCHEDULED", checkInAudit.FromState);
        Assert.Equal("IN_PROGRESS", checkInAudit.ToState);
        Assert.Equal(session.CheckInAt, checkInAudit.OccurredAt);
        using var auditJson = JsonDocument.Parse(checkInAudit.NewValueJson!);
        Assert.Equal(session.Id, auditJson.RootElement.GetProperty("workSessionId").GetGuid());
        Assert.NotEqual(Guid.Empty, session.Id);
    }

    [Fact]
    public async Task CheckIn_Response_ContainsOnlyTheCallersOwnVisits()
    {
        var scenario = await ScheduledVisitAsync();
        var otherTechnician = await CallerAsync(scenario.TenantId, [scenario.Site], RoleCodes.Technician);

        // Original visit (assigned to scenario.Technician) is marked missed with a Coordinator-entered reason, then a
        // follow-up visit is created for a different technician on the same Work Order.
        const string missedReason = "Coordinator-only missed reason that must not reach another technician";
        var missed = await SendAsync(HttpMethod.Post, $"{Visits}/{scenario.ServiceVisitId}/mark-missed", scenario.Coordinator, new { reason = missedReason }, scenario.VisitETag);
        Assert.Equal(HttpStatusCode.OK, missed.StatusCode);
        var missedEtag = $"\"{SingleVisit(await JsonAsync(missed)).GetProperty("rowVersion").GetString()}\"";

        var decided = await SendAsync(HttpMethod.Post, $"{Visits}/{scenario.ServiceVisitId}/missed-decision", scenario.Coordinator, new
        {
            decision = "FOLLOW_UP",
            reason = "Follow up with a different technician",
            newSchedule = new
            {
                assignedTeamId = Guid.NewGuid(),
                assignedTechnicianId = otherTechnician.UserId,
                scheduledStartAt = "2026-10-05T08:00:00Z",
                scheduledEndAt = "2026-10-05T10:00:00Z"
            }
        }, missedEtag);
        Assert.Equal(HttpStatusCode.OK, decided.StatusCode);
        var followUp = (await JsonAsync(decided)).GetProperty("visits").EnumerateArray()
            .Single(v => v.GetProperty("serviceVisitId").GetGuid() != scenario.ServiceVisitId);

        var response = await SendAsync(
            HttpMethod.Post, $"{Visits}/{followUp.GetProperty("serviceVisitId").GetGuid()}/check-in", otherTechnician, null,
            $"\"{followUp.GetProperty("rowVersion").GetString()}\"");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        using var body = JsonDocument.Parse(raw);
        var visit = Assert.Single(body.RootElement.GetProperty("visits").EnumerateArray());
        Assert.Equal(otherTechnician.UserId, visit.GetProperty("assignedTechnicianId").GetGuid());
        Assert.DoesNotContain(missedReason, raw);
        Assert.DoesNotContain(scenario.Technician.UserId.ToString(), raw);
    }

    [Fact]
    public async Task CheckIn_WhenTheWorkOrderIsNoLongerScheduled_Returns409StateConflict_AndWritesNothing()
    {
        var scenario = await ScheduledVisitAsync();

        // Not reachable through the API today (Work Order cancel is a later ticket), so seed the state directly.
        await WithDbAsync(db => db.WorkOrders
            .Where(w => w.Id == scenario.WorkOrderId)
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.Status, WorkOrderStatus.Cancelled)));

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{Visits}/{scenario.ServiceVisitId}/check-in", scenario.Technician, null, scenario.VisitETag),
            HttpStatusCode.Conflict, "STATE_CONFLICT");

        Assert.Equal(0, await WithDbAsync(db => db.WorkSessions.AsNoTracking().CountAsync(s => s.ServiceVisitId == scenario.ServiceVisitId)));
        Assert.Equal(
            ServiceVisitStatus.Scheduled,
            await WithDbAsync(db => db.ServiceVisits.AsNoTracking().Where(v => v.Id == scenario.ServiceVisitId).Select(v => v.Status).SingleAsync()));
    }

    [Fact]
    public async Task CheckIn_WithoutIfMatch_Returns400_AndWritesNothing()
    {
        var scenario = await ScheduledVisitAsync();

        var response = await SendAsync(HttpMethod.Post, $"{Visits}/{scenario.ServiceVisitId}/check-in", scenario.Technician, null, null);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "BAD_REQUEST");
        Assert.Equal(0, await WithDbAsync(db => db.WorkSessions.AsNoTracking().CountAsync(s => s.ServiceVisitId == scenario.ServiceVisitId)));
    }

    [Fact]
    public async Task CheckIn_AndMine_WithoutAToken_Return401()
    {
        var scenario = await ScheduledVisitAsync();

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await SendAsync(HttpMethod.Post, $"{Visits}/{scenario.ServiceVisitId}/check-in", null, null, scenario.VisitETag)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, $"{Visits}/mine", null, null, null)).StatusCode);
    }

    // ---------------- Permission ----------------

    [Fact]
    public async Task CheckIn_NonTechnician_Get403()
    {
        var scenario = await ScheduledVisitAsync();

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{Visits}/{scenario.ServiceVisitId}/check-in", scenario.Coordinator, null, scenario.VisitETag),
            HttpStatusCode.Forbidden, "ACCESS_DENIED");
    }

    // ---------------- Scope ----------------

    [Fact]
    public async Task CheckIn_ByADifferentTechnician_Get404()
    {
        var scenario = await ScheduledVisitAsync();
        var otherTechnician = await CallerAsync(scenario.TenantId, [scenario.Site], RoleCodes.Technician);

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{Visits}/{scenario.ServiceVisitId}/check-in", otherTechnician, null, scenario.VisitETag),
            HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task CheckIn_ByTheAssignedTechnicianWithRevokedSiteScope_Get404()
    {
        var scenario = await ScheduledVisitAsync();

        // The technician was eligible (and Site-scoped) at Schedule time; scope is re-checked at Check-in time
        // (defense-in-depth, S3-001 plan), so revoking it afterwards must deny even the assigned technician.
        await WithDbAsync(db => db.UserSiteScopes
            .Where(scope => scope.TenantId == scenario.TenantId && scope.UserId == scenario.Technician.UserId && scope.SiteId == scenario.Site.Id)
            .ExecuteDeleteAsync());

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{Visits}/{scenario.ServiceVisitId}/check-in", scenario.Technician, null, scenario.VisitETag),
            HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task CheckIn_TenantMismatch_Get404()
    {
        var scenario = await ScheduledVisitAsync();
        var otherTenantTechnician = await CallerAsync(Guid.NewGuid(), [], RoleCodes.Technician);

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{Visits}/{scenario.ServiceVisitId}/check-in", otherTenantTechnician, null, scenario.VisitETag),
            HttpStatusCode.NotFound, "NOT_FOUND");
    }

    // ---------------- State guard ----------------

    [Fact]
    public async Task CheckIn_VisitNotScheduled_Returns409StateConflict()
    {
        var scenario = await ScheduledVisitAsync();
        var cancelled = await SendAsync(HttpMethod.Post, $"{Visits}/{scenario.ServiceVisitId}/cancel", scenario.Coordinator, new { reason = "No longer needed" }, scenario.VisitETag);
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        var cancelledEtag = SingleVisit(await JsonAsync(cancelled)).GetProperty("rowVersion").GetString();

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{Visits}/{scenario.ServiceVisitId}/check-in", scenario.Technician, null, $"\"{cancelledEtag}\""),
            HttpStatusCode.Conflict, "STATE_CONFLICT");
    }

    // ---------------- Concurrency: stale token ----------------

    [Fact]
    public async Task CheckIn_StaleRowVersion_Returns409ConcurrencyConflict()
    {
        var scenario = await ScheduledVisitAsync();

        // Bump the Visit's rowversion with a real concurrent Reschedule before the stale Check-in arrives.
        var reassigned = await SendAsync(HttpMethod.Post, $"{Visits}/{scenario.ServiceVisitId}/reschedule", scenario.Coordinator, new
        {
            reason = "Concurrent reschedule to bump rowVersion",
            scheduledStartAt = "2026-10-03T08:00:00Z",
            scheduledEndAt = "2026-10-03T10:00:00Z"
        }, scenario.VisitETag);
        Assert.Equal(HttpStatusCode.OK, reassigned.StatusCode);

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{Visits}/{scenario.ServiceVisitId}/check-in", scenario.Technician, null, scenario.VisitETag),
            HttpStatusCode.Conflict, "CONCURRENCY_CONFLICT");
    }

    [Fact]
    public async Task TwoConcurrentCheckIns_OfTheSameVisit_ExactlyOneSucceeds()
    {
        var scenario = await ScheduledVisitAsync();

        var responses = await Task.WhenAll(
            Task.Run(() => SendAsync(HttpMethod.Post, $"{Visits}/{scenario.ServiceVisitId}/check-in", scenario.Technician, null, scenario.VisitETag)),
            Task.Run(() => SendAsync(HttpMethod.Post, $"{Visits}/{scenario.ServiceVisitId}/check-in", scenario.Technician, null, scenario.VisitETag)));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal(1, await WithDbAsync(db => db.WorkSessions.AsNoTracking().CountAsync(s => s.ServiceVisitId == scenario.ServiceVisitId)));
    }

    // ---------------- Concurrency / state: no active overlap (BR-05) ----------------

    [Fact]
    public async Task CheckIn_WhenAlreadyCheckedInElsewhere_Returns409StateConflict()
    {
        var scenario = await ScheduledVisitAsync();
        var checkedIn = await SendAsync(HttpMethod.Post, $"{Visits}/{scenario.ServiceVisitId}/check-in", scenario.Technician, null, scenario.VisitETag);
        Assert.Equal(HttpStatusCode.OK, checkedIn.StatusCode);

        var second = await AnotherScheduledVisitAsync(scenario);

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{Visits}/{second.ServiceVisitId}/check-in", scenario.Technician, null, second.VisitETag),
            HttpStatusCode.Conflict, "STATE_CONFLICT");

        Assert.Equal(1, await WithDbAsync(db => db.WorkSessions.AsNoTracking().CountAsync(s => s.TechnicianId == scenario.Technician.UserId)));
    }

    [Fact]
    public async Task TwoConcurrentCheckIns_ForTheSameTechnicianOnDifferentVisits_ExactlyOneSucceeds()
    {
        var first = await ScheduledVisitAsync();
        var second = await AnotherScheduledVisitAsync(first);

        var responses = await Task.WhenAll(
            Task.Run(() => SendAsync(HttpMethod.Post, $"{Visits}/{first.ServiceVisitId}/check-in", first.Technician, null, first.VisitETag)),
            Task.Run(() => SendAsync(HttpMethod.Post, $"{Visits}/{second.ServiceVisitId}/check-in", first.Technician, null, second.VisitETag)));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal(1, await WithDbAsync(db => db.WorkSessions.AsNoTracking().CountAsync(s => s.TechnicianId == first.Technician.UserId)));
    }

    // ---------------- My Visits ----------------

    [Fact]
    public async Task Mine_ReturnsOnlyTheCallersOwnAssignedScheduledVisits()
    {
        var scenario = await ScheduledVisitAsync();
        var otherTechnicianScenario = await ScheduledVisitAsync();

        var response = await SendAsync(HttpMethod.Get, $"{Visits}/mine", scenario.Technician, null, null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = (await JsonAsync(response)).GetProperty("items").EnumerateArray().ToList();
        var visitId = Assert.Single(items).GetProperty("serviceVisitId").GetGuid();
        Assert.Equal(scenario.ServiceVisitId, visitId);
        Assert.DoesNotContain(items, item => item.GetProperty("serviceVisitId").GetGuid() == otherTechnicianScenario.ServiceVisitId);
    }

    [Fact]
    public async Task Mine_ForADifferentTechnicianInTheSameTenantAndSite_ReturnsNothing()
    {
        var scenario = await ScheduledVisitAsync();
        var otherTechnician = await CallerAsync(scenario.TenantId, [scenario.Site], RoleCodes.Technician);

        var response = await SendAsync(HttpMethod.Get, $"{Visits}/mine", otherTechnician, null, null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Empty(body.GetProperty("items").EnumerateArray());
        Assert.Equal(0, body.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task Mine_ExcludesAVisitAfterTheTechniciansSiteScopeIsRevoked()
    {
        var scenario = await ScheduledVisitAsync();
        await WithDbAsync(db => db.UserSiteScopes
            .Where(scope => scope.TenantId == scenario.TenantId && scope.UserId == scenario.Technician.UserId && scope.SiteId == scenario.Site.Id)
            .ExecuteDeleteAsync());

        var response = await SendAsync(HttpMethod.Get, $"{Visits}/mine", scenario.Technician, null, null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty((await JsonAsync(response)).GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Mine_WithAnInvalidPage_Returns400()
    {
        var scenario = await ScheduledVisitAsync();

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Get, $"{Visits}/mine?page=0", scenario.Technician, null, null),
            HttpStatusCode.BadRequest, "BAD_REQUEST");
    }

    [Fact]
    public async Task Mine_ExcludesAVisitAfterCheckIn_SinceOnlyScheduledIsActionable()
    {
        var scenario = await ScheduledVisitAsync();
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Post, $"{Visits}/{scenario.ServiceVisitId}/check-in", scenario.Technician, null, scenario.VisitETag)).StatusCode);

        var response = await SendAsync(HttpMethod.Get, $"{Visits}/mine", scenario.Technician, null, null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty((await JsonAsync(response)).GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Mine_NonTechnician_Get403()
    {
        var scenario = await ScheduledVisitAsync();

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Get, $"{Visits}/mine", scenario.Coordinator, null, null),
            HttpStatusCode.Forbidden, "ACCESS_DENIED");
    }

    // ---------------- Helpers ----------------

    private static JsonElement SingleVisit(JsonElement workOrderResponse) =>
        Assert.Single(workOrderResponse.GetProperty("visits").EnumerateArray());

    /// <summary>Builds a real SCHEDULED Service Visit through the full real flow: Draft -&gt; Submit -&gt; Approve -&gt; Convert -&gt; Schedule.</summary>
    private async Task<Scenario> ScheduledVisitAsync()
    {
        var tenantId = Guid.NewGuid();
        var site = await SiteAsync(tenantId);
        var admin = await CallerAsync(tenantId, [], RoleCodes.Administrator);
        var owner = await CallerAsync(tenantId, [site], RoleCodes.Requester);
        var approver = await CallerAsync(tenantId, [site], RoleCodes.Approver);
        var coordinator = await CallerAsync(tenantId, [site], RoleCodes.Coordinator);
        var technician = await CallerAsync(tenantId, [site], RoleCodes.Technician);
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(HttpMethod.Post, Routes, admin, new { requestCategoryCode = "ELECTRICAL", siteId = site.Id, approverRoleCode = "APPROVER" }, null)).StatusCode);

        return await ScheduleForAsync(tenantId, site, owner, approver, coordinator, technician, duplicateReason: null);
    }

    /// <summary>
    /// A second, independent SCHEDULED Visit for the same technician/tenant/site (distinct Repair Request/Work
    /// Order — WO-004 is a unique FK). Reuses the first scenario's own Owner/Approver rather than minting new
    /// ones, since a second Approver in the same Site could otherwise be a different one than routing assigns.
    /// </summary>
    private async Task<Scenario> AnotherScheduledVisitAsync(Scenario scenario) =>
        await ScheduleForAsync(
            scenario.TenantId, scenario.Site, scenario.Owner, scenario.Approver, scenario.Coordinator, scenario.Technician,
            duplicateReason: "Second, unrelated fault on the same Site/Category for S3-001 concurrency testing");

    private async Task<Scenario> ScheduleForAsync(
        Guid tenantId, Site site, Caller owner, Caller approver, Caller coordinator, Caller technician, string? duplicateReason)
    {
        var (id, draftETag) = await DraftAsync(owner, site);
        await WithDbAsync(async db =>
        {
            var file = new FileAsset(tenantId, "evidence.png", "image/png", 1024, ValidHash, $"{tenantId:N}/{Guid.NewGuid():N}", owner.UserId, DateTime.UtcNow);
            file.MarkClean();
            db.FileAssets.Add(file);
            db.RepairRequestAttachments.Add(new RepairRequestAttachment(id, file.Id, AttachmentFileRules.AccessScopeCode));
            await db.SaveChangesAsync();
        });

        var submitted = await SendAsync(HttpMethod.Post, $"{Requests}/{id}/submit", owner, new { duplicateContinuationReason = duplicateReason }, draftETag);
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
        var visit = SingleVisit(await JsonAsync(scheduled));

        return new Scenario(
            tenantId, site, owner, approver, coordinator, technician,
            workOrderId, visit.GetProperty("serviceVisitId").GetGuid(), $"\"{visit.GetProperty("rowVersion").GetString()}\"");
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
