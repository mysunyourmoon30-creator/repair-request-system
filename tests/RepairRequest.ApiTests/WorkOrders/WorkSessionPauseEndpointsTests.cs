using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
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

/// <summary>API host with its own disposable LocalDB database for the S3-002 Pause endpoint tests.</summary>
public sealed class WorkSessionPauseApiFactory : AuthApiFactory
{
    public override string ConnectionString =>
        "Server=(localdb)\\MSSQLLocalDB;Database=RepairRequestDb_WorkSessionPauseApiTest;Trusted_Connection=True;TrustServerCertificate=True";
}

/// <summary>
/// WS-API-002 Pause and the current-session read end to end (ST-WS-002; UC-WO-012; S3-002). Technician only, the
/// caller's own CHECKED_IN session only, within tenant and current Site scope; If-Match is checked against the
/// session's own RowVersion; a non-blank reason is required; the pause time and status are server-derived; every
/// pause is its own period row and history is never replaced; the audit is written in the same transaction.
/// </summary>
public sealed class WorkSessionPauseEndpointsTests : IClassFixture<WorkSessionPauseApiFactory>
{
    private const string Requests = "/api/v1/repair-requests";
    private const string Routes = "/api/v1/approval-routes";
    private const string WorkOrders = "/api/v1/work-orders";
    private const string Visits = "/api/v1/service-visits";
    private const string Sessions = "/api/v1/work-sessions";
    private const string ValidHash = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";

    private readonly WorkSessionPauseApiFactory _factory;

    public WorkSessionPauseEndpointsTests(WorkSessionPauseApiFactory factory)
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
    public async Task Pause_Success_PausesTheSession_AppendsAPeriod_AndAuditsInTheSameTransaction()
    {
        var c = await CheckedInAsync();
        var before = DateTime.UtcNow.AddSeconds(-2);

        // Extra members a client might try to send are ignored: status and time are server-derived.
        var response = await SendAsync(HttpMethod.Post, $"{Sessions}/{c.SessionId}/pause", c.Scenario.Technician,
            new { reason = "  Waiting for a spare part  ", status = "CHECKED_OUT", pausedAt = "2000-01-01T00:00:00Z", pauseStartAt = "2000-01-01T00:00:00Z" }, c.ETag);
        var after = DateTime.UtcNow.AddSeconds(2);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal("PAUSED", body.GetProperty("status").GetString());
        var pauseStart = body.GetProperty("pauseStartAt").GetDateTimeOffset().UtcDateTime;
        Assert.InRange(pauseStart, before, after);

        var pauses = body.GetProperty("pauses").EnumerateArray().ToList();
        var pause = Assert.Single(pauses);
        Assert.Equal("Waiting for a spare part", pause.GetProperty("pauseReason").GetString());
        Assert.Equal(pauseStart, pause.GetProperty("pausedAt").GetDateTimeOffset().UtcDateTime);
        Assert.Equal(JsonValueKind.Null, pause.GetProperty("resumedAt").ValueKind);

        var newETag = $"\"{body.GetProperty("rowVersion").GetString()}\"";
        Assert.Equal(newETag, response.Headers.ETag!.Tag);
        Assert.NotEqual(c.ETag, newETag);

        var session = await WithDbAsync(db => db.WorkSessions.AsNoTracking().SingleAsync(s => s.Id == c.SessionId));
        Assert.Equal(WorkSessionStatus.Paused, session.Status);
        Assert.Equal(pauseStart, session.PauseStartAt);
        Assert.Null(session.CheckOutAt);

        var row = await WithDbAsync(db => db.WorkSessionPauses.AsNoTracking().SingleAsync(p => p.WorkSessionId == c.SessionId));
        Assert.Equal("Waiting for a spare part", row.PauseReason);
        Assert.Equal(pauseStart, row.PausedAt);
        Assert.Null(row.ResumedAt);

        // Same transaction: exactly one audit row, from CHECKED_IN to PAUSED, by the technician, with the same server time.
        var audit = await WithDbAsync(db => db.AuditHistory.AsNoTracking().SingleAsync(a => a.EntityId == c.SessionId && a.ActionCode == "WORK_SESSION_PAUSED"));
        Assert.Equal("WORK_SESSION", audit.EntityType);
        Assert.Equal("CHECKED_IN", audit.FromState);
        Assert.Equal("PAUSED", audit.ToState);
        Assert.Equal("Waiting for a spare part", audit.Reason);
        Assert.Equal(c.Scenario.Technician.UserId, audit.ActorId);
        Assert.Equal(pauseStart, audit.OccurredAt);
        Assert.NotEqual(Guid.Empty, audit.CorrelationId);
        using var auditJson = JsonDocument.Parse(audit.NewValueJson!);
        Assert.Equal(row.Id, auditJson.RootElement.GetProperty("workSessionPauseId").GetGuid());

        // Pause changes neither the Visit nor the Work Order (RR-STS-001 defines no such transition).
        Assert.Equal(ServiceVisitStatus.InProgress, await WithDbAsync(db => db.ServiceVisits.AsNoTracking().Where(v => v.Id == c.Scenario.ServiceVisitId).Select(v => v.Status).SingleAsync()));
        Assert.Equal(WorkOrderStatus.InProgress, await WithDbAsync(db => db.WorkOrders.AsNoTracking().Where(w => w.Id == c.Scenario.WorkOrderId).Select(w => w.Status).SingleAsync()));
    }

