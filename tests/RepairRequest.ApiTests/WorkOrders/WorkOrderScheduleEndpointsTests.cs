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

/// <summary>API host with its own disposable LocalDB database for the S2-003 Schedule endpoint tests.</summary>
public sealed class WorkOrderScheduleApiFactory : AuthApiFactory
{
    public override string ConnectionString =>
        "Server=(localdb)\\MSSQLLocalDB;Database=RepairRequestDb_WorkOrderScheduleApiTest;Trusted_Connection=True;TrustServerCertificate=True";
}

/// <summary>
/// RR-API-002 (WO-API-002) Schedule end to end (ST-WO-001; UC-WO-003; S2-003): only a Coordinator within the Work
/// Order's Site scope schedules an OPEN Work Order; other roles are 403; a non-OPEN source is 409; a missing team,
/// missing/invalid window or ineligible technician is 422; success creates exactly one SCHEDULED Service Visit and
/// writes one audit record; a concurrent race allows exactly one winner.
/// </summary>
public sealed class WorkOrderScheduleEndpointsTests : IClassFixture<WorkOrderScheduleApiFactory>
{
    private const string Requests = "/api/v1/repair-requests";
    private const string Routes = "/api/v1/approval-routes";
    private const string WorkOrders = "/api/v1/work-orders";
    private const string ValidHash = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";

    private readonly WorkOrderScheduleApiFactory _factory;

    public WorkOrderScheduleEndpointsTests(WorkOrderScheduleApiFactory factory)
    {
        _factory = factory;
    }

    private sealed record Caller(Guid UserId, Guid TenantId, string Token);

    private sealed record Scenario(Guid TenantId, Site Site, Caller Owner, Caller Approver, Caller Coordinator, Caller Technician, Guid WorkOrderId, string ETag);

    private static string Schedule(Guid id) => $"{WorkOrders}/{id}/schedule";

    // ---------------- Success ----------------

    [Fact]
    public async Task Coordinator_SchedulesAnOpenWorkOrder_Returns200_CreatesOneScheduledVisit_SetsOwnerTeam_AndWritesOneAudit()
    {
        var scenario = await OpenWorkOrderAsync();
        var teamId = Guid.NewGuid();
        var start = "2026-10-01T08:00:00Z";
        var end = "2026-10-01T10:00:00Z";

        var response = await SendAsync(Schedule(scenario.WorkOrderId), scenario.Coordinator, new
        {
            ownerTeamId = teamId,
            assignedTechnicianId = scenario.Technician.UserId,
            scheduledStartAt = start,
            scheduledEndAt = end
        }, scenario.ETag);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal("SCHEDULED", body.GetProperty("status").GetString());
        var visits = body.GetProperty("visits").EnumerateArray().ToList();
        var visit = Assert.Single(visits);
        Assert.Equal("SCHEDULED", visit.GetProperty("status").GetString());
        Assert.Equal("INITIAL", visit.GetProperty("visitType").GetString());
        Assert.Equal(teamId, visit.GetProperty("assignedTeamId").GetGuid());
        Assert.Equal(scenario.Technician.UserId, visit.GetProperty("assignedTechnicianId").GetGuid());

        var storedWorkOrder = await WithDbAsync(db => db.WorkOrders.AsNoTracking().SingleAsync(wo => wo.Id == scenario.WorkOrderId));
        Assert.Equal(WorkOrderStatus.Scheduled, storedWorkOrder.Status);
        Assert.Equal(teamId, storedWorkOrder.OwnerTeamId);

        Assert.Equal(1, await WithDbAsync(db => db.ServiceVisits.CountAsync(v => v.WorkOrderId == scenario.WorkOrderId)));
        Assert.Equal(1, await WithDbAsync(db => db.AuditHistory.CountAsync(a => a.EntityId == scenario.WorkOrderId && a.ActionCode == "WORK_ORDER_SCHEDULED")));
    }

    // ---------------- Authorization ----------------

