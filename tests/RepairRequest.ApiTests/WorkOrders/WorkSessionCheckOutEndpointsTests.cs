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

/// <summary>API host with its own disposable LocalDB database for the S3-004 Check-out endpoint tests.</summary>
public sealed class WorkSessionCheckOutApiFactory : AuthApiFactory
{
    public override string ConnectionString =>
        "Server=(localdb)\\MSSQLLocalDB;Database=RepairRequestDb_WorkSessionCheckOutApiTest;Trusted_Connection=True;TrustServerCertificate=True";
}

/// <summary>
/// WS-API-004 Check-out end to end (ST-WS-004; ST-SV-003; UC-WO-016; S3-004). Technician only, the caller's own
/// CHECKED_IN session only, within tenant and current Site scope; If-Match is checked against the session's own
/// RowVersion; the check-out time and status are server-derived; the Visit reaches COMPLETED in the same
/// transaction; the Work Order is never touched; per the resolved Portfolio Project Owner scope decision, no
/// summary/outcome/evidence is collected or enforced (BR-06's other half is a later ticket).
/// </summary>
public sealed class WorkSessionCheckOutEndpointsTests : IClassFixture<WorkSessionCheckOutApiFactory>
{
    private const string Requests = "/api/v1/repair-requests";
    private const string Routes = "/api/v1/approval-routes";
    private const string WorkOrders = "/api/v1/work-orders";
    private const string Visits = "/api/v1/service-visits";
    private const string Sessions = "/api/v1/work-sessions";
    private const string ValidHash = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";

    private readonly WorkSessionCheckOutApiFactory _factory;

    public WorkSessionCheckOutEndpointsTests(WorkSessionCheckOutApiFactory factory)
    {
        _factory = factory;
    }

    private sealed record Caller(Guid UserId, Guid TenantId, string Token);

    private sealed record Scenario(
        Guid TenantId, Site Site, Caller Owner, Caller Approver, Caller Coordinator, Caller Technician,
        Guid WorkOrderId, Guid ServiceVisitId, string VisitETag);

    private sealed record Checked(Scenario Scenario, Guid SessionId, string ETag);

    // ---------------- Success ----------------

    [Fact]
    public async Task CheckOut_Success_ChecksOutTheSession_CompletesTheVisit_AndAuditsBothInTheSameTransaction()
    {
        var c = await CheckedInAsync();
        var before = DateTime.UtcNow.AddSeconds(-2);

        // Extra members a client might try to send are ignored: status and time are server-derived, and no
        // summary/outcome/evidence is read (per the resolved scope decision).
        var response = await SendAsync(HttpMethod.Post, $"{Sessions}/{c.SessionId}/check-out", c.Scenario.Technician,
            new { status = "SOME_OTHER_STATUS", checkOutAt = "2000-01-01T00:00:00Z", summary = "done", outcome = "REPAIRED" }, c.ETag);
        var after = DateTime.UtcNow.AddSeconds(2);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal("CHECKED_OUT", body.GetProperty("status").GetString());
        var checkOutAt = body.GetProperty("checkOutAt").GetDateTimeOffset().UtcDateTime;
        Assert.InRange(checkOutAt, before, after);

        var newETag = $"\"{body.GetProperty("rowVersion").GetString()}\"";
        Assert.Equal(newETag, response.Headers.ETag!.Tag);
        Assert.NotEqual(c.ETag, newETag);

        var session = await WithDbAsync(db => db.WorkSessions.AsNoTracking().SingleAsync(s => s.Id == c.SessionId));
        Assert.Equal(WorkSessionStatus.CheckedOut, session.Status);
        Assert.Equal(checkOutAt, session.CheckOutAt);

        var visit = await WithDbAsync(db => db.ServiceVisits.AsNoTracking().SingleAsync(v => v.Id == c.Scenario.ServiceVisitId));
        Assert.Equal(ServiceVisitStatus.Completed, visit.Status);
        Assert.Equal(checkOutAt, visit.CompletedAt);

        // Same transaction: one audit row per entity, both with the same server time and no reason.
        var sessionAudit = await WithDbAsync(db => db.AuditHistory.AsNoTracking().SingleAsync(a => a.EntityId == c.SessionId && a.ActionCode == "WORK_SESSION_CHECKED_OUT"));
        Assert.Equal("WORK_SESSION", sessionAudit.EntityType);
        Assert.Equal("CHECKED_IN", sessionAudit.FromState);
        Assert.Equal("CHECKED_OUT", sessionAudit.ToState);
        Assert.Null(sessionAudit.Reason);
        Assert.Equal(c.Scenario.Technician.UserId, sessionAudit.ActorId);
        Assert.Equal(checkOutAt, sessionAudit.OccurredAt);
        Assert.NotEqual(Guid.Empty, sessionAudit.CorrelationId);

        var visitAudit = await WithDbAsync(db => db.AuditHistory.AsNoTracking().SingleAsync(a => a.EntityId == c.Scenario.ServiceVisitId && a.ActionCode == "SERVICE_VISIT_COMPLETED"));
        Assert.Equal("SERVICE_VISIT", visitAudit.EntityType);
        Assert.Equal("IN_PROGRESS", visitAudit.FromState);
        Assert.Equal("COMPLETED", visitAudit.ToState);
        Assert.Null(visitAudit.Reason);
        Assert.Equal(checkOutAt, visitAudit.OccurredAt);
        using var visitAuditJson = JsonDocument.Parse(visitAudit.NewValueJson!);
        Assert.Equal(c.SessionId, visitAuditJson.RootElement.GetProperty("workSessionId").GetGuid());

        // Check-out never touches the Work Order (UC-WO-016 postcondition: "WO remains IN_PROGRESS until summary submit").
        Assert.Equal(WorkOrderStatus.InProgress, await WithDbAsync(db => db.WorkOrders.AsNoTracking().Where(w => w.Id == c.Scenario.WorkOrderId).Select(w => w.Status).SingleAsync()));
    }