    // ---------------- Authorization / scope / IDOR ----------------

    [Fact]
    public async Task Pause_NonTechnician_Get403()
    {
        var c = await CheckedInAsync();

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{Sessions}/{c.SessionId}/pause", c.Scenario.Coordinator, new { reason = "x" }, c.ETag),
            HttpStatusCode.Forbidden, "ACCESS_DENIED");
        await AssertNothingPausedAsync(c.SessionId);
    }

    [Fact]
    public async Task Pause_ByADifferentTechnicianInTheSameTenant_Get404()
    {
        var c = await CheckedInAsync();
        var other = await CallerAsync(c.Scenario.TenantId, [c.Scenario.Site], RoleCodes.Technician);

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{Sessions}/{c.SessionId}/pause", other, new { reason = "x" }, c.ETag),
            HttpStatusCode.NotFound, "NOT_FOUND");
        await AssertNothingPausedAsync(c.SessionId);
    }

    [Fact]
    public async Task Pause_ByATechnicianOfAnotherTenant_Get404()
    {
        var c = await CheckedInAsync();
        var foreign = await CallerAsync(Guid.NewGuid(), [], RoleCodes.Technician);

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{Sessions}/{c.SessionId}/pause", foreign, new { reason = "x" }, c.ETag),
            HttpStatusCode.NotFound, "NOT_FOUND");
        await AssertNothingPausedAsync(c.SessionId);
    }

    [Fact]
    public async Task Pause_ByTheOwnerWhoseSiteScopeWasRevoked_Get404()
    {
        var c = await CheckedInAsync();
        await WithDbAsync(db => db.UserSiteScopes
            .Where(scope => scope.TenantId == c.Scenario.TenantId && scope.UserId == c.Scenario.Technician.UserId && scope.SiteId == c.Scenario.Site.Id)
            .ExecuteDeleteAsync());

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{Sessions}/{c.SessionId}/pause", c.Scenario.Technician, new { reason = "x" }, c.ETag),
            HttpStatusCode.NotFound, "NOT_FOUND");
        await AssertNothingPausedAsync(c.SessionId);
    }

    [Fact]
    public async Task Pause_OfAnUnknownSession_Get404()
    {
        var c = await CheckedInAsync();

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{Sessions}/{Guid.NewGuid()}/pause", c.Scenario.Technician, new { reason = "x" }, c.ETag),
            HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task Pause_WithoutIfMatch_Returns400_AndWithoutAToken_Returns401()
    {
        var c = await CheckedInAsync();

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{Sessions}/{c.SessionId}/pause", c.Scenario.Technician, new { reason = "x" }, null),
            HttpStatusCode.BadRequest, "BAD_REQUEST");
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await SendAsync(HttpMethod.Post, $"{Sessions}/{c.SessionId}/pause", null, new { reason = "x" }, c.ETag)).StatusCode);
        await AssertNothingPausedAsync(c.SessionId);
    }

    // ---------------- Reason ----------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t \n")]
    public async Task Pause_WithABlankReason_Returns422_AndWritesNothing(string? reason)
    {
        var c = await CheckedInAsync();

        var body = await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{Sessions}/{c.SessionId}/pause", c.Scenario.Technician, new { reason }, c.ETag),
            HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
        Assert.True(body.GetProperty("errors").TryGetProperty("reason", out _));
        await AssertNothingPausedAsync(c.SessionId);
    }

    [Fact]
    public async Task Pause_WithAMissingReasonMember_Returns422()
    {
        var c = await CheckedInAsync();

        var body = await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{Sessions}/{c.SessionId}/pause", c.Scenario.Technician, new { }, c.ETag),
            HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
        Assert.True(body.GetProperty("errors").TryGetProperty("reason", out _));
        await AssertNothingPausedAsync(c.SessionId);
    }

    [Fact]
    public async Task Pause_WithATooLongReason_Returns422_AndTheMaximumLengthIsAccepted()
    {
        var c = await CheckedInAsync();

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{Sessions}/{c.SessionId}/pause", c.Scenario.Technician, new { reason = new string('x', 1001) }, c.ETag),
            HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
        await AssertNothingPausedAsync(c.SessionId);

        Assert.Equal(
            HttpStatusCode.OK,
            (await SendAsync(HttpMethod.Post, $"{Sessions}/{c.SessionId}/pause", c.Scenario.Technician, new { reason = new string('x', 1000) }, c.ETag)).StatusCode);
    }

    // ---------------- State / duplicate / concurrency ----------------

    [Fact]
    public async Task Pause_OfAnAlreadyPausedSession_Returns409StateConflict_AndKeepsTheFirstPause()
    {
        var c = await CheckedInAsync();
        var first = await SendAsync(HttpMethod.Post, $"{Sessions}/{c.SessionId}/pause", c.Scenario.Technician, new { reason = "first" }, c.ETag);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var freshETag = first.Headers.ETag!.Tag;
        var firstStart = (await JsonAsync(first)).GetProperty("pauseStartAt").GetDateTimeOffset().UtcDateTime;

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{Sessions}/{c.SessionId}/pause", c.Scenario.Technician, new { reason = "second" }, freshETag),
            HttpStatusCode.Conflict, "STATE_CONFLICT");

        var row = await WithDbAsync(db => db.WorkSessionPauses.AsNoTracking().SingleAsync(p => p.WorkSessionId == c.SessionId));
        Assert.Equal("first", row.PauseReason);
        Assert.Equal(firstStart, row.PausedAt);
        Assert.Equal(1, await WithDbAsync(db => db.AuditHistory.AsNoTracking().CountAsync(a => a.EntityId == c.SessionId && a.ActionCode == "WORK_SESSION_PAUSED")));
    }

    [Fact]
    public async Task Pause_WithAStaleRowVersion_Returns409ConcurrencyConflict()
    {
        var c = await CheckedInAsync();
        Assert.Equal(
            HttpStatusCode.OK,
            (await SendAsync(HttpMethod.Post, $"{Sessions}/{c.SessionId}/pause", c.Scenario.Technician, new { reason = "first" }, c.ETag)).StatusCode);

        // The original token is now stale (the row version is checked before the state).
        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{Sessions}/{c.SessionId}/pause", c.Scenario.Technician, new { reason = "again" }, c.ETag),
            HttpStatusCode.Conflict, "CONCURRENCY_CONFLICT");
        Assert.Equal(1, await WithDbAsync(db => db.WorkSessionPauses.AsNoTracking().CountAsync(p => p.WorkSessionId == c.SessionId)));
    }

    [Fact]
    public async Task TwoConcurrentPauses_OfTheSameSession_ExactlyOneSucceeds()
    {
        var c = await CheckedInAsync();

        var responses = await Task.WhenAll(
            Task.Run(() => SendAsync(HttpMethod.Post, $"{Sessions}/{c.SessionId}/pause", c.Scenario.Technician, new { reason = "a" }, c.ETag)),
            Task.Run(() => SendAsync(HttpMethod.Post, $"{Sessions}/{c.SessionId}/pause", c.Scenario.Technician, new { reason = "b" }, c.ETag)));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal(1, await WithDbAsync(db => db.WorkSessionPauses.AsNoTracking().CountAsync(p => p.WorkSessionId == c.SessionId)));
        Assert.Equal(1, await WithDbAsync(db => db.AuditHistory.AsNoTracking().CountAsync(a => a.EntityId == c.SessionId && a.ActionCode == "WORK_SESSION_PAUSED")));
    }

    [Fact]
    public async Task ACheckedInTechnicianWhoIsPaused_StillCannotCheckInElsewhere()
    {
        var c = await CheckedInAsync();
        Assert.Equal(
            HttpStatusCode.OK,
            (await SendAsync(HttpMethod.Post, $"{Sessions}/{c.SessionId}/pause", c.Scenario.Technician, new { reason = "lunch" }, c.ETag)).StatusCode);
        var second = await AnotherScheduledVisitAsync(c.Scenario);

        // BR-05: a PAUSED session is still an active (non-CHECKED_OUT) session.
        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{Visits}/{second.ServiceVisitId}/check-in", c.Scenario.Technician, null, second.VisitETag),
            HttpStatusCode.Conflict, "STATE_CONFLICT");
    }

    // ---------------- History is never replaced ----------------

    [Fact]
    public async Task ASecondPauseAfterTheFirstWasClosed_AddsAPeriod_AndKeepsTheEarlierOneUnchanged()
    {
        var c = await CheckedInAsync();
        var first = await SendAsync(HttpMethod.Post, $"{Sessions}/{c.SessionId}/pause", c.Scenario.Technician, new { reason = "first" }, c.ETag);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstRow = await WithDbAsync(db => db.WorkSessionPauses.AsNoTracking().SingleAsync(p => p.WorkSessionId == c.SessionId));

        // Resume is a later ticket, so simulate its effect directly: close the period and return the session to CHECKED_IN.
        var resumedAt = firstRow.PausedAt.AddMinutes(10);
        await WithDbAsync(db => db.WorkSessionPauses.Where(p => p.Id == firstRow.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.ResumedAt, resumedAt)));
        await WithDbAsync(db => db.WorkSessions.Where(s => s.Id == c.SessionId).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, WorkSessionStatus.CheckedIn)));
        var current = await CurrentAsync(c.Scenario.Technician);
        Assert.Equal("CHECKED_IN", current!.Value.GetProperty("status").GetString());

        var second = await SendAsync(HttpMethod.Post, $"{Sessions}/{c.SessionId}/pause", c.Scenario.Technician, new { reason = "second" }, $"\"{current.Value.GetProperty("rowVersion").GetString()}\"");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var rows = await WithDbAsync(db => db.WorkSessionPauses.AsNoTracking().Where(p => p.WorkSessionId == c.SessionId).OrderBy(p => p.PausedAt).ToListAsync());
        Assert.Equal(2, rows.Count);
        Assert.Equal(firstRow.Id, rows[0].Id);
        Assert.Equal("first", rows[0].PauseReason);
        Assert.Equal(firstRow.PausedAt, rows[0].PausedAt);
        Assert.Equal(resumedAt, rows[0].ResumedAt);
        Assert.Equal("second", rows[1].PauseReason);
        Assert.Null(rows[1].ResumedAt);

        var responsePauses = (await JsonAsync(second)).GetProperty("pauses").EnumerateArray().Select(p => p.GetProperty("pauseReason").GetString()).ToList();
        Assert.Equal(["second", "first"], responsePauses);
    }

    [Fact]
    public async Task TheDatabaseItselfRefusesASecondOpenPause_AndABlankReason()
    {
        var c = await CheckedInAsync();
        Assert.Equal(
            HttpStatusCode.OK,
            (await SendAsync(HttpMethod.Post, $"{Sessions}/{c.SessionId}/pause", c.Scenario.Technician, new { reason = "first" }, c.ETag)).StatusCode);

        var secondOpen = await Assert.ThrowsAsync<DbUpdateException>(() => WithDbAsync(async db =>
        {
            db.Add(NewPauseRow(c, "second open pause"));
            await db.SaveChangesAsync();
        }));
        Assert.Contains("IX_work_session_pause_open", secondOpen.InnerException?.Message);

        var other = await CheckedInAsync();
        var blank = await Assert.ThrowsAsync<SqlException>(() => WithDbAsync(db => db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO work_session_pause (work_session_pause_id, tenant_id, work_session_id, paused_at, pause_reason) VALUES ({Guid.NewGuid()}, {other.Scenario.TenantId}, {other.SessionId}, SYSUTCDATETIME(), N'   ')")));
        Assert.Contains("CK_work_session_pause_reason_not_blank", blank.Message);
    }

    // ---------------- Current session read ----------------

    [Fact]
    public async Task Current_WithNoSession_Returns204()
    {
        var scenario = await ScheduledVisitAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Get, $"{Sessions}/current", scenario.Technician, null, null)).StatusCode);
    }

    [Fact]
    public async Task Current_AfterCheckIn_ReturnsTheOwnSession_WithoutPauses_AndAnETag()
    {
        var c = await CheckedInAsync();

        var response = await SendAsync(HttpMethod.Get, $"{Sessions}/current", c.Scenario.Technician, null, null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal(c.SessionId, body.GetProperty("workSessionId").GetGuid());
        Assert.Equal(c.Scenario.ServiceVisitId, body.GetProperty("serviceVisitId").GetGuid());
        Assert.Equal("CHECKED_IN", body.GetProperty("status").GetString());
        Assert.Empty(body.GetProperty("pauses").EnumerateArray());
        Assert.Equal(c.ETag, response.Headers.ETag!.Tag);
    }

    [Fact]
    public async Task Current_ForADifferentTechnician_Returns204_AndForANonTechnician_403()
    {
        var c = await CheckedInAsync();
        var other = await CallerAsync(c.Scenario.TenantId, [c.Scenario.Site], RoleCodes.Technician);

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Get, $"{Sessions}/current", other, null, null)).StatusCode);
        await AssertProblemAsync(
            await SendAsync(HttpMethod.Get, $"{Sessions}/current", c.Scenario.Coordinator, null, null),
            HttpStatusCode.Forbidden, "ACCESS_DENIED");
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, $"{Sessions}/current", null, null, null)).StatusCode);
    }

    [Fact]
    public async Task Current_WhilePaused_ShowsThePauseWithItsReason()
    {
        var c = await CheckedInAsync();
        Assert.Equal(
            HttpStatusCode.OK,
            (await SendAsync(HttpMethod.Post, $"{Sessions}/{c.SessionId}/pause", c.Scenario.Technician, new { reason = "Waiting for access" }, c.ETag)).StatusCode);

        var body = (await CurrentAsync(c.Scenario.Technician))!.Value;

        Assert.Equal("PAUSED", body.GetProperty("status").GetString());
        var pause = Assert.Single(body.GetProperty("pauses").EnumerateArray());
        Assert.Equal("Waiting for access", pause.GetProperty("pauseReason").GetString());
    }

    // ---------------- Helpers ----------------

    private static WorkSessionPause NewPauseRow(Checked c, string reason)
    {
        // Built through the domain's own factory path would be denied (the session is already PAUSED), so this
        // reflects an unsaved row exactly as a buggy writer would produce it, to prove the database backstop.
        var pause = (WorkSessionPause)Activator.CreateInstance(typeof(WorkSessionPause), nonPublic: true)!;
        void Set(string name, object? value) => typeof(WorkSessionPause).GetProperty(name)!.SetValue(pause, value);
        Set(nameof(WorkSessionPause.TenantId), c.Scenario.TenantId);
        Set(nameof(WorkSessionPause.WorkSessionId), c.SessionId);
        Set(nameof(WorkSessionPause.PausedAt), DateTime.UtcNow);
        Set(nameof(WorkSessionPause.PauseReason), reason);
        return pause;
    }

    private async Task AssertNothingPausedAsync(Guid sessionId)
    {
        Assert.Equal(0, await WithDbAsync(db => db.WorkSessionPauses.AsNoTracking().CountAsync(p => p.WorkSessionId == sessionId)));
        Assert.Equal(0, await WithDbAsync(db => db.AuditHistory.AsNoTracking().CountAsync(a => a.EntityId == sessionId && a.ActionCode == "WORK_SESSION_PAUSED")));
        var session = await WithDbAsync(db => db.WorkSessions.AsNoTracking().SingleAsync(s => s.Id == sessionId));
        Assert.Equal(WorkSessionStatus.CheckedIn, session.Status);
        Assert.Null(session.PauseStartAt);
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
            duplicateReason: "Second, unrelated fault on the same Site/Category for S3-002 testing");

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