    [Theory]
    [InlineData(RoleCodes.Requester)]
    [InlineData(RoleCodes.Approver)]
    [InlineData(RoleCodes.Technician)]
    [InlineData(RoleCodes.TeamLead)]
    [InlineData(RoleCodes.Supervisor)]
    [InlineData(RoleCodes.Administrator)]
    public async Task RolesWithoutCoordinator_Get403(string role)
    {
        var scenario = await OpenWorkOrderAsync();
        var caller = await CallerAsync(scenario.TenantId, [scenario.Site], role);

        await AssertProblemAsync(await ScheduleAsync(scenario, caller), HttpStatusCode.Forbidden, "ACCESS_DENIED");
        await AssertUnchangedAsync(scenario.WorkOrderId);
    }

    [Fact]
    public async Task CoordinatorOutOfSiteScope_AndAnotherTenant_Get404()
    {
        var scenario = await OpenWorkOrderAsync();
        var otherSiteCoordinator = await CallerAsync(scenario.TenantId, [await SiteAsync(scenario.TenantId)], RoleCodes.Coordinator);
        var otherTenantId = Guid.NewGuid();
        var foreign = await CallerAsync(otherTenantId, [await SiteAsync(otherTenantId)], RoleCodes.Coordinator);

        foreach (var caller in new[] { otherSiteCoordinator, foreign })
        {
            await AssertProblemAsync(await ScheduleAsync(scenario, caller), HttpStatusCode.NotFound, "NOT_FOUND");
        }

        await AssertUnchangedAsync(scenario.WorkOrderId);
    }

    // ---------------- State ----------------

    [Fact]
    public async Task AlreadyScheduled_Returns409StateConflict_AndDoesNotCreateASecondVisit()
    {
        var scenario = await OpenWorkOrderAsync();
        var first = await ScheduleAsync(scenario, scenario.Coordinator);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var freshWorkOrder = await SendGetAsync($"{WorkOrders}/{scenario.WorkOrderId}", scenario.Coordinator);
        await AssertProblemAsync(
            await SendAsync(Schedule(scenario.WorkOrderId), scenario.Coordinator, DefaultBody(scenario), freshWorkOrder.Headers.ETag!.Tag),
            HttpStatusCode.Conflict,
            "STATE_CONFLICT");

        Assert.Equal(1, await WithDbAsync(db => db.ServiceVisits.CountAsync(v => v.WorkOrderId == scenario.WorkOrderId)));
    }

    // ---------------- Validation ----------------

    [Fact]
    public async Task MissingTeam_Returns422()
    {
        var scenario = await OpenWorkOrderAsync();

        var response = await SendAsync(Schedule(scenario.WorkOrderId), scenario.Coordinator, new
        {
            assignedTechnicianId = scenario.Technician.UserId,
            scheduledStartAt = "2026-10-01T08:00:00Z",
            scheduledEndAt = "2026-10-01T10:00:00Z"
        }, scenario.ETag);

        var body = await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
        Assert.True(body.GetProperty("errors").TryGetProperty("ownerTeamId", out _));
        await AssertUnchangedAsync(scenario.WorkOrderId);
    }

    [Fact]
    public async Task EndBeforeStart_Returns422()
    {
        var scenario = await OpenWorkOrderAsync();

        var response = await SendAsync(Schedule(scenario.WorkOrderId), scenario.Coordinator, new
        {
            ownerTeamId = Guid.NewGuid(),
            assignedTechnicianId = scenario.Technician.UserId,
            scheduledStartAt = "2026-10-01T10:00:00Z",
            scheduledEndAt = "2026-10-01T08:00:00Z"
        }, scenario.ETag);

        var body = await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
        Assert.True(body.GetProperty("errors").TryGetProperty("scheduledEndAt", out _));
        await AssertUnchangedAsync(scenario.WorkOrderId);
    }

