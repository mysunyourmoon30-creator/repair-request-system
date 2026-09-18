using System.Globalization;
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

/// <summary>API host with its own disposable LocalDB database for the S2-003 Service Visit action endpoint tests.</summary>
public sealed class ServiceVisitsApiFactory : AuthApiFactory
{
    public override string ConnectionString =>
        "Server=(localdb)\\MSSQLLocalDB;Database=RepairRequestDb_ServiceVisitsApiTest;Trusted_Connection=True;TrustServerCertificate=True";
}

/// <summary>
/// WO-API-003..007 Service Visit actions end to end (ST-SV-004..009; UC-WO-005..009; S2-003): Reschedule, Reassign,
/// Cancel, Mark Missed and Decide Missed (all four decisions). All Coordinator-only within the Work Order's Site
/// scope; If-Match required and checked against the Visit's own RowVersion; wrong source state is 409; missing
/// reason or an ineligible technician is 422; every action returns the parent Work Order with its full
/// <c>visits</c> array.
/// </summary>
public sealed class ServiceVisitsEndpointsTests : IClassFixture<ServiceVisitsApiFactory>
{
    private const string Requests = "/api/v1/repair-requests";
    private const string Routes = "/api/v1/approval-routes";
    private const string WorkOrders = "/api/v1/work-orders";
    private const string Visits = "/api/v1/service-visits";
    private const string ValidHash = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";

    private readonly ServiceVisitsApiFactory _factory;

    public ServiceVisitsEndpointsTests(ServiceVisitsApiFactory factory)
    {
        _factory = factory;
    }

    private sealed record Caller(Guid UserId, Guid TenantId, string Token);

    private sealed record Scenario(
        Guid TenantId, Site Site, Caller Owner, Caller Approver, Caller Coordinator, Caller Technician,
        Guid WorkOrderId, Guid ServiceVisitId, string VisitETag);

    // ---------------- Reschedule (ST-SV-004/005) ----------------

