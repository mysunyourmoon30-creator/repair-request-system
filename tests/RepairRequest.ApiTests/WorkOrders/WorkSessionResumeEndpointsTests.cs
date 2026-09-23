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

/// <summary>API host with its own disposable LocalDB database for the S3-003 Resume endpoint tests.</summary>
public sealed class WorkSessionResumeApiFactory : AuthApiFactory
{
    public override string ConnectionString =>
        "Server=(localdb)\\MSSQLLocalDB;Database=RepairRequestDb_WorkSessionResumeApiTest;Trusted_Connection=True;TrustServerCertificate=True";
}

/// <summary>
/// WS-API-003 Resume end to end (ST-WS-003; UC-WO-012; S3-003). Technician only, the caller's own PAUSED session
/// only, within tenant and current Site scope; If-Match is checked against the session's own RowVersion; the
/// resume time and status are server-derived; the closed pause period keeps its original PausedAt/PauseReason;
/// the audit is written in the same transaction; multiple Pause/Resume cycles are each their own period row.
/// </summary>
public sealed class WorkSessionResumeEndpointsTests : IClassFixture<WorkSessionResumeApiFactory>
{
    private const string Requests = "/api/v1/repair-requests";
    private const string Routes = "/api/v1/approval-routes";
    private const string WorkOrders = "/api/v1/work-orders";
    private const string Visits = "/api/v1/service-visits";
    private const string Sessions = "/api/v1/work-sessions";
    private const string ValidHash = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";

    private readonly WorkSessionResumeApiFactory _factory;

    public WorkSessionResumeEndpointsTests(WorkSessionResumeApiFactory factory)
    {
        _factory = factory;
    }

    private sealed record Caller(Guid UserId, Guid TenantId, string Token);

    private sealed record Scenario(
        Guid TenantId, Site Site, Caller Owner, Caller Approver, Caller Coordinator, Caller Technician,
        Guid WorkOrderId, Guid ServiceVisitId, string VisitETag);

    private sealed record Checked(Scenario Scenario, Guid SessionId, string ETag);

    private sealed record Paused(Scenario Scenario, Guid SessionId, string ETag, DateTime PausedAt);

    // ---------------- Success ----------------

    [Fact]
    public async Task Resume_Success_ResumesTheSession_ClosesThePause_AndAuditsInTheSameTransaction()
    {
        var p = await PausedAsync();
        var before = DateTime.UtcNow.AddSeconds(-2);

        // Extra members a client might try to send are ignored: status and time are server-derived.
        var response = await SendAsync(HttpMethod.Post, $"{Sessions}/{p.SessionId}/resume", p.Scenario.Technician,
            new { status = "CHECKED_OUT", resumeAt = "2000-01-01T00:00:00Z" }, p.ETag);
        var after = DateTime.UtcNow.AddSeconds(2);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal("CHECKED_IN", body.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("pauseStartAt").ValueKind);
        var resumeAt = body.GetProperty("resumeAt").GetDateTimeOffset().UtcDateTime;
        Assert.InRange(resumeAt, before, after);

        var pauses = body.GetProperty("pauses").EnumerateArray().ToList();
        var pause = Assert.Single(pauses);
        Assert.Equal(resumeAt, pause.GetProperty("resumedAt").GetDateTimeOffset().UtcDateTime);
        Assert.Equal(p.PausedAt, pause.GetProperty("pausedAt").GetDateTimeOffset().UtcDateTime);

        var newETag = $"\"{body.GetProperty("rowVersion").GetString()}\"";
        Assert.Equal(newETag, response.Headers.ETag!.Tag);
        Assert.NotEqual(p.ETag, newETag);

        var session = await WithDbAsync(db => db.WorkSessions.AsNoTracking().SingleAsync(s => s.Id == p.SessionId));
        Assert.Equal(WorkSessionStatus.CheckedIn, session.Status);
        Assert.Null(session.PauseStartAt);
        Assert.Equal(resumeAt, session.ResumeAt);

        var row = await WithDbAsync(db => db.WorkSessionPauses.AsNoTracking().SingleAsync(x => x.WorkSessionId == p.SessionId));
        Assert.Equal(p.PausedAt, row.PausedAt);
        Assert.Equal(resumeAt, row.ResumedAt);

        // Same transaction: exactly one audit row, from PAUSED to CHECKED_IN, by the technician, with the same server time, no reason.
        var audit = await WithDbAsync(db => db.AuditHistory.AsNoTracking().SingleAsync(a => a.EntityId == p.SessionId && a.ActionCode == "WORK_SESSION_RESUMED"));
        Assert.Equal("WORK_SESSION", audit.EntityType);
        Assert.Equal("PAUSED", audit.FromState);
        Assert.Equal("CHECKED_IN", audit.ToState);
        Assert.Null(audit.Reason);
        Assert.Equal(p.Scenario.Technician.UserId, audit.ActorId);
        Assert.Equal(resumeAt, audit.OccurredAt);
        Assert.NotEqual(Guid.Empty, audit.CorrelationId);
        using var auditJson = JsonDocument.Parse(audit.NewValueJson!);
        Assert.Equal(row.Id, auditJson.RootElement.GetProperty("workSessionPauseId").GetGuid());

        // Resume changes neither the Visit nor the Work Order (RR-STS-001 defines no such transition).
        Assert.Equal(ServiceVisitStatus.InProgress, await WithDbAsync(db => db.ServiceVisits.AsNoTracking().Where(v => v.Id == p.Scenario.ServiceVisitId).Select(v => v.Status).SingleAsync()));
        Assert.Equal(WorkOrderStatus.InProgress, await WithDbAsync(db => db.WorkOrders.AsNoTracking().Where(w => w.Id == p.Scenario.WorkOrderId).Select(w => w.Status).SingleAsync()));
    }