    [Fact]
    public async Task TechnicianOutsideTheWorkOrdersSite_Returns422()
    {
        var scenario = await OpenWorkOrderAsync();
        var outsideSite = await SiteAsync(scenario.TenantId);
        var outsideTechnician = await CallerAsync(scenario.TenantId, [outsideSite], RoleCodes.Technician);

        var response = await SendAsync(Schedule(scenario.WorkOrderId), scenario.Coordinator, new
        {
            ownerTeamId = Guid.NewGuid(),
            assignedTechnicianId = outsideTechnician.UserId,
            scheduledStartAt = "2026-10-01T08:00:00Z",
            scheduledEndAt = "2026-10-01T10:00:00Z"
        }, scenario.ETag);

        var body = await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
        Assert.True(body.GetProperty("errors").TryGetProperty("assignedTechnicianId", out _));
        await AssertUnchangedAsync(scenario.WorkOrderId);
    }

    [Fact]
    public async Task NonTechnicianAssignee_Returns422()
    {
        var scenario = await OpenWorkOrderAsync();

        var response = await SendAsync(Schedule(scenario.WorkOrderId), scenario.Coordinator, new
        {
            ownerTeamId = Guid.NewGuid(),
            assignedTechnicianId = scenario.Owner.UserId,
            scheduledStartAt = "2026-10-01T08:00:00Z",
            scheduledEndAt = "2026-10-01T10:00:00Z"
        }, scenario.ETag);

        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
        await AssertUnchangedAsync(scenario.WorkOrderId);
    }

    // ---------------- Concurrency ----------------

    [Fact]
    public async Task TwoConcurrentSchedules_OfTheSameWorkOrder_ExactlyOneSucceeds_WithOneVisit()
    {
        var scenario = await OpenWorkOrderAsync();

        var responses = await Task.WhenAll(
            Task.Run(() => ScheduleAsync(scenario, scenario.Coordinator)),
            Task.Run(() => ScheduleAsync(scenario, scenario.Coordinator)));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);

        Assert.Equal(1, await WithDbAsync(db => db.ServiceVisits.CountAsync(v => v.WorkOrderId == scenario.WorkOrderId)));
        Assert.Equal(
            WorkOrderStatus.Scheduled,
            await WithDbAsync(db => db.WorkOrders.AsNoTracking().Where(wo => wo.Id == scenario.WorkOrderId).Select(wo => wo.Status).SingleAsync()));
    }

    // ---------------- Helpers ----------------

    private static object DefaultBody(Scenario scenario) => new
    {
        ownerTeamId = Guid.NewGuid(),
        assignedTechnicianId = scenario.Technician.UserId,
        scheduledStartAt = "2026-10-01T08:00:00Z",
        scheduledEndAt = "2026-10-01T10:00:00Z"
    };

    private Task<HttpResponseMessage> ScheduleAsync(Scenario scenario, Caller caller) =>
        SendAsync(Schedule(scenario.WorkOrderId), caller, DefaultBody(scenario), scenario.ETag);

    private async Task AssertUnchangedAsync(Guid workOrderId)
    {
        var stored = await WithDbAsync(db => db.WorkOrders.AsNoTracking().SingleAsync(wo => wo.Id == workOrderId));
        Assert.Equal(WorkOrderStatus.Open, stored.Status);
        Assert.Null(stored.OwnerTeamId);
        Assert.Equal(0, await WithDbAsync(db => db.ServiceVisits.CountAsync(v => v.WorkOrderId == workOrderId)));
    }

    /// <summary>Builds a real OPEN Work Order through the full real flow: Draft -&gt; Submit -&gt; Approve -&gt; Convert.</summary>
    private async Task<Scenario> OpenWorkOrderAsync()
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
        var workOrder = await JsonAsync(converted);

        return new Scenario(tenantId, site, owner, approver, coordinator, technician, workOrder.GetProperty("workOrderId").GetGuid(), converted.Headers.ETag!.Tag);
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

    private async Task<HttpResponseMessage> SendGetAsync(string url, Caller caller)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", caller.Token);
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