    [Fact]
    public async Task Reschedule_Success_UpdatesTheWindow_AndStaysScheduled()
    {
        var scenario = await ScheduledVisitAsync();

        var response = await SendAsync($"{Visits}/{scenario.ServiceVisitId}/reschedule", scenario.Coordinator, new
        {
            reason = "Customer requested a later slot",
            scheduledStartAt = "2026-10-02T08:00:00Z",
            scheduledEndAt = "2026-10-02T10:00:00Z"
        }, scenario.VisitETag);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var visit = SingleVisit(await JsonAsync(response));
        Assert.Equal("SCHEDULED", visit.GetProperty("status").GetString());
        Assert.Equal("2026-10-02T08:00:00Z", visit.GetProperty("scheduledStartAt").GetDateTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Reschedule_NonCoordinator_Get403()
    {
        var scenario = await ScheduledVisitAsync();
        var requester = await CallerAsync(scenario.TenantId, [scenario.Site], RoleCodes.Requester);

        await AssertProblemAsync(
            await SendAsync($"{Visits}/{scenario.ServiceVisitId}/reschedule", requester, new { reason = "x", scheduledStartAt = "2026-10-02T08:00:00Z", scheduledEndAt = "2026-10-02T10:00:00Z" }, scenario.VisitETag),
            HttpStatusCode.Forbidden, "ACCESS_DENIED");
    }

    [Fact]
    public async Task Reschedule_MissingReason_Returns422()
    {
        var scenario = await ScheduledVisitAsync();

        var response = await SendAsync($"{Visits}/{scenario.ServiceVisitId}/reschedule", scenario.Coordinator, new
        {
            scheduledStartAt = "2026-10-02T08:00:00Z",
            scheduledEndAt = "2026-10-02T10:00:00Z"
        }, scenario.VisitETag);

        var body = await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
        Assert.True(body.GetProperty("errors").TryGetProperty("reason", out _));
    }

    [Fact]
    public async Task Reschedule_AfterCancel_Returns409StateConflict()
    {
        var scenario = await ScheduledVisitAsync();
        var cancelled = await SendAsync($"{Visits}/{scenario.ServiceVisitId}/cancel", scenario.Coordinator, new { reason = "No longer needed" }, scenario.VisitETag);
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        var cancelledEtag = SingleVisit(await JsonAsync(cancelled)).GetProperty("rowVersion").GetString();

        await AssertProblemAsync(
            await SendAsync($"{Visits}/{scenario.ServiceVisitId}/reschedule", scenario.Coordinator, new { reason = "x", scheduledStartAt = "2026-10-02T08:00:00Z", scheduledEndAt = "2026-10-02T10:00:00Z" }, $"\"{cancelledEtag}\""),
            HttpStatusCode.Conflict, "STATE_CONFLICT");
    }

    // ---------------- Reassign (ST-SV-006) ----------------

    [Fact]
    public async Task Reassign_Success_UpdatesTeamAndTechnician_AndStaysScheduled()
    {
        var scenario = await ScheduledVisitAsync();
        var newTechnician = await CallerAsync(scenario.TenantId, [scenario.Site], RoleCodes.Technician);
        var newTeamId = Guid.NewGuid();

        var response = await SendAsync($"{Visits}/{scenario.ServiceVisitId}/reassign", scenario.Coordinator, new
        {
            reason = "Original technician is unavailable",
            assignedTeamId = newTeamId,
            assignedTechnicianId = newTechnician.UserId
        }, scenario.VisitETag);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var visit = SingleVisit(await JsonAsync(response));
        Assert.Equal("SCHEDULED", visit.GetProperty("status").GetString());
        Assert.Equal(newTeamId, visit.GetProperty("assignedTeamId").GetGuid());
        Assert.Equal(newTechnician.UserId, visit.GetProperty("assignedTechnicianId").GetGuid());
    }

    [Fact]
    public async Task Reassign_IneligibleTechnician_Returns422()
    {
        var scenario = await ScheduledVisitAsync();

        var response = await SendAsync($"{Visits}/{scenario.ServiceVisitId}/reassign", scenario.Coordinator, new
        {
            reason = "reassign",
            assignedTeamId = Guid.NewGuid(),
            assignedTechnicianId = scenario.Owner.UserId
        }, scenario.VisitETag);

        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
    }

    // ---------------- Cancel (ST-SV-007) ----------------

    [Fact]
    public async Task Cancel_Success_MovesToCancelled_AndWorkOrderStaysScheduled()
    {
        var scenario = await ScheduledVisitAsync();

        var response = await SendAsync($"{Visits}/{scenario.ServiceVisitId}/cancel", scenario.Coordinator, new { reason = "No longer needed" }, scenario.VisitETag);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal("SCHEDULED", body.GetProperty("status").GetString());
        Assert.Equal("CANCELLED", SingleVisit(body).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Cancel_AlreadyCancelled_Returns409StateConflict()
    {
        var scenario = await ScheduledVisitAsync();
        var first = await SendAsync($"{Visits}/{scenario.ServiceVisitId}/cancel", scenario.Coordinator, new { reason = "No longer needed" }, scenario.VisitETag);
        var etag = $"\"{SingleVisit(await JsonAsync(first)).GetProperty("rowVersion").GetString()}\"";

        await AssertProblemAsync(
            await SendAsync($"{Visits}/{scenario.ServiceVisitId}/cancel", scenario.Coordinator, new { reason = "again" }, etag),
            HttpStatusCode.Conflict, "STATE_CONFLICT");
    }

    // ---------------- Mark Missed (ST-SV-008) ----------------

    [Fact]
    public async Task MarkMissed_Success_MovesToMissed()
    {
        var scenario = await ScheduledVisitAsync();

        var response = await SendAsync($"{Visits}/{scenario.ServiceVisitId}/mark-missed", scenario.Coordinator, new { reason = "Technician could not reach the site" }, scenario.VisitETag);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("MISSED", SingleVisit(await JsonAsync(response)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task MarkMissed_MissingReason_Returns422()
    {
        var scenario = await ScheduledVisitAsync();

        var body = await AssertProblemAsync(
            await SendAsync($"{Visits}/{scenario.ServiceVisitId}/mark-missed", scenario.Coordinator, new { }, scenario.VisitETag),
            HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
        Assert.True(body.GetProperty("errors").TryGetProperty("reason", out _));
    }

    // ---------------- Decide Missed (ST-SV-009/D-15) — all four decisions ----------------

    [Theory]
    [InlineData("RESCHEDULE")]
    [InlineData("FOLLOW_UP")]
    [InlineData("REASSIGN")]
    public async Task DecideMissed_WithAFollowUpDecision_CreatesANewScheduledVisit_ReferencingTheOriginal(string decision)
    {
        var scenario = await MissedVisitAsync();

        var response = await SendAsync($"{Visits}/{scenario.ServiceVisitId}/missed-decision", scenario.Coordinator, new
        {
            decision,
            reason = "Rescheduling the missed work",
            newSchedule = new
            {
                assignedTeamId = Guid.NewGuid(),
                assignedTechnicianId = scenario.Technician.UserId,
                scheduledStartAt = "2026-10-05T08:00:00Z",
                scheduledEndAt = "2026-10-05T10:00:00Z"
            }
        }, scenario.VisitETag);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var visits = (await JsonAsync(response)).GetProperty("visits").EnumerateArray().ToList();
        Assert.Equal(2, visits.Count);

        var original = visits.Single(v => v.GetProperty("serviceVisitId").GetGuid() == scenario.ServiceVisitId);
        Assert.Equal("MISSED", original.GetProperty("status").GetString());
        Assert.Equal(decision, original.GetProperty("missedDecisionCode").GetString());

        var followUp = visits.Single(v => v.GetProperty("serviceVisitId").GetGuid() != scenario.ServiceVisitId);
        Assert.Equal("SCHEDULED", followUp.GetProperty("status").GetString());
        Assert.Equal("FOLLOW_UP", followUp.GetProperty("visitType").GetString());
        Assert.Equal(scenario.ServiceVisitId, followUp.GetProperty("sourceMissedVisitId").GetGuid());
    }

    [Fact]
    public async Task DecideMissed_NoFollowUp_CreatesNoNewVisit_AndWorkOrderContinues()
    {
        var scenario = await MissedVisitAsync();

        var response = await SendAsync($"{Visits}/{scenario.ServiceVisitId}/missed-decision", scenario.Coordinator, new
        {
            decision = "NO_FOLLOW_UP",
            reason = "No further action needed"
        }, scenario.VisitETag);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal("SCHEDULED", body.GetProperty("status").GetString());
        var visit = SingleVisit(body);
        Assert.Equal("MISSED", visit.GetProperty("status").GetString());
        Assert.Equal("NO_FOLLOW_UP", visit.GetProperty("missedDecisionCode").GetString());
    }

    [Fact]
    public async Task DecideMissed_AlreadyDecided_Returns409StateConflict_AndDoesNotCreateASecondFollowUp()
    {
        var scenario = await MissedVisitAsync();
        var first = await SendAsync($"{Visits}/{scenario.ServiceVisitId}/missed-decision", scenario.Coordinator, new { decision = "NO_FOLLOW_UP", reason = "done" }, scenario.VisitETag);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var etag = $"\"{SingleVisit(await JsonAsync(first)).GetProperty("rowVersion").GetString()}\"";

        await AssertProblemAsync(
            await SendAsync($"{Visits}/{scenario.ServiceVisitId}/missed-decision", scenario.Coordinator, new { decision = "RESCHEDULE", reason = "again", newSchedule = new { assignedTeamId = Guid.NewGuid(), assignedTechnicianId = scenario.Technician.UserId, scheduledStartAt = "2026-10-06T08:00:00Z", scheduledEndAt = "2026-10-06T10:00:00Z" } }, etag),
            HttpStatusCode.Conflict, "STATE_CONFLICT");

        Assert.Equal(1, await WithDbAsync(db => db.ServiceVisits.CountAsync(v => v.WorkOrderId == scenario.WorkOrderId)));
    }

    [Fact]
    public async Task DecideMissed_OnAScheduledVisit_Returns409StateConflict()
    {
        var scenario = await ScheduledVisitAsync();

        await AssertProblemAsync(
            await SendAsync($"{Visits}/{scenario.ServiceVisitId}/missed-decision", scenario.Coordinator, new { decision = "NO_FOLLOW_UP", reason = "x" }, scenario.VisitETag),
            HttpStatusCode.Conflict, "STATE_CONFLICT");
    }

    [Fact]
    public async Task DecideMissed_RescheduleWithoutNewSchedule_Returns422()
    {
        var scenario = await MissedVisitAsync();

        var body = await AssertProblemAsync(
            await SendAsync($"{Visits}/{scenario.ServiceVisitId}/missed-decision", scenario.Coordinator, new { decision = "RESCHEDULE", reason = "x" }, scenario.VisitETag),
            HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
        Assert.True(body.GetProperty("errors").TryGetProperty("newSchedule", out _));
    }

    // ---------------- Concurrency ----------------

    [Fact]
    public async Task TwoConcurrentCancels_OfTheSameVisit_ExactlyOneSucceeds()
    {
        var scenario = await ScheduledVisitAsync();

        var responses = await Task.WhenAll(
            Task.Run(() => SendAsync($"{Visits}/{scenario.ServiceVisitId}/cancel", scenario.Coordinator, new { reason = "a" }, scenario.VisitETag)),
            Task.Run(() => SendAsync($"{Visits}/{scenario.ServiceVisitId}/cancel", scenario.Coordinator, new { reason = "b" }, scenario.VisitETag)));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal(
            ServiceVisitStatus.Cancelled,
            await WithDbAsync(db => db.ServiceVisits.AsNoTracking().Where(v => v.Id == scenario.ServiceVisitId).Select(v => v.Status).SingleAsync()));
    }

    // ---------------- Scope ----------------

    [Fact]
    public async Task CoordinatorOutOfSiteScope_Get404()
    {
        var scenario = await ScheduledVisitAsync();
        var outsider = await CallerAsync(scenario.TenantId, [await SiteAsync(scenario.TenantId)], RoleCodes.Coordinator);

        await AssertProblemAsync(
            await SendAsync($"{Visits}/{scenario.ServiceVisitId}/cancel", outsider, new { reason = "x" }, scenario.VisitETag),
            HttpStatusCode.NotFound, "NOT_FOUND");
    }

    // ---------------- Helpers ----------------

    private static JsonElement SingleVisit(JsonElement workOrderResponse) =>
        Assert.Single(workOrderResponse.GetProperty("visits").EnumerateArray());

    private async Task<Scenario> MissedVisitAsync()
    {
        var scenario = await ScheduledVisitAsync();
        var missed = await SendAsync($"{Visits}/{scenario.ServiceVisitId}/mark-missed", scenario.Coordinator, new { reason = "Technician could not reach the site" }, scenario.VisitETag);
        Assert.Equal(HttpStatusCode.OK, missed.StatusCode);
        var visit = SingleVisit(await JsonAsync(missed));
        return scenario with { VisitETag = $"\"{visit.GetProperty("rowVersion").GetString()}\"" };
    }

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
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(Routes, admin, new { requestCategoryCode = "ELECTRICAL", siteId = site.Id, approverRoleCode = "APPROVER" }, null)).StatusCode);

        var (id, draftETag) = await DraftAsync(owner, site);
        await WithDbAsync(async db =>
        {
            var file = new FileAsset(tenantId, "evidence.png", "image/png", 1024, ValidHash, $"{tenantId:N}/{Guid.NewGuid():N}", owner.UserId, DateTime.UtcNow);
            file.MarkClean();
            db.FileAssets.Add(file);
            db.RepairRequestAttachments.Add(new RepairRequestAttachment(id, file.Id, AttachmentFileRules.AccessScopeCode));
            await db.SaveChangesAsync();
        });

        var submitted = await SendAsync($"{Requests}/{id}/submit", owner, new { }, draftETag);
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);

        var approved = await SendAsync($"{Requests}/{id}/approve", approver, null, submitted.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);

        var converted = await SendAsync($"{Requests}/{id}/convert-to-work-order", coordinator, null, approved.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.OK, converted.StatusCode);
        var workOrderId = (await JsonAsync(converted)).GetProperty("workOrderId").GetGuid();

        var scheduled = await SendAsync($"{WorkOrders}/{workOrderId}/schedule", coordinator, new
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
        var created = await SendAsync(Requests, owner, new
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

    private async Task<HttpResponseMessage> SendAsync(string url, Caller? caller, object? body, string? ifMatch)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = body is null ? null : JsonContent.Create(body) };
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
