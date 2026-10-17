using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RepairRequest.Api.Contracts.WorkOrders;
using RepairRequest.ApiTests.Approvals;
using RepairRequest.ApiTests.Audit;
using RepairRequest.ApiTests.WorkEvidence;
using RepairRequest.Domain.Auditing;
using RepairRequest.Domain.Security;
using RepairRequest.Domain.WorkOrders;

namespace RepairRequest.ApiTests.WorkOrders;

/// <summary>API host with its own disposable LocalDB database for the Time Correction endpoint tests (the shared lifecycle gate still applies).</summary>
public sealed class TimeCorrectionApiFactory : AuthApiFactory
{
    public override string ConnectionString =>
        "Server=(localdb)\\MSSQLLocalDB;Database=RepairRequestDb_TimeCorrectionApiTest;Trusted_Connection=True;TrustServerCertificate=True";
}

/// <summary>
/// TIME-API-001 (Request), TIME-API-002 (Approve), TIME-API-003 (Reject) and the Supervisor's pending list over HTTP (`docs/17`, owner approval
/// of 13 October 2026): roles, scope and non-leaking 404, the exact pause target, the request contract (no client-supplied binding data),
/// If-Match, no self-decision, duplicate pending, the Work Order guard with Reject still allowed, and the live Service Report.
/// </summary>
public sealed class TimeCorrectionEndpointsTests : IClassFixture<TimeCorrectionApiFactory>
{
    private readonly TimeCorrectionApiFactory _factory;
    private readonly EvidenceApiClient _api;

    public TimeCorrectionEndpointsTests(TimeCorrectionApiFactory factory)
    {
        _factory = factory;
        _api = new EvidenceApiClient(factory);
    }

    private sealed record World(EvidenceApiWorld Base, DateTime CheckIn, Guid Pause1, Guid Pause2)
    {
        public DateTime At(int minutes) => CheckIn.AddMinutes(minutes);
    }

