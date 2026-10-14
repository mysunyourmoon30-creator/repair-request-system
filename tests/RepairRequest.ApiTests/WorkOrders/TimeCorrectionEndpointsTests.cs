using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RepairRequest.Api.Contracts.WorkOrders;
using RepairRequest.ApiTests.Approvals;
using RepairRequest.ApiTests.WorkEvidence;
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