    [Fact]
    public async Task AfterCheckOut_TheTechnicianCanCheckInElsewhere()
    {
        var c = await CheckedInAsync();
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Post, $"{Sessions}/{c.SessionId}/check-out", c.Scenario.Technician, null, c.ETag)).StatusCode);

        var second = await AnotherScheduledVisitAsync(c.Scenario);

        // BR-05 "no active overlap": a CHECKED_OUT session no longer blocks Check-in elsewhere.
        Assert.Equal(
            HttpStatusCode.OK,
            (await SendAsync(HttpMethod.Post, $"{Visits}/{second.ServiceVisitId}/check-in", c.Scenario.Technician, null, second.VisitETag)).StatusCode);
    }

    // ---------------- Authorization / scope / IDOR ----------------

    [Fact]
    public async Task CheckOut_NonTechnician_Get403()
    {
        var c = await CheckedInAsync();

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{Sessions}/{c.SessionId}/check-out", c.Scenario.Coordinator, null, c.ETag),
            HttpStatusCode.Forbidden, "ACCESS_DENIED");
        await AssertStillCheckedInAsync(c.SessionId);
    }

    [Fact]
    public async Task CheckOut_ByADifferentTechnicianInTheSameTenant_Get404()
    {
        var c = await CheckedInAsync();
        var other = await CallerAsync(c.Scenario.TenantId, [c.Scenario.Site], RoleCodes.Technician);

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{Sessions}/{c.SessionId}/check-out", other, null, c.ETag),
            HttpStatusCode.NotFound, "NOT_FOUND");
        await AssertStillCheckedInAsync(c.SessionId);
    }

    [Fact]
    public async Task CheckOut_ByATechnicianOfAnotherTenant_Get404()
    {
        var c = await CheckedInAsync();
        var foreign = await CallerAsync(Guid.NewGuid(), [], RoleCodes.Technician);

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{Sessions}/{c.SessionId}/check-out", foreign, null, c.ETag),
            HttpStatusCode.NotFound, "NOT_FOUND");
        await AssertStillCheckedInAsync(c.SessionId);
    }

    [Fact]
    public async Task CheckOut_ByTheOwnerWhoseSiteScopeWasRevoked_Get404()
    {
        var c = await CheckedInAsync();
        await WithDbAsync(db => db.UserSiteScopes
            .Where(scope => scope.TenantId == c.Scenario.TenantId && scope.UserId == c.Scenario.Technician.UserId && scope.SiteId == c.Scenario.Site.Id)
            .ExecuteDeleteAsync());

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{Sessions}/{c.SessionId}/check-out", c.Scenario.Technician, null, c.ETag),
            HttpStatusCode.NotFound, "NOT_FOUND");
        await AssertStillCheckedInAsync(c.SessionId);
    }

    [Fact]
    public async Task CheckOut_OfAnUnknownSession_Get404()
    {
        var c = await CheckedInAsync();

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{Sessions}/{Guid.NewGuid()}/check-out", c.Scenario.Technician, null, c.ETag),
            HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task CheckOut_WithoutIfMatch_Returns400_AndWithoutAToken_Returns401()
    {
        var c = await CheckedInAsync();

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{Sessions}/{c.SessionId}/check-out", c.Scenario.Technician, null, null),
            HttpStatusCode.BadRequest, "BAD_REQUEST");
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await SendAsync(HttpMethod.Post, $"{Sessions}/{c.SessionId}/check-out", null, null, c.ETag)).StatusCode);
        await AssertStillCheckedInAsync(c.SessionId);
    }

    // ---------------- State / duplicate / concurrency ----------------

    [Fact]
    public async Task CheckOut_OfAPausedSession_Returns409StateConflict_AndWritesNothing()
    {
        var c = await CheckedInAsync();
        var paused = await SendAsync(HttpMethod.Post, $"{Sessions}/{c.SessionId}/pause", c.Scenario.Technician, new { reason = "x" }, c.ETag);
        Assert.Equal(HttpStatusCode.OK, paused.StatusCode);
        var pausedETag = $"\"{(await JsonAsync(paused)).GetProperty("rowVersion").GetString()}\"";

        // Confirmed decision: Check-out never auto-resumes. A PAUSED session is rejected outright — the
        // technician must Resume first, then Check-out as a separate call.
        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{Sessions}/{c.SessionId}/check-out", c.Scenario.Technician, null, pausedETag),
            HttpStatusCode.Conflict, "STATE_CONFLICT");

        // The Work Session itself is untouched.
        var session = await WithDbAsync(db => db.WorkSessions.AsNoTracking().SingleAsync(s => s.Id == c.SessionId));
        Assert.Equal(WorkSessionStatus.Paused, session.Status);
        Assert.Null(session.CheckOutAt);

        // The open pause period is NOT auto-closed.
        var openPause = await WithDbAsync(db => db.WorkSessionPauses.AsNoTracking().SingleAsync(p => p.WorkSessionId == c.SessionId && p.ResumedAt == null));
        Assert.Equal("x", openPause.PauseReason);

        // The Service Visit is untouched (still IN_PROGRESS, not COMPLETED).
        var visit = await WithDbAsync(db => db.ServiceVisits.AsNoTracking().SingleAsync(v => v.Id == c.Scenario.ServiceVisitId));
        Assert.Equal(ServiceVisitStatus.InProgress, visit.Status);
        Assert.Null(visit.CompletedAt);

        // No Check-out-related audit rows are written.
        Assert.Equal(0, await WithDbAsync(db => db.AuditHistory.AsNoTracking().CountAsync(a => a.EntityId == c.SessionId && a.ActionCode == "WORK_SESSION_CHECKED_OUT")));
        Assert.Equal(0, await WithDbAsync(db => db.AuditHistory.AsNoTracking().CountAsync(a => a.EntityId == c.Scenario.ServiceVisitId && a.ActionCode == "SERVICE_VISIT_COMPLETED")));
    }

    [Fact]
    public async Task ASecondCheckOut_OfAnAlreadyCheckedOutSession_Returns409StateConflict()
    {
        var c = await CheckedInAsync();
        var first = await SendAsync(HttpMethod.Post, $"{Sessions}/{c.SessionId}/check-out", c.Scenario.Technician, null, c.ETag);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var freshETag = first.Headers.ETag!.Tag;
        var checkOutAt = (await JsonAsync(first)).GetProperty("checkOutAt").GetDateTimeOffset().UtcDateTime;

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{Sessions}/{c.SessionId}/check-out", c.Scenario.Technician, null, freshETag),
            HttpStatusCode.Conflict, "STATE_CONFLICT");

        var session = await WithDbAsync(db => db.WorkSessions.AsNoTracking().SingleAsync(s => s.Id == c.SessionId));
        Assert.Equal(checkOutAt, session.CheckOutAt);
        Assert.Equal(1, await WithDbAsync(db => db.AuditHistory.AsNoTracking().CountAsync(a => a.EntityId == c.SessionId && a.ActionCode == "WORK_SESSION_CHECKED_OUT")));
    }

    [Fact]
    public async Task CheckOut_WithAStaleRowVersion_Returns409ConcurrencyConflict()
    {
        var c = await CheckedInAsync();
        Assert.Equal(
            HttpStatusCode.OK,
            (await SendAsync(HttpMethod.Post, $"{Sessions}/{c.SessionId}/check-out", c.Scenario.Technician, null, c.ETag)).StatusCode);

        // The original (pre-Check-out) token is now stale — the row version is checked before the state.
        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{Sessions}/{c.SessionId}/check-out", c.Scenario.Technician, null, c.ETag),
            HttpStatusCode.Conflict, "CONCURRENCY_CONFLICT");
        Assert.Equal(1, await WithDbAsync(db => db.AuditHistory.AsNoTracking().CountAsync(a => a.EntityId == c.SessionId && a.ActionCode == "WORK_SESSION_CHECKED_OUT")));
    }

    [Fact]
    public async Task TwoConcurrentCheckOuts_OfTheSameSession_ExactlyOneSucceeds()
    {
        var c = await CheckedInAsync();

        var responses = await Task.WhenAll(
            Task.Run(() => SendAsync(HttpMethod.Post, $"{Sessions}/{c.SessionId}/check-out", c.Scenario.Technician, null, c.ETag)),
            Task.Run(() => SendAsync(HttpMethod.Post, $"{Sessions}/{c.SessionId}/check-out", c.Scenario.Technician, null, c.ETag)));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal(1, await WithDbAsync(db => db.AuditHistory.AsNoTracking().CountAsync(a => a.EntityId == c.SessionId && a.ActionCode == "WORK_SESSION_CHECKED_OUT")));
        Assert.Equal(1, await WithDbAsync(db => db.AuditHistory.AsNoTracking().CountAsync(a => a.EntityId == c.Scenario.ServiceVisitId && a.ActionCode == "SERVICE_VISIT_COMPLETED")));

        var session = await WithDbAsync(db => db.WorkSessions.AsNoTracking().SingleAsync(s => s.Id == c.SessionId));
        Assert.Equal(WorkSessionStatus.CheckedOut, session.Status);
    }

    // ---------------- Regression: Pause/Resume are denied after Check-out ----------------

    [Fact]
    public async Task AfterCheckOut_PauseIsDenied_Returns409StateConflict()
    {
        var c = await CheckedInAsync();
        var checkedOut = await SendAsync(HttpMethod.Post, $"{Sessions}/{c.SessionId}/check-out", c.Scenario.Technician, null, c.ETag);
        Assert.Equal(HttpStatusCode.OK, checkedOut.StatusCode);
        var freshETag = $"\"{(await JsonAsync(checkedOut)).GetProperty("rowVersion").GetString()}\"";

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{Sessions}/{c.SessionId}/pause", c.Scenario.Technician, new { reason = "x" }, freshETag),
            HttpStatusCode.Conflict, "STATE_CONFLICT");
    }

    [Fact]
    public async Task AfterCheckOut_ResumeIsDenied_Returns409StateConflict()
    {
        var c = await CheckedInAsync();
        var checkedOut = await SendAsync(HttpMethod.Post, $"{Sessions}/{c.SessionId}/check-out", c.Scenario.Technician, null, c.ETag);
        Assert.Equal(HttpStatusCode.OK, checkedOut.StatusCode);
        var freshETag = $"\"{(await JsonAsync(checkedOut)).GetProperty("rowVersion").GetString()}\"";

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{Sessions}/{c.SessionId}/resume", c.Scenario.Technician, null, freshETag),
            HttpStatusCode.Conflict, "STATE_CONFLICT");
    }

    // ---------------- Current session read ----------------

    [Fact]
    public async Task Current_AfterCheckOut_Returns204()
    {
        var c = await CheckedInAsync();
        Assert.Equal(
            HttpStatusCode.OK,
            (await SendAsync(HttpMethod.Post, $"{Sessions}/{c.SessionId}/check-out", c.Scenario.Technician, null, c.ETag)).StatusCode);

        Assert.Null(await CurrentAsync(c.Scenario.Technician));
    }

    // ---------------- Helpers ----------------

    private async Task AssertStillCheckedInAsync(Guid sessionId)
    {
        var session = await WithDbAsync(db => db.WorkSessions.AsNoTracking().SingleAsync(s => s.Id == sessionId));
        Assert.Equal(WorkSessionStatus.CheckedIn, session.Status);
        Assert.Null(session.CheckOutAt);
        Assert.Equal(0, await WithDbAsync(db => db.AuditHistory.AsNoTracking().CountAsync(a => a.EntityId == sessionId && a.ActionCode == "WORK_SESSION_CHECKED_OUT")));
    }

    private async Task<JsonElement?> CurrentAsync(Caller caller)
    {
        var response = await SendAsync(HttpMethod.Get, $"{Sessions}/current", caller, null, null);
        return response.StatusCode == HttpStatusCode.NoContent ? null : await JsonAsync(response);
    }

    /// <summary>A real CHECKED_IN session: the full flow up to Schedule, then the technician's real Check-in.</summary>
    private async Task<Checked> CheckedInAsync()
    {
        var scenario = await ScheduledVisitAsync();
        var checkedIn = await SendAsync(HttpMethod.Post, $"{Visits}/{scenario.ServiceVisitId}/check-in", scenario.Technician, null, scenario.VisitETag);
        Assert.Equal(HttpStatusCode.OK, checkedIn.StatusCode);

        var current = (await CurrentAsync(scenario.Technician))!.Value;
        return new Checked(scenario, current.GetProperty("workSessionId").GetGuid(), $"\"{current.GetProperty("rowVersion").GetString()}\"");
    }

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

    private async Task<Scenario> AnotherScheduledVisitAsync(Scenario scenario) =>
        await ScheduleForAsync(
            scenario.TenantId, scenario.Site, scenario.Owner, scenario.Approver, scenario.Coordinator, scenario.Technician,
            duplicateReason: "Second, unrelated fault on the same Site/Category for S3-004 testing");

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
        var visit = Assert.Single((await JsonAsync(scheduled)).GetProperty("visits").EnumerateArray());

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