    /// <summary>The evidence world (checked-out session on an IN_PROGRESS Work Order) plus two closed pauses: check-in C, pauses C+10..C+20 and C+30..C+40, check-out at C+60.</summary>
    private async Task<World> WorldAsync()
    {
        var world = await _api.CreateWorldAsync();
        var session = await _factory.WithDbAsync(db => db.WorkSessions.AsNoTracking().Where(item => item.ServiceVisitId == world.VisitId).Select(item => new { item.Id, item.CheckInAt }).SingleAsync());
        await _factory.WithDbAsync(db => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [work_session] SET [check_out_at] = {session.CheckInAt.AddMinutes(60)} WHERE [work_session_id] = {session.Id}"));
        var pause1 = Guid.NewGuid();
        var pause2 = Guid.NewGuid();
        await _factory.WithDbAsync(db => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO [work_session_pause] ([work_session_pause_id], [tenant_id], [work_session_id], [paused_at], [pause_reason], [resumed_at])
            VALUES ({pause1}, {world.TenantId}, {session.Id}, {session.CheckInAt.AddMinutes(10)}, 'Waiting', {session.CheckInAt.AddMinutes(20)}),
                   ({pause2}, {world.TenantId}, {session.Id}, {session.CheckInAt.AddMinutes(30)}, 'Waiting', {session.CheckInAt.AddMinutes(40)})
            """));
        return new World(world, session.CheckInAt, pause1, pause2);
    }

    private Task<Guid> SessionIdAsync(World world) =>
        _factory.WithDbAsync(db => db.WorkSessions.AsNoTracking().Where(item => item.ServiceVisitId == world.Base.VisitId).Select(item => item.Id).SingleAsync());

    private static string Iso(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);

    private async Task<HttpResponseMessage> RequestAsync(
        World world, EvidenceCaller? caller, string type, DateTime newValue, Guid? pauseId = null, object? extra = null, string reason = "The technician forgot to press the button.")
    {
        var sessionId = await SessionIdAsync(world);
        var body = new Dictionary<string, object?> { ["timestampType"] = type, ["newValue"] = Iso(newValue), ["reason"] = reason };
        if (pauseId is not null)
        {
            body["workSessionPauseId"] = pauseId;
        }

        if (extra is not null)
        {
            foreach (var property in extra.GetType().GetProperties())
            {
                body[property.Name] = property.GetValue(extra);
            }
        }

        return await _api.SendAsync(HttpMethod.Post, $"/api/v1/work-sessions/{sessionId}/time-corrections", caller, JsonContent.Create(body));
    }

    private async Task<(Guid Id, string ETag)> RequestOkAsync(World world, EvidenceCaller caller, string type, DateTime newValue, Guid? pauseId = null)
    {
        var response = await RequestAsync(world, caller, type, newValue, pauseId);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return ((await EvidenceApiClient.Json(response)).GetProperty("timeCorrectionId").GetGuid(), response.Headers.ETag!.Tag);
    }

    private Task<HttpResponseMessage> DecideAsync(string action, Guid id, EvidenceCaller? caller, string? ifMatch, object? body = null) =>
        _api.SendAsync(HttpMethod.Post, $"/api/v1/time-corrections/{id}/{action}", caller, body is null ? null : JsonContent.Create(body), ifMatch);

    private static async Task<string> CodeAsync(HttpResponseMessage response) => (await EvidenceApiClient.Json(response)).GetProperty("code").GetString()!;

    // ---------------- request ----------------

    [Fact]
    public async Task Request_ByTheOwningTechnician_Returns201_WithETagAndAPendingBodyAndNoLocation_AndChangesNoTimeFact()
    {
        var world = await WorldAsync();

        var response = await RequestAsync(world, world.Base.Technician, "CHECK_IN", world.At(-5));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await EvidenceApiClient.Json(response);
        var id = body.GetProperty("timeCorrectionId").GetGuid();
        Assert.Null(response.Headers.Location);                              // no single-item GET route exists to point at
        Assert.NotNull(response.Headers.ETag);                               // the token the decision's If-Match needs
        Assert.Equal(id, (await _factory.WithDbAsync(db => db.TimeCorrections.AsNoTracking().SingleAsync(item => item.Id == id))).Id);
        Assert.Equal("PENDING", body.GetProperty("status").GetString());
        Assert.Equal("CHECK_IN", body.GetProperty("timestampType").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("workSessionPauseId").ValueKind);
        Assert.Equal(world.CheckIn, body.GetProperty("oldValue").GetDateTime().ToUniversalTime(), TimeSpan.FromMilliseconds(1));
        Assert.Equal(world.Base.Technician.UserId, body.GetProperty("requestedBy").GetGuid());
        Assert.Equal(world.Base.WorkOrderId, body.GetProperty("workOrderId").GetGuid());

        // The response carries no SLA, tenant or server-only member.
        var names = EvidenceApiClient.PropertyNames(body).Select(name => name.ToLowerInvariant()).ToList();
        Assert.DoesNotContain(names, name => name.Contains("sla") || name.Contains("tenant"));
        Assert.Equal(
            world.CheckIn,
            await _factory.WithDbAsync(db => db.WorkSessions.AsNoTracking().Where(item => item.ServiceVisitId == world.Base.VisitId).Select(item => item.CheckInAt).SingleAsync()));
    }

    [Fact]
    public async Task Request_ForAPauseType_NamesTheExactPause_ItIsEchoedBack_AndAMissingOrForeignPauseIs422()
    {
        var world = await WorldAsync();
        var other = await WorldAsync();

        var ok = await RequestAsync(world, world.Base.TeamLead, "PAUSE_START", world.At(12), world.Pause1);
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        Assert.Equal(world.Pause1, (await EvidenceApiClient.Json(ok)).GetProperty("workSessionPauseId").GetGuid());

        var missing = await RequestAsync(world, world.Base.TeamLead, "RESUME", world.At(22));
        var foreign = await RequestAsync(world, world.Base.TeamLead, "RESUME", world.At(22), other.Pause1);
        var forCheckIn = await RequestAsync(world, world.Base.TeamLead, "CHECK_IN", world.At(-5), world.Pause2);
        foreach (var bad in new[] { missing, foreign, forCheckIn })
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
            Assert.Contains("workSessionPauseId", (await EvidenceApiClient.Json(bad)).GetProperty("errors").EnumerateObject().Select(property => property.Name));
        }
    }

    [Fact]
    public async Task Request_Authorization_Technician_AndTeamLeadOnly_EveryoneElseIs403_AndAnonymousIs401()
    {
        var world = await WorldAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await RequestAsync(world, null, "CHECK_IN", world.At(-5))).StatusCode);
        foreach (var caller in new[] { world.Base.Supervisor, world.Base.Coordinator, world.Base.Approver, world.Base.Administrator, world.Base.Contact })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await RequestAsync(world, caller, "CHECK_IN", world.At(-5))).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Created, (await RequestAsync(world, world.Base.TeamLead, "CHECK_IN", world.At(-5))).StatusCode);
    }

    [Fact]
    public async Task Request_ForAnotherTechniciansSession_AnOutOfSiteTeamLead_OrAnotherTenant_IsTheSameNonLeaking404()
    {
        var world = await WorldAsync();
        var other = await WorldAsync();
        var codes = new List<string>();

        foreach (var caller in new[] { world.Base.OtherTechnician, world.Base.OutOfSiteTeamLead, other.Base.Technician, other.Base.TeamLead })
        {
            var response = await RequestAsync(world, caller, "CHECK_IN", world.At(-5));
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            codes.Add(await CodeAsync(response));
        }

        var unknown = await _api.SendAsync(
            HttpMethod.Post, $"/api/v1/work-sessions/{Guid.NewGuid()}/time-corrections", world.Base.TeamLead,
            JsonContent.Create(new { timestampType = "CHECK_IN", newValue = Iso(world.At(-5)), reason = "x" }));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        codes.Add(await CodeAsync(unknown));
        Assert.Single(codes.Distinct());
    }

    [Fact]
    public async Task Request_Validation_BadTypeFutureEqualBrokenChronologyBlankReasonAndOffsetlessTime()
    {
        var world = await WorldAsync();

        var cases = new (string Type, DateTime Value, string Reason, string Field)[]
        {
            ("CHECK_SIDEWAYS", world.At(-5), "x", "timestampType"),
            ("CHECK_IN", world.At(15), "x", "newValue"),                       // after the first pause started
            ("CHECK_OUT", world.CheckIn.AddMinutes(60), "x", "newValue"),      // equal to the current value
            ("CHECK_OUT", DateTime.UtcNow.AddMinutes(5), "x", "newValue"),     // in the future
            ("CHECK_IN", world.At(-5), "   ", "reason")
        };
        foreach (var (type, value, reason, field) in cases)
        {
            var response = await RequestAsync(world, world.Base.TeamLead, type, value, reason: reason);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Contains(field, (await EvidenceApiClient.Json(response)).GetProperty("errors").EnumerateObject().Select(property => property.Name));
        }

        var sessionId = await SessionIdAsync(world);
        var offsetless = await _api.SendAsync(
            HttpMethod.Post, $"/api/v1/work-sessions/{sessionId}/time-corrections", world.Base.TeamLead,
            JsonContent.Create(new { timestampType = "CHECK_IN", newValue = "2026-10-01T08:00:00", reason = "x" }));
        Assert.Equal(HttpStatusCode.BadRequest, offsetless.StatusCode);
        Assert.Equal(0, await _factory.WithDbAsync(db => db.TimeCorrections.CountAsync(item => item.TenantId == world.Base.TenantId)));
    }

    [Fact]
    public async Task Request_OfAValueEqualToTheCurrentOne_Is422ForEveryType_AndWritesNeitherACorrectionNorAnAudit()
    {
        var world = await WorldAsync();

        var noOps = new (string Type, DateTime Value, Guid? Pause)[]
        {
            ("CHECK_IN", world.CheckIn, null),
            ("CHECK_OUT", world.At(60), null),
            ("PAUSE_START", world.At(10), world.Pause1),
            ("RESUME", world.At(20), world.Pause1)
        };
        foreach (var (type, value, pause) in noOps)
        {
            var response = await RequestAsync(world, world.Base.TeamLead, type, value, pause);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Contains("newValue", (await EvidenceApiClient.Json(response)).GetProperty("errors").EnumerateObject().Select(property => property.Name));
            Assert.Null(response.Headers.ETag);
        }

        Assert.Equal(0, await _factory.WithDbAsync(db => db.TimeCorrections.CountAsync(item => item.TenantId == world.Base.TenantId)));
        Assert.Equal(0, await _factory.WithDbAsync(db => db.AuditHistory.CountAsync(item => item.TenantId == world.Base.TenantId && item.EntityType == "TIME_CORRECTION")));

        // The rejected no-ops left no pending slot behind: a genuine change of the same targets is accepted.
        Assert.Equal(HttpStatusCode.Created, (await RequestAsync(world, world.Base.TeamLead, "CHECK_IN", world.At(-5))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await RequestAsync(world, world.Base.TeamLead, "PAUSE_START", world.At(12), world.Pause1)).StatusCode);
        Assert.Equal(2, await _factory.WithDbAsync(db => db.AuditHistory.CountAsync(item => item.TenantId == world.Base.TenantId && item.EntityType == "TIME_CORRECTION")));
    }

    // ---------------- TIME-API-ADD-002: the request targets (sessions + pause ids) of ONE Work Order inside the caller's request scope ----------------

    private Task<HttpResponseMessage> TargetsAsync(EvidenceCaller? caller, Guid workOrderId) =>
        _api.SendAsync(HttpMethod.Get, $"/api/v1/work-orders/{workOrderId}/time-correction-targets", caller);

    [Fact]
    public async Task RequestTargets_ForTheOwningTechnician_ListTheCompletedSessionWithItsPauseIdsInOrder_AndNothingElse()
    {
        var world = await WorldAsync();
        var sessionId = await SessionIdAsync(world);

        var response = await TargetsAsync(world.Base.Technician, world.Base.WorkOrderId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString() ?? string.Empty, StringComparison.Ordinal);
        Assert.Null(response.Headers.ETag);
        var body = await EvidenceApiClient.Json(response);
        Assert.Equal(world.Base.WorkOrderId, body.GetProperty("workOrderId").GetGuid());
        Assert.Equal("IN_PROGRESS", body.GetProperty("workOrderStatus").GetString());
        var sessions = body.GetProperty("sessions").EnumerateArray().ToList();
        var session = Assert.Single(sessions);
        Assert.Equal(sessionId, session.GetProperty("workSessionId").GetGuid());
        Assert.Equal("CHECKED_OUT", session.GetProperty("status").GetString());                       // completed sessions are included
        Assert.Equal(world.CheckIn, session.GetProperty("checkInAt").GetDateTime().ToUniversalTime(), TimeSpan.FromMilliseconds(1));
        Assert.Equal(world.At(60), session.GetProperty("checkOutAt").GetDateTime().ToUniversalTime(), TimeSpan.FromMilliseconds(1));
        var pauses = session.GetProperty("pauses").EnumerateArray().ToList();
        Assert.Equal([world.Pause1, world.Pause2], pauses.Select(pause => pause.GetProperty("workSessionPauseId").GetGuid()));
        Assert.Equal(world.At(10), pauses[0].GetProperty("pausedAt").GetDateTime().ToUniversalTime(), TimeSpan.FromMilliseconds(1));
        Assert.Equal(world.At(20), pauses[0].GetProperty("resumedAt").GetDateTime().ToUniversalTime(), TimeSpan.FromMilliseconds(1));

        // No tenant id, no reason or pending information, no SLA / cost / acceptance member, no other user's id.
        var names = TimelineRedactionAssertions.PropertyNames(body).Select(name => name.ToLowerInvariant()).ToList();
        Assert.DoesNotContain(names, name => name.Contains("tenant") || name.Contains("sla") || name.Contains("reason") || name.Contains("pending")
            || name.Contains("cost") || name.Contains("acceptance") || name.Contains("technician") || name.Contains("requestedby") || name.Contains("rowversion"));
    }

    /// <summary>A second Visit of the SAME Work Order, assigned to the other Technician, with a checked-out session and one closed pause of their own.</summary>
    private async Task<(Guid SessionId, Guid PauseId)> AddOtherTechniciansSessionAsync(World world)
    {
        var sessionId = await _factory.WithDbAsync(async db =>
        {
            var visit = ServiceVisit.Create(
                world.Base.TenantId, world.Base.WorkOrderId, ServiceVisitType.Initial, Guid.NewGuid(), world.Base.OtherTechnician.UserId, DateTime.UtcNow.AddHours(1), DateTime.UtcNow.AddHours(3));
            visit.CheckIn();
            db.ServiceVisits.Add(visit);
            var session = WorkSession.Create(world.Base.TenantId, visit.Id, world.Base.OtherTechnician.UserId, DateTime.UtcNow.AddHours(-2));
            session.CheckOut(DateTime.UtcNow.AddHours(-1));
            db.WorkSessions.Add(session);
            await db.SaveChangesAsync();
            return session.Id;
        });
        var pauseId = Guid.NewGuid();
        await _factory.WithDbAsync(db => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO [work_session_pause] ([work_session_pause_id], [tenant_id], [work_session_id], [paused_at], [pause_reason], [resumed_at])
            VALUES ({pauseId}, {world.Base.TenantId}, {sessionId}, {DateTime.UtcNow.AddMinutes(-100)}, 'Waiting', {DateTime.UtcNow.AddMinutes(-90)})
            """));
        return (sessionId, pauseId);
    }