    // ---------------- Authorization / scope / IDOR ----------------

    [Fact]
    public async Task Resume_NonTechnician_Get403()
    {
        var p = await PausedAsync();

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{Sessions}/{p.SessionId}/resume", p.Scenario.Coordinator, null, p.ETag),
            HttpStatusCode.Forbidden, "ACCESS_DENIED");
        await AssertStillPausedAsync(p.SessionId, p.PausedAt);
    }

    [Fact]
    public async Task Resume_ByADifferentTechnicianInTheSameTenant_Get404()
    {
        var p = await PausedAsync();
        var other = await CallerAsync(p.Scenario.TenantId, [p.Scenario.Site], RoleCodes.Technician);

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{Sessions}/{p.SessionId}/resume", other, null, p.ETag),
            HttpStatusCode.NotFound, "NOT_FOUND");
        await AssertStillPausedAsync(p.SessionId, p.PausedAt);
    }

    [Fact]
    public async Task Resume_ByATechnicianOfAnotherTenant_Get404()
    {
        var p = await PausedAsync();
        var foreign = await CallerAsync(Guid.NewGuid(), [], RoleCodes.Technician);

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{Sessions}/{p.SessionId}/resume", foreign, null, p.ETag),
            HttpStatusCode.NotFound, "NOT_FOUND");
        await AssertStillPausedAsync(p.SessionId, p.PausedAt);
    }

    [Fact]
    public async Task Resume_ByTheOwnerWhoseSiteScopeWasRevoked_Get404()
    {
        var p = await PausedAsync();
        await WithDbAsync(db => db.UserSiteScopes
            .Where(scope => scope.TenantId == p.Scenario.TenantId && scope.UserId == p.Scenario.Technician.UserId && scope.SiteId == p.Scenario.Site.Id)
            .ExecuteDeleteAsync());

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{Sessions}/{p.SessionId}/resume", p.Scenario.Technician, null, p.ETag),
            HttpStatusCode.NotFound, "NOT_FOUND");
        await AssertStillPausedAsync(p.SessionId, p.PausedAt);
    }

    [Fact]
    public async Task Resume_OfAnUnknownSession_Get404()
    {
        var p = await PausedAsync();

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{Sessions}/{Guid.NewGuid()}/resume", p.Scenario.Technician, null, p.ETag),
            HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task Resume_WithoutIfMatch_Returns400_AndWithoutAToken_Returns401()
    {
        var p = await PausedAsync();

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{Sessions}/{p.SessionId}/resume", p.Scenario.Technician, null, null),
            HttpStatusCode.BadRequest, "BAD_REQUEST");
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await SendAsync(HttpMethod.Post, $"{Sessions}/{p.SessionId}/resume", null, null, p.ETag)).StatusCode);
        await AssertStillPausedAsync(p.SessionId, p.PausedAt);
    }

    // ---------------- State / duplicate / concurrency ----------------

    [Fact]
    public async Task Resume_OfACheckedInSession_Returns409StateConflict()
    {
        var c = await CheckedInAsync();

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{Sessions}/{c.SessionId}/resume", c.Scenario.Technician, null, c.ETag),
            HttpStatusCode.Conflict, "STATE_CONFLICT");

        var session = await WithDbAsync(db => db.WorkSessions.AsNoTracking().SingleAsync(s => s.Id == c.SessionId));
        Assert.Equal(WorkSessionStatus.CheckedIn, session.Status);
        Assert.Null(session.ResumeAt);
        Assert.Equal(0, await WithDbAsync(db => db.AuditHistory.AsNoTracking().CountAsync(a => a.EntityId == c.SessionId && a.ActionCode == "WORK_SESSION_RESUMED")));
    }

    [Fact]
    public async Task ASecondResume_OfAnAlreadyResumedSession_Returns409StateConflict_AndKeepsTheResumeFacts()
    {
        var p = await PausedAsync();
        var first = await SendAsync(HttpMethod.Post, $"{Sessions}/{p.SessionId}/resume", p.Scenario.Technician, null, p.ETag);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var freshETag = first.Headers.ETag!.Tag;
        var resumeAt = (await JsonAsync(first)).GetProperty("resumeAt").GetDateTimeOffset().UtcDateTime;

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{Sessions}/{p.SessionId}/resume", p.Scenario.Technician, null, freshETag),
            HttpStatusCode.Conflict, "STATE_CONFLICT");

        var session = await WithDbAsync(db => db.WorkSessions.AsNoTracking().SingleAsync(s => s.Id == p.SessionId));
        Assert.Equal(resumeAt, session.ResumeAt);
        Assert.Equal(1, await WithDbAsync(db => db.AuditHistory.AsNoTracking().CountAsync(a => a.EntityId == p.SessionId && a.ActionCode == "WORK_SESSION_RESUMED")));
    }

    [Fact]
    public async Task Resume_WithAStaleRowVersion_Returns409ConcurrencyConflict()
    {
        var p = await PausedAsync();
        Assert.Equal(
            HttpStatusCode.OK,
            (await SendAsync(HttpMethod.Post, $"{Sessions}/{p.SessionId}/resume", p.Scenario.Technician, null, p.ETag)).StatusCode);

        // The original (pre-Resume) token is now stale — the row version is checked before the state, so this is
        // CONCURRENCY_CONFLICT, not STATE_CONFLICT, even though the session is also no longer PAUSED.
        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{Sessions}/{p.SessionId}/resume", p.Scenario.Technician, null, p.ETag),
            HttpStatusCode.Conflict, "CONCURRENCY_CONFLICT");
        Assert.Equal(1, await WithDbAsync(db => db.AuditHistory.AsNoTracking().CountAsync(a => a.EntityId == p.SessionId && a.ActionCode == "WORK_SESSION_RESUMED")));
    }

    [Fact]
    public async Task TwoConcurrentResumes_OfTheSameSession_ExactlyOneSucceeds()
    {
        var p = await PausedAsync();

        var responses = await Task.WhenAll(
            Task.Run(() => SendAsync(HttpMethod.Post, $"{Sessions}/{p.SessionId}/resume", p.Scenario.Technician, null, p.ETag)),
            Task.Run(() => SendAsync(HttpMethod.Post, $"{Sessions}/{p.SessionId}/resume", p.Scenario.Technician, null, p.ETag)));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal(1, await WithDbAsync(db => db.AuditHistory.AsNoTracking().CountAsync(a => a.EntityId == p.SessionId && a.ActionCode == "WORK_SESSION_RESUMED")));

        var session = await WithDbAsync(db => db.WorkSessions.AsNoTracking().SingleAsync(s => s.Id == p.SessionId));
        Assert.Equal(WorkSessionStatus.CheckedIn, session.Status);
    }

    // ---------------- Multiple Pause/Resume cycles: history is never rewritten ----------------

    [Fact]
    public async Task PauseResumePauseResume_EachCycleIsItsOwnPeriod_AndEarlierHistoryIsNeverRewritten()
    {
        var p = await PausedAsync();
        var firstResume = await SendAsync(HttpMethod.Post, $"{Sessions}/{p.SessionId}/resume", p.Scenario.Technician, null, p.ETag);
        Assert.Equal(HttpStatusCode.OK, firstResume.StatusCode);
        var afterFirstResume = await JsonAsync(firstResume);

        var secondPause = await SendAsync(HttpMethod.Post, $"{Sessions}/{p.SessionId}/pause", p.Scenario.Technician,
            new { reason = "second interruption" }, $"\"{afterFirstResume.GetProperty("rowVersion").GetString()}\"");
        Assert.Equal(HttpStatusCode.OK, secondPause.StatusCode);
        var afterSecondPause = await JsonAsync(secondPause);

        var secondResume = await SendAsync(HttpMethod.Post, $"{Sessions}/{p.SessionId}/resume", p.Scenario.Technician,
            null, $"\"{afterSecondPause.GetProperty("rowVersion").GetString()}\"");
        Assert.Equal(HttpStatusCode.OK, secondResume.StatusCode);
        var finalBody = await JsonAsync(secondResume);

        Assert.Equal("CHECKED_IN", finalBody.GetProperty("status").GetString());
        var pauses = finalBody.GetProperty("pauses").EnumerateArray().ToList();
        Assert.Equal(2, pauses.Count);

        // Newest first.
        Assert.Equal("second interruption", pauses[0].GetProperty("pauseReason").GetString());
        Assert.NotEqual(JsonValueKind.Null, pauses[0].GetProperty("resumedAt").ValueKind);

        var firstReason = await WithDbAsync(db => db.WorkSessionPauses.AsNoTracking()
            .Where(x => x.WorkSessionId == p.SessionId && x.PausedAt == p.PausedAt)
            .Select(x => x.PauseReason)
            .SingleAsync());
        Assert.Equal("Waiting for a part", firstReason);

        var rows = await WithDbAsync(db => db.WorkSessionPauses.AsNoTracking().Where(x => x.WorkSessionId == p.SessionId).ToListAsync());
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.NotNull(row.ResumedAt));
        Assert.Equal(2, await WithDbAsync(db => db.AuditHistory.AsNoTracking().CountAsync(a => a.EntityId == p.SessionId && a.ActionCode == "WORK_SESSION_RESUMED")));
    }

    // ---------------- Current session read ----------------

    [Fact]
    public async Task Current_AfterResume_ShowsCheckedIn_WithTheClosedPauseInHistory()
    {
        var p = await PausedAsync();
        Assert.Equal(
            HttpStatusCode.OK,
            (await SendAsync(HttpMethod.Post, $"{Sessions}/{p.SessionId}/resume", p.Scenario.Technician, null, p.ETag)).StatusCode);

        var body = (await CurrentAsync(p.Scenario.Technician))!.Value;

        Assert.Equal("CHECKED_IN", body.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("pauseStartAt").ValueKind);
        var pause = Assert.Single(body.GetProperty("pauses").EnumerateArray());
        Assert.NotEqual(JsonValueKind.Null, pause.GetProperty("resumedAt").ValueKind);
    }

    // ---------------- Helpers ----------------

    private async Task AssertStillPausedAsync(Guid sessionId, DateTime pausedAt)
    {
        var session = await WithDbAsync(db => db.WorkSessions.AsNoTracking().SingleAsync(s => s.Id == sessionId));
        Assert.Equal(WorkSessionStatus.Paused, session.Status);
        Assert.Equal(pausedAt, session.PauseStartAt);
        Assert.Null(session.ResumeAt);
        Assert.Equal(0, await WithDbAsync(db => db.AuditHistory.AsNoTracking().CountAsync(a => a.EntityId == sessionId && a.ActionCode == "WORK_SESSION_RESUMED")));

        var openPause = await WithDbAsync(db => db.WorkSessionPauses.AsNoTracking().SingleAsync(x => x.WorkSessionId == sessionId && x.ResumedAt == null));
        Assert.Equal(pausedAt, openPause.PausedAt);
    }

    private async Task<JsonElement?> CurrentAsync(Caller caller)
    {
        var response = await SendAsync(HttpMethod.Get, $"{Sessions}/current", caller, null, null);
        return response.StatusCode == HttpStatusCode.NoContent ? null : await JsonAsync(response);
    }

    /// <summary>A real PAUSED session with a known pause reason/time: the full flow up to Check-in, then a real Pause.</summary>
    private async Task<Paused> PausedAsync()
    {
        var c = await CheckedInAsync();
        var response = await SendAsync(HttpMethod.Post, $"{Sessions}/{c.SessionId}/pause", c.Scenario.Technician, new { reason = "Waiting for a part" }, c.ETag);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        var pausedAt = body.GetProperty("pauseStartAt").GetDateTimeOffset().UtcDateTime;
        return new Paused(c.Scenario, c.SessionId, $"\"{body.GetProperty("rowVersion").GetString()}\"", pausedAt);
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

        return await ScheduleForAsync(tenantId, site, owner, approver, coordinator, technician);
    }

    private async Task<Scenario> ScheduleForAsync(
        Guid tenantId, Site site, Caller owner, Caller approver, Caller coordinator, Caller technician)
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