    [Fact]
    public async Task RequestTargets_OnAMixedOwnerWorkOrder_GiveEachTechnicianOnlyTheirOwnSessionsAndPauses_AndTheTeamLeadBoth()
    {
        var world = await WorldAsync();
        var mine = await SessionIdAsync(world);
        var theirs = await AddOtherTechniciansSessionAsync(world);

        var asTechnician = await TargetsAsync(world.Base.Technician, world.Base.WorkOrderId);
        var asOther = await TargetsAsync(world.Base.OtherTechnician, world.Base.WorkOrderId);
        var asTeamLead = await TargetsAsync(world.Base.TeamLead, world.Base.WorkOrderId);

        Assert.Equal(HttpStatusCode.OK, asTechnician.StatusCode);
        var technicianText = await asTechnician.Content.ReadAsStringAsync();
        var technicianSession = Assert.Single(JsonDocument.Parse(technicianText).RootElement.GetProperty("sessions").EnumerateArray());
        Assert.Equal(mine, technicianSession.GetProperty("workSessionId").GetGuid());
        Assert.Equal([world.Pause1, world.Pause2], technicianSession.GetProperty("pauses").EnumerateArray().Select(pause => pause.GetProperty("workSessionPauseId").GetGuid()));
        Assert.DoesNotContain(theirs.SessionId.ToString(), technicianText, StringComparison.OrdinalIgnoreCase);       // the other technician's session and pause never leak
        Assert.DoesNotContain(theirs.PauseId.ToString(), technicianText, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(HttpStatusCode.OK, asOther.StatusCode);
        var otherText = await asOther.Content.ReadAsStringAsync();
        var otherSession = Assert.Single(JsonDocument.Parse(otherText).RootElement.GetProperty("sessions").EnumerateArray());
        Assert.Equal(theirs.SessionId, otherSession.GetProperty("workSessionId").GetGuid());
        Assert.Equal([theirs.PauseId], otherSession.GetProperty("pauses").EnumerateArray().Select(pause => pause.GetProperty("workSessionPauseId").GetGuid()));
        Assert.DoesNotContain(mine.ToString(), otherText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(world.Pause1.ToString(), otherText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(world.Pause2.ToString(), otherText, StringComparison.OrdinalIgnoreCase);

        // The Team Lead's read stays Site-scoped, not owner-scoped: both sessions, each with its own pauses.
        Assert.Equal(HttpStatusCode.OK, asTeamLead.StatusCode);
        var teamLeadSessions = (await EvidenceApiClient.Json(asTeamLead)).GetProperty("sessions").EnumerateArray().ToList();
        Assert.Equal(2, teamLeadSessions.Count);
        Assert.Equal(
            new[] { world.Pause1, world.Pause2, theirs.PauseId }.Order(),
            teamLeadSessions.SelectMany(item => item.GetProperty("pauses").EnumerateArray()).Select(pause => pause.GetProperty("workSessionPauseId").GetGuid()).Order());
    }

    [Fact]
    public async Task RequestTargets_ForATeamLeadOfTheSite_SeeTheSessions_AndAnOutOfSiteTeamLeadAndAnotherTechnicianGetTheSameNotFound()
    {
        var world = await WorldAsync();

        var teamLead = await TargetsAsync(world.Base.TeamLead, world.Base.WorkOrderId);
        Assert.Equal(HttpStatusCode.OK, teamLead.StatusCode);
        Assert.Single((await EvidenceApiClient.Json(teamLead)).GetProperty("sessions").EnumerateArray());

        var codes = new List<string>();
        foreach (var caller in new[] { world.Base.OutOfSiteTeamLead, world.Base.OtherTechnician })
        {
            var response = await TargetsAsync(caller, world.Base.WorkOrderId);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            codes.Add(await CodeAsync(response));
        }

        var unknown = await TargetsAsync(world.Base.TeamLead, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        codes.Add(await CodeAsync(unknown));
        Assert.Single(codes.Distinct());
    }

    [Fact]
    public async Task RequestTargets_AreForbiddenToEveryOtherRole_AndUnauthenticatedIs401()
    {
        var world = await WorldAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await TargetsAsync(null, world.Base.WorkOrderId)).StatusCode);
        foreach (var caller in new[] { world.Base.Supervisor, world.Base.Coordinator, world.Base.Approver, world.Base.Administrator, world.Base.Contact, world.Base.OtherTenantSupervisor })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await TargetsAsync(caller, world.Base.WorkOrderId)).StatusCode);
        }
    }

    [Fact]
    public async Task RequestTargets_StayReadableAfterTheWorkOrderIsClosed_AndSayWhichStatusItIs()
    {
        var world = await WorldAsync();
        await _factory.WithDbAsync(db => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [work_order] SET [status] = 'CLOSED' WHERE [work_order_id] = {world.Base.WorkOrderId}"));

        var response = await TargetsAsync(world.Base.TeamLead, world.Base.WorkOrderId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("CLOSED", (await EvidenceApiClient.Json(response)).GetProperty("workOrderStatus").GetString());
    }

    [Fact]
    public async Task RequestTargets_WriteNoAuditRow_AndAnotherTenantsDataNeverAppears()
    {
        var world = await WorldAsync();
        var other = await WorldAsync();                                  // another tenant with its own session and pauses
        var before = await _factory.WithDbAsync(db => db.AuditHistory.CountAsync());

        var response = await TargetsAsync(world.Base.TeamLead, world.Base.WorkOrderId);

        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(other.Pause1.ToString(), text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(other.Pause2.ToString(), text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, await _factory.WithDbAsync(db => db.AuditHistory.CountAsync()));
        Assert.Equal(HttpStatusCode.NotFound, (await TargetsAsync(world.Base.TeamLead, other.Base.WorkOrderId)).StatusCode);   // the other tenant's Work Order is the same 404
    }

    // ---------------- timeline (docs/17 P11): the existing AUD-API-001 route shows the three event types ----------------

    private Task<HttpResponseMessage> TimelineAsync(EvidenceCaller? caller, Guid workOrderId, int pageSize = 100) =>
        _api.SendAsync(HttpMethod.Get, $"/api/v1/entities/WORK_ORDER/{workOrderId}/timeline?pageSize={pageSize}", caller);

    [Fact]
    public async Task Timeline_ShowsRequestedApprovedAndRejectedEvents_ToTheExistingReaders_WithAllowlistedDetailsOnly()
    {
        const string requestReason = "SENTINEL-TIMELINE-REQUEST-REASON-41f2";
        const string decisionReason = "SENTINEL-TIMELINE-DECISION-REASON-41f2";
        var world = await WorldAsync();
        var sessionId = await SessionIdAsync(world);

        var approved = await RequestAsync(world, world.Base.TeamLead, "CHECK_IN", world.At(-5), reason: requestReason);
        var approvedId = (await EvidenceApiClient.Json(approved)).GetProperty("timeCorrectionId").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await DecideAsync("approve", approvedId, world.Base.Supervisor, approved.Headers.ETag!.Tag)).StatusCode);
        var rejected = await RequestAsync(world, world.Base.TeamLead, "CHECK_OUT", world.At(55), reason: requestReason);
        var rejectedId = (await EvidenceApiClient.Json(rejected)).GetProperty("timeCorrectionId").GetGuid();
        Assert.Equal(
            HttpStatusCode.OK,
            (await DecideAsync("reject", rejectedId, world.Base.Supervisor, rejected.Headers.ETag!.Tag, new { decisionReason })).StatusCode);

        foreach (var reader in new[] { world.Base.Supervisor, world.Base.TeamLead, world.Base.Coordinator })
        {
            var response = await TimelineAsync(reader, world.Base.WorkOrderId);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var text = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain(requestReason, text, StringComparison.Ordinal);
            Assert.DoesNotContain(decisionReason, text, StringComparison.Ordinal);
            Assert.DoesNotContain("visitCompletedAtSynchronized", text, StringComparison.OrdinalIgnoreCase);
            TimelineRedactionAssertions.AssertPage(JsonDocument.Parse(text).RootElement);

            var items = JsonDocument.Parse(text).RootElement.GetProperty("items").EnumerateArray()
                .Where(item => item.GetProperty("entityType").GetString() == "TIME_CORRECTION").ToList();
            Assert.Equal(
                new[] { "TIME_CORRECTION_APPROVED", "TIME_CORRECTION_REJECTED", "TIME_CORRECTION_REQUESTED", "TIME_CORRECTION_REQUESTED" },
                items.Select(item => item.GetProperty("actionCode").GetString()!).Order(StringComparer.Ordinal));
            Assert.All(items, item =>
            {
                var details = item.GetProperty("details").EnumerateObject().Select(property => property.Name).ToList();
                Assert.All(details, name => Assert.Contains(name, new[] { "workSessionId", "workSessionPauseId", "timestampType", "oldValue", "newValue" }));
                Assert.Equal(sessionId, Guid.Parse(item.GetProperty("details").GetProperty("workSessionId").GetString()!));
                Assert.Equal("USER", item.GetProperty("actor").GetProperty("kind").GetString());
            });
            var approvedEvent = items.Single(item => item.GetProperty("actionCode").GetString() == "TIME_CORRECTION_APPROVED");
            Assert.Equal(world.CheckIn, approvedEvent.GetProperty("details").GetProperty("oldValue").GetDateTime().ToUniversalTime(), TimeSpan.FromMilliseconds(1));
            Assert.Equal(world.At(-5), approvedEvent.GetProperty("details").GetProperty("newValue").GetDateTime().ToUniversalTime(), TimeSpan.FromMilliseconds(1));
            Assert.Equal(world.Base.Supervisor.UserId, approvedEvent.GetProperty("actor").GetProperty("actorId").GetGuid());
        }

        // Unchanged access rules: Technician and Administrator are refused, another tenant gets the identical not-found, anonymous is 401.
        foreach (var refused in new[] { world.Base.Technician, world.Base.Administrator })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await TimelineAsync(refused, world.Base.WorkOrderId)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await TimelineAsync(world.Base.OtherTenantSupervisor, world.Base.WorkOrderId)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await TimelineAsync(null, world.Base.WorkOrderId)).StatusCode);
    }

    [Fact]
    public async Task Timeline_MixedPages_NeverExposeBeforeAfterOutsideTheThreeCorrectionActions_EvenForForgedPayloads()
    {
        const string forged =
            "{\"workSessionId\":\"11111111-1111-1111-1111-111111111111\",\"timestampType\":\"CHECK_IN\",\"oldValue\":\"2026-01-01T00:00:00\","
            + "\"newValue\":\"2026-01-02T00:00:00\",\"pausedAt\":\"2026-01-03T00:00:00\",\"reason\":\"SENTINEL-FORGED-REASON\","
            + "\"visitCompletedAtSynchronized\":true,\"nested\":{\"oldValue\":\"x\"}}";
        var world = await WorldAsync();
        var sessionId = await SessionIdAsync(world);
        var request = await RequestAsync(world, world.Base.TeamLead, "CHECK_IN", world.At(-5));
        var correctionId = (await EvidenceApiClient.Json(request)).GetProperty("timeCorrectionId").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await DecideAsync("approve", correctionId, world.Base.Supervisor, request.Headers.ETag!.Tag)).StatusCode);

        // Legacy and future events interleaved with the real ones, all carrying before/after keys in their payload.
        var now = DateTime.UtcNow;
        var actor = world.Base.Supervisor.UserId;
        var tenant = world.Base.TenantId;
        await _factory.WithDbAsync(async db =>
        {
            AuditHistory Row(string type, Guid entityId, string action, int seconds) =>
                new(tenant, type, entityId, action, null, null, null, forged, "SENTINEL-FORGED-ROW-REASON", actor, now.AddSeconds(seconds), Guid.NewGuid());
            db.AuditHistory.AddRange(
                Row("WORK_ORDER", world.Base.WorkOrderId, "WORK_ORDER_STARTED", 1),                 // not allowlisted at all
                Row("WORK_SESSION", sessionId, "WORK_SESSION_PAUSED", 2),                         // allowlisted: only pausedAt may pass
                Row("TIME_CORRECTION", correctionId, "TIME_CORRECTION_FUTURE_ACTION", 3),         // unknown action on a correction row
                Row("WORK_SESSION", sessionId, "TIME_CORRECTION_APPROVED", 4),                    // correction action on another entity type
                Row("SERVICE_VISIT", world.Base.VisitId, "SOME_FUTURE_UNKNOWN_CODE", 5));
            await db.SaveChangesAsync();
            return 0;
        });

        // Every page size walks the same mixed set; every page passes the context-aware redaction rules.
        foreach (var pageSize in new[] { 1, 2, 3, 100 })
        {
            var items = new List<JsonElement>();
            string? cursor = null;
            do
            {
                var url = $"/api/v1/entities/WORK_ORDER/{world.Base.WorkOrderId}/timeline?pageSize={pageSize}" + (cursor is null ? string.Empty : $"&cursor={Uri.EscapeDataString(cursor)}");
                var response = await _api.SendAsync(HttpMethod.Get, url, world.Base.Supervisor);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var text = await response.Content.ReadAsStringAsync();
                Assert.DoesNotContain("SENTINEL-FORGED", text, StringComparison.Ordinal);
                var page = JsonDocument.Parse(text).RootElement.Clone();
                TimelineRedactionAssertions.AssertPage(page);
                items.AddRange(page.GetProperty("items").EnumerateArray());
                cursor = page.GetProperty("nextCursor").GetString();
            }
            while (cursor is not null);

            Assert.Equal(items.Count, items.Select(item => item.GetProperty("auditId").GetGuid()).Distinct().Count());
            var forgedRows = items.Where(item => item.GetProperty("actionCode").GetString() is "WORK_ORDER_STARTED" or "WORK_SESSION_PAUSED" or "TIME_CORRECTION_FUTURE_ACTION" or "SOME_FUTURE_UNKNOWN_CODE"
                || (item.GetProperty("entityType").GetString() == "WORK_SESSION" && item.GetProperty("actionCode").GetString() == "TIME_CORRECTION_APPROVED")).ToList();
            Assert.Equal(5, forgedRows.Count);
            Assert.All(forgedRows, item =>
            {
                var details = item.GetProperty("details");
                Assert.False(details.TryGetProperty("oldValue", out _));
                Assert.False(details.TryGetProperty("newValue", out _));
                Assert.False(details.TryGetProperty("reason", out _));
            });
            Assert.Equal(
                ["pausedAt"],
                forgedRows.Single(item => item.GetProperty("actionCode").GetString() == "WORK_SESSION_PAUSED").GetProperty("details").EnumerateObject().Select(property => property.Name));

            // The genuine correction events are still there, exactly once each, with their before/after.
            var genuine = items.Where(item => item.GetProperty("entityType").GetString() == "TIME_CORRECTION" && item.GetProperty("actionCode").GetString() != "TIME_CORRECTION_FUTURE_ACTION").ToList();
            Assert.Equal(2, genuine.Count);
            Assert.All(genuine, item => Assert.True(item.GetProperty("details").TryGetProperty("newValue", out _)));
        }
    }

    [Fact]
    public async Task Request_IgnoresEveryClientSuppliedBindingField_BecauseTheContractHasNoSuchMember()
    {
        var world = await WorldAsync();
        var hostile = Guid.NewGuid();

        var response = await RequestAsync(
            world, world.Base.TeamLead, "CHECK_IN", world.At(-5),
            extra: new { oldValue = "2001-01-01T00:00:00Z", status = "APPROVED", requestedBy = hostile, tenantId = hostile, workSessionId = hostile, decidedBy = hostile, slaRecalculated = true });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await EvidenceApiClient.Json(response);
        Assert.Equal("PENDING", body.GetProperty("status").GetString());
        Assert.Equal(world.Base.TeamLead.UserId, body.GetProperty("requestedBy").GetGuid());
        Assert.Equal(world.CheckIn, body.GetProperty("oldValue").GetDateTime().ToUniversalTime(), TimeSpan.FromMilliseconds(1));
        var stored = await _factory.WithDbAsync(db => db.TimeCorrections.AsNoTracking().SingleAsync(item => item.Id == body.GetProperty("timeCorrectionId").GetGuid()));
        Assert.Equal(world.Base.TenantId, stored.TenantId);
        Assert.False(stored.SlaRecalculated);

        Assert.Equal(
            new[] { "NewValue", "Reason", "TimestampType", "WorkSessionPauseId" },
            typeof(RequestTimeCorrectionRequest).GetProperties().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(new[] { "DecisionReason" }, typeof(RejectTimeCorrectionRequest).GetProperties().Select(property => property.Name));
    }

    [Fact]
    public async Task Request_ForTheSameTargetAsAPendingOne_Is409StateConflict_AndOtherTargetsAreFine()
    {
        var world = await WorldAsync();

        Assert.Equal(HttpStatusCode.Created, (await RequestAsync(world, world.Base.TeamLead, "CHECK_IN", world.At(-5))).StatusCode);
        var duplicate = await RequestAsync(world, world.Base.Technician, "CHECK_IN", world.At(-6));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("STATE_CONFLICT", await CodeAsync(duplicate));

        Assert.Equal(HttpStatusCode.Created, (await RequestAsync(world, world.Base.TeamLead, "PAUSE_START", world.At(12), world.Pause1)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await RequestAsync(world, world.Base.TeamLead, "PAUSE_START", world.At(13), world.Pause1)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await RequestAsync(world, world.Base.TeamLead, "PAUSE_START", world.At(32), world.Pause2)).StatusCode);
    }

    // ---------------- decisions ----------------

    [Fact]
    public async Task Approve_AppliesTheExactPauseCorrection_Returns200WithAFreshETag_AndEveryOtherFactStaysPut()
    {
        var world = await WorldAsync();
        var (id, eTag) = await RequestOkAsync(world, world.Base.TeamLead, "RESUME", world.At(22), world.Pause1);

        var response = await DecideAsync("approve", id, world.Base.Supervisor, eTag);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await EvidenceApiClient.Json(response);
        Assert.Equal("APPROVED", body.GetProperty("status").GetString());
        Assert.Equal(world.Base.Supervisor.UserId, body.GetProperty("decidedBy").GetGuid());
        Assert.NotEqual(eTag, response.Headers.ETag!.Tag);
        var pauses = await _factory.WithDbAsync(db => db.WorkSessionPauses.AsNoTracking().Where(item => item.TenantId == world.Base.TenantId).OrderBy(item => item.PausedAt).Select(item => new { item.PausedAt, item.ResumedAt }).ToListAsync());
        Assert.Equal(world.At(22), pauses[0].ResumedAt);
        Assert.Equal(world.At(10), pauses[0].PausedAt);
        Assert.Equal((world.At(30), (DateTime?)world.At(40)), (pauses[1].PausedAt, pauses[1].ResumedAt));
        Assert.Equal(1, await _factory.WithDbAsync(db => db.AuditHistory.CountAsync(audit => audit.EntityId == id && audit.ActionCode == "TIME_CORRECTION_APPROVED")));
    }

    [Fact]
    public async Task Decisions_RequireIfMatch_AndAStaleTokenIs409_AndASecondDecisionIs409()
    {
        var world = await WorldAsync();
        var (id, eTag) = await RequestOkAsync(world, world.Base.TeamLead, "CHECK_IN", world.At(-5));

        var missing = await DecideAsync("approve", id, world.Base.Supervisor, null);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        var stale = await DecideAsync("approve", id, world.Base.Supervisor, "\"AAAAAAAAAAA=\"");
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("CONCURRENCY_CONFLICT", await CodeAsync(stale));

        Assert.Equal(HttpStatusCode.OK, (await DecideAsync("approve", id, world.Base.Supervisor, eTag)).StatusCode);

        var again = await DecideAsync("approve", id, world.Base.Supervisor, eTag);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        var fresh = (await _api.GetAsync($"/api/v1/time-corrections", world.Base.Supervisor)).StatusCode;
        Assert.Equal(HttpStatusCode.OK, fresh);
    }

    [Fact]
    public async Task Reject_NeedsAReason_AndLeavesEveryTimeFactAlone()
    {
        var world = await WorldAsync();
        var (id, eTag) = await RequestOkAsync(world, world.Base.TeamLead, "CHECK_OUT", world.At(55));

        var blank = await DecideAsync("reject", id, world.Base.Supervisor, eTag, new { decisionReason = "  " });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, blank.StatusCode);
        Assert.Contains("decisionReason", (await EvidenceApiClient.Json(blank)).GetProperty("errors").EnumerateObject().Select(property => property.Name));

        var rejected = await DecideAsync("reject", id, world.Base.Supervisor, eTag, new { decisionReason = "Not supported by the visit log." });
        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);
        var body = await EvidenceApiClient.Json(rejected);
        Assert.Equal("REJECTED", body.GetProperty("status").GetString());
        Assert.Equal("Not supported by the visit log.", body.GetProperty("decisionReason").GetString());
        Assert.Equal(
            world.At(60),
            await _factory.WithDbAsync(db => db.WorkSessions.AsNoTracking().Where(item => item.ServiceVisitId == world.Base.VisitId).Select(item => item.CheckOutAt!.Value).SingleAsync()));
    }

    [Fact]
    public async Task Decisions_Authorization_SupervisorOnly_AndScopedToTheirSite_WithTheSameNonLeaking404()
    {
        var world = await WorldAsync();
        var other = await WorldAsync();
        var (id, eTag) = await RequestOkAsync(world, world.Base.TeamLead, "CHECK_IN", world.At(-5));

        Assert.Equal(HttpStatusCode.Unauthorized, (await DecideAsync("approve", id, null, eTag)).StatusCode);
        foreach (var caller in new[] { world.Base.Technician, world.Base.TeamLead, world.Base.Coordinator, world.Base.Approver, world.Base.Administrator, world.Base.Contact })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await DecideAsync("approve", id, caller, eTag)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await DecideAsync("reject", id, caller, eTag, new { decisionReason = "x" })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await _api.GetAsync("/api/v1/time-corrections", caller)).StatusCode);
        }

        var codes = new List<string>();
        foreach (var caller in new[] { world.Base.OtherTenantSupervisor, other.Base.Supervisor })
        {
            var response = await DecideAsync("approve", id, caller, eTag);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            codes.Add(await CodeAsync(response));
        }

        var unknown = await DecideAsync("approve", Guid.NewGuid(), world.Base.Supervisor, eTag);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        codes.Add(await CodeAsync(unknown));
        Assert.Single(codes.Distinct());
    }

    [Fact]
    public async Task ARequesterWhoIsAlsoASupervisor_Gets403OnApproveAndReject_BeforeAnyOtherCheck()
    {
        var world = await WorldAsync();
        var both = await _api.CallerAsync(world.Base.TenantId, [world.Base.SiteA], RoleCodes.TeamLead, RoleCodes.Supervisor);
        var (id, _) = await RequestOkAsync(world, both, "CHECK_IN", world.At(-5));

        var approve = await DecideAsync("approve", id, both, "\"AAAAAAAAAAA=\"");   // a stale token: the 403 is decided first
        var reject = await DecideAsync("reject", id, both, "\"AAAAAAAAAAA=\"", new { decisionReason = "x" });

        Assert.Equal(HttpStatusCode.Forbidden, approve.StatusCode);
        Assert.Equal("ACCESS_DENIED", await CodeAsync(approve));
        Assert.Equal(HttpStatusCode.Forbidden, reject.StatusCode);
        Assert.Equal("PENDING", (await EvidenceApiClient.Json(await _api.GetAsync("/api/v1/time-corrections", world.Base.Supervisor))).GetProperty("items")[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task OnAClosedWorkOrder_RequestAndApproveAre409_AndRejectStillWorks()
    {
        var world = await WorldAsync();
        var (approveId, approveTag) = await RequestOkAsync(world, world.Base.TeamLead, "CHECK_IN", world.At(-5));
        var (rejectId, rejectTag) = await RequestOkAsync(world, world.Base.TeamLead, "CHECK_OUT", world.At(55));
        await _factory.WithDbAsync(db => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [work_order] SET [status] = 'CLOSED' WHERE [work_order_id] = {world.Base.WorkOrderId}"));

        Assert.Equal(HttpStatusCode.Conflict, (await RequestAsync(world, world.Base.TeamLead, "PAUSE_START", world.At(12), world.Pause1)).StatusCode);
        var approve = await DecideAsync("approve", approveId, world.Base.Supervisor, approveTag);
        Assert.Equal(HttpStatusCode.Conflict, approve.StatusCode);
        Assert.Equal("STATE_CONFLICT", await CodeAsync(approve));
        Assert.Equal(HttpStatusCode.OK, (await DecideAsync("reject", rejectId, world.Base.Supervisor, rejectTag, new { decisionReason = "Closed already." })).StatusCode);
    }

    // ---------------- pending list ----------------

    [Fact]
    public async Task PendingList_IsPagedScopedAndOldestFirst_AndShowsOnlyPendingRequestsOfTheSupervisorsSites()
    {
        var world = await WorldAsync();
        var other = await WorldAsync();
        var (first, firstTag) = await RequestOkAsync(world, world.Base.TeamLead, "CHECK_IN", world.At(-5));
        var (second, _) = await RequestOkAsync(world, world.Base.TeamLead, "PAUSE_START", world.At(12), world.Pause1);
        var (third, _) = await RequestOkAsync(world, world.Base.TeamLead, "CHECK_OUT", world.At(55));
        await RequestOkAsync(other, other.Base.TeamLead, "CHECK_IN", other.At(-5));
        Assert.Equal(HttpStatusCode.OK, (await DecideAsync("reject", first, world.Base.Supervisor, firstTag, new { decisionReason = "No." })).StatusCode);

        var page = await EvidenceApiClient.Json(await _api.GetAsync("/api/v1/time-corrections?page=1&pageSize=1", world.Base.Supervisor));

        Assert.Equal(2, page.GetProperty("totalCount").GetInt32());
        Assert.Equal(1, page.GetProperty("items").GetArrayLength());
        Assert.Equal(second, page.GetProperty("items")[0].GetProperty("timeCorrectionId").GetGuid());
        var next = await EvidenceApiClient.Json(await _api.GetAsync("/api/v1/time-corrections?page=2&pageSize=1", world.Base.Supervisor));
        Assert.Equal(third, next.GetProperty("items")[0].GetProperty("timeCorrectionId").GetGuid());
        Assert.Equal(HttpStatusCode.BadRequest, (await _api.GetAsync("/api/v1/time-corrections?page=0", world.Base.Supervisor)).StatusCode);
        Assert.Equal(0, (await EvidenceApiClient.Json(await _api.GetAsync("/api/v1/time-corrections", world.Base.OtherTenantSupervisor))).GetProperty("totalCount").GetInt32());
    }

    // ---------------- Service Report stays live ----------------

    [Fact]
    public async Task TheServiceReport_ShowsTheCorrectedFactsAfterApproval_WithoutAnyReportSnapshot()
    {
        var world = await WorldAsync();
        var before = await EvidenceApiClient.Json(await _api.GetAsync($"/api/v1/work-orders/{world.Base.WorkOrderId}/service-report", world.Base.Supervisor));
        var sessionBefore = before.GetProperty("visits")[0].GetProperty("sessions")[0];
        Assert.Equal(world.CheckIn, sessionBefore.GetProperty("checkInAt").GetDateTime().ToUniversalTime(), TimeSpan.FromMilliseconds(1));

        var (checkIn, checkInTag) = await RequestOkAsync(world, world.Base.TeamLead, "CHECK_IN", world.At(-5));
        var (pause, pauseTag) = await RequestOkAsync(world, world.Base.TeamLead, "PAUSE_START", world.At(12), world.Pause1);
        Assert.Equal(HttpStatusCode.OK, (await DecideAsync("approve", checkIn, world.Base.Supervisor, checkInTag)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await DecideAsync("approve", pause, world.Base.Supervisor, pauseTag)).StatusCode);

        var after = await EvidenceApiClient.Json(await _api.GetAsync($"/api/v1/work-orders/{world.Base.WorkOrderId}/service-report", world.Base.Supervisor));
        var session = after.GetProperty("visits")[0].GetProperty("sessions")[0];
        Assert.Equal(world.At(-5), session.GetProperty("checkInAt").GetDateTime().ToUniversalTime(), TimeSpan.FromMilliseconds(1));
        Assert.Equal(world.At(12), session.GetProperty("pauses")[0].GetProperty("pausedAt").GetDateTime().ToUniversalTime(), TimeSpan.FromMilliseconds(1));
        Assert.Equal(world.At(30), session.GetProperty("pauses")[1].GetProperty("pausedAt").GetDateTime().ToUniversalTime(), TimeSpan.FromMilliseconds(1));
    }
}
