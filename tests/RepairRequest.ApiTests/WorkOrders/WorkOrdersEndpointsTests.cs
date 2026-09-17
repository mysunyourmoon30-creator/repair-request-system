using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RepairRequest.Domain.MasterData;
using RepairRequest.Domain.Security;
using RepairRequest.Domain.WorkOrders;
using RepairRequest.Infrastructure.Identity;
using RepairRequest.Infrastructure.Persistence;
using RepairRequestAggregate = RepairRequest.Domain.RepairRequests.RepairRequest;

namespace RepairRequest.ApiTests.WorkOrders;

/// <summary>API host with its own disposable LocalDB database for the Work Order List/Detail endpoint tests.</summary>
public sealed class WorkOrderApiFactory : AuthApiFactory
{
    public override string ConnectionString =>
        "Server=(localdb)\\MSSQLLocalDB;Database=RepairRequestDb_WorkOrderApiTest;Trusted_Connection=True;TrustServerCertificate=True";
}

/// <summary>
/// Work Order List/Detail endpoints end to end (S2-001; DEC-S2-001-01..05, `docs/12` Section 11): WorkOrder.Read
/// scope mirrors Repair Request (site-wide vs. own data), TECHNICIAN is denied outright, status filter and fixed
/// newest-first sort, and the response never carries Scheduled Date, Technician, Team or Team Lead.
/// </summary>
public sealed class WorkOrdersEndpointsTests : IClassFixture<WorkOrderApiFactory>
{
    private const string Collection = "/api/v1/work-orders";

    private readonly WorkOrderApiFactory _factory;

    public WorkOrdersEndpointsTests(WorkOrderApiFactory factory)
    {
        _factory = factory;
    }

    private sealed record Caller(Guid UserId, Guid TenantId, string Token);

    private sealed record World(Guid TenantId, Site Site, Equipment Equipment, Site OtherSite);

    public static TheoryData<string> RolesDeniedWorkOrderRead =>
    [
        RoleCodes.Technician,
        RoleCodes.Administrator
    ];

    // ---------------- List ----------------

    [Fact]
    public async Task List_FiltersByStatus_SortsNewestFirst_AndOmitsUnresolvedColumns()
    {
        var world = await WorldAsync();
        var requester = await CallerAsync(world.TenantId, [world.Site], RoleCodes.Requester);
        var coordinator = await CallerAsync(world.TenantId, [world.Site], RoleCodes.Coordinator);
        var baseTime = new DateTime(2026, 9, 17, 8, 0, 0, DateTimeKind.Utc);

        var oldest = await SeedWorkOrderAsync(world, requester.UserId, "WO-1", WorkOrderStatus.Open, baseTime, "RR-2026-000001");
        var middle = await SeedWorkOrderAsync(world, requester.UserId, "WO-2", WorkOrderStatus.Closed, baseTime.AddMinutes(1), "RR-2026-000002");
        var newest = await SeedWorkOrderAsync(world, requester.UserId, "WO-3", WorkOrderStatus.Open, baseTime.AddMinutes(2), "RR-2026-000003");

        var all = await JsonAsync(await SendAsync(HttpMethod.Get, $"{Collection}?pageSize=100", coordinator));
        var openOnly = await JsonAsync(await SendAsync(HttpMethod.Get, $"{Collection}?status=OPEN&pageSize=100", coordinator));

        Assert.Equal(3, all.GetProperty("totalCount").GetInt32());
        Assert.Equal(
            new[] { newest.WorkOrderId, middle.WorkOrderId, oldest.WorkOrderId },
            all.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("workOrderId").GetGuid()));

        Assert.Equal(2, openOnly.GetProperty("totalCount").GetInt32());
        Assert.Equal(
            new[] { newest.WorkOrderId, oldest.WorkOrderId },
            openOnly.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("workOrderId").GetGuid()));

        var first = openOnly.GetProperty("items").EnumerateArray().First();
        Assert.Equal("WO-3", first.GetProperty("workOrderNo").GetString());
        Assert.Equal("OPEN", first.GetProperty("status").GetString());
        Assert.Equal("RR-2026-000003", first.GetProperty("repairRequestNo").GetString());
        Assert.Equal("CUST-1", first.GetProperty("customerCode").GetString());
        Assert.Equal("SITE-A", first.GetProperty("siteCode").GetString());
        Assert.Equal("EQ-A1", first.GetProperty("equipmentCode").GetString());

        // DEC-S2-001-02/03: no Scheduled Date / Technician / Team / Team Lead field is ever returned.
        Assert.False(first.TryGetProperty("scheduledAt", out _));
        Assert.False(first.TryGetProperty("technician", out _));
        Assert.False(first.TryGetProperty("assignedTeam", out _));
        Assert.False(first.TryGetProperty("teamLead", out _));
    }

    [Fact]
    public async Task List_InvalidStatus_Returns400()
    {
        var world = await WorldAsync();
        var coordinator = await CallerAsync(world.TenantId, [world.Site], RoleCodes.Coordinator);

        var response = await SendAsync(HttpMethod.Get, $"{Collection}?status=NOT_A_STATUS", coordinator);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "BAD_REQUEST");
    }

    [Fact]
    public async Task List_OtherTenantWorkOrders_AreNeverReturned()
    {
        var world = await WorldAsync();
        var otherWorld = await WorldAsync();
        var requester = await CallerAsync(world.TenantId, [world.Site], RoleCodes.Requester);
        var otherRequester = await CallerAsync(otherWorld.TenantId, [otherWorld.Site], RoleCodes.Requester);
        await SeedWorkOrderAsync(otherWorld, otherRequester.UserId, "WO-FOREIGN", WorkOrderStatus.Open, DateTime.UtcNow, "RR-FOREIGN-1");
        var coordinator = await CallerAsync(world.TenantId, [world.Site], RoleCodes.Coordinator);

        var response = await JsonAsync(await SendAsync(HttpMethod.Get, $"{Collection}?pageSize=100", coordinator));

        Assert.Equal(0, response.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task List_AuthorizedCaller_WithNoWorkOrdersAtAll_Returns200WithEmptyItems()
    {
        // No WorkOrder is ever seeded in this world: the caller is authorized (WorkOrder.Read + site scope),
        // but nothing exists yet to see. This must be an ordinary empty page, not an error.
        var world = await WorldAsync();
        var coordinator = await CallerAsync(world.TenantId, [world.Site], RoleCodes.Coordinator);

        var httpResponse = await SendAsync(HttpMethod.Get, Collection, coordinator);
        var response = await JsonAsync(httpResponse);

        Assert.Equal(HttpStatusCode.OK, httpResponse.StatusCode);
        Assert.Equal(0, response.GetProperty("totalCount").GetInt32());
        Assert.Empty(response.GetProperty("items").EnumerateArray());
        Assert.Equal(1, response.GetProperty("page").GetInt32());
    }

    [Theory]
    [MemberData(nameof(RolesDeniedWorkOrderRead))]
    public async Task List_RoleWithoutWorkOrderReadPolicy_Returns403(string roleCode)
    {
        var world = await WorldAsync();
        var caller = await CallerAsync(world.TenantId, [world.Site], roleCode);

        var response = await SendAsync(HttpMethod.Get, Collection, caller);

        await AssertProblemAsync(response, HttpStatusCode.Forbidden, "ACCESS_DENIED");
    }

    // ---------------- Detail ----------------

    [Fact]
    public async Task Get_FollowsRepairRequestDerivedScope_AndOutOfScopeOrMissingReturns404Identically()
    {
        var world = await WorldAsync();
        var requester = await CallerAsync(world.TenantId, [world.Site], RoleCodes.Requester);
        var otherRequester = await CallerAsync(world.TenantId, [world.Site], RoleCodes.Requester);
        var coordinatorAtSite = await CallerAsync(world.TenantId, [world.Site], RoleCodes.Coordinator);
        var coordinatorElsewhere = await CallerAsync(world.TenantId, [world.OtherSite], RoleCodes.Coordinator);
        var seeded = await SeedWorkOrderAsync(world, requester.UserId, "WO-1", WorkOrderStatus.Open, DateTime.UtcNow, "RR-2026-000001");
        var url = $"{Collection}/{seeded.WorkOrderId}";

        var ownerView = await SendAsync(HttpMethod.Get, url, requester);
        var coordinatorView = await SendAsync(HttpMethod.Get, url, coordinatorAtSite);
        var random = await SendAsync(HttpMethod.Get, $"{Collection}/{Guid.NewGuid()}", requester);

        Assert.Equal(HttpStatusCode.OK, ownerView.StatusCode);
        Assert.Equal("WO-1", (await JsonAsync(ownerView)).GetProperty("workOrderNo").GetString());
        Assert.Equal(HttpStatusCode.OK, coordinatorView.StatusCode);

        var expectedNotFound = await NotFoundShapeAsync(random);
        Assert.Equal(expectedNotFound, await NotFoundShapeAsync(await SendAsync(HttpMethod.Get, url, otherRequester)));
        Assert.Equal(expectedNotFound, await NotFoundShapeAsync(await SendAsync(HttpMethod.Get, url, coordinatorElsewhere)));
    }

    [Fact]
    public async Task Get_TechnicianCaller_Returns403_NotFound()
    {
        var world = await WorldAsync();
        var requester = await CallerAsync(world.TenantId, [world.Site], RoleCodes.Requester);
        var technician = await CallerAsync(world.TenantId, [world.Site], RoleCodes.Technician);
        var seeded = await SeedWorkOrderAsync(world, requester.UserId, "WO-1", WorkOrderStatus.Open, DateTime.UtcNow, "RR-2026-000001");

        var response = await SendAsync(HttpMethod.Get, $"{Collection}/{seeded.WorkOrderId}", technician);

        await AssertProblemAsync(response, HttpStatusCode.Forbidden, "ACCESS_DENIED");
    }

    // ---------------- Helpers ----------------

    private HttpClient CreateClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

    private async Task<World> WorldAsync()
    {
        var tenantId = Guid.NewGuid();

        return await WithDbAsync(async db =>
        {
            var customer = new Customer(tenantId, "CUST-1");
            db.Customers.Add(customer);

            var site = new Site(tenantId, customer.Id, "SITE-A");
            var otherSite = new Site(tenantId, customer.Id, "SITE-B");
            db.Sites.AddRange(site, otherSite);

            var equipment = new Equipment(tenantId, site.Id, "EQ-A1");
            db.Equipment.Add(equipment);

            await db.SaveChangesAsync();
            return new World(tenantId, site, equipment, otherSite);
        });
    }

    private sealed record SeededWorkOrder(Guid WorkOrderId, Guid RepairRequestId);

    private async Task<SeededWorkOrder> SeedWorkOrderAsync(
        World world, Guid requesterId, string workOrderNo, WorkOrderStatus status, DateTime createdAt, string requestNo) =>
        await WithDbAsync(async db =>
        {
            var request = RepairRequestAggregate.CreateDraft(world.TenantId, requesterId);
            request.EditDraft(world.Site.Id, world.Equipment.Id, null, null, null, null, null, null);
            var requestEntry = db.RepairRequests.Add(request);
            requestEntry.Property(r => r.RequestNo).CurrentValue = requestNo;

            var workOrder = WorkOrder.Create(world.TenantId, request.Id, workOrderNo, createdAt);
            var workOrderEntry = db.WorkOrders.Add(workOrder);
            if (status != WorkOrderStatus.Open)
            {
                workOrderEntry.Property(w => w.Status).CurrentValue = status;
            }

            await db.SaveChangesAsync();
            return new SeededWorkOrder(workOrder.Id, request.Id);
        });

    private async Task<Caller> CallerAsync(Guid tenantId, Site[] assignedSites, string roleCode)
    {
        var user = await _factory.CreateUserInTenantAsync(tenantId, roleCode);

        if (assignedSites.Length > 0)
        {
            await WithDbAsync(db =>
            {
                foreach (var site in assignedSites)
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

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, Caller? caller)
    {
        var request = new HttpRequestMessage(method, url);

        if (caller is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", caller.Token);
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
        return body;
    }

    private static async Task<string> NotFoundShapeAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        Assert.Equal("NOT_FOUND", (string?)node["code"]);
        node.Remove("correlationId");
        node.Remove("traceId");
        return node.ToJsonString();
    }

    private async Task<T> WithDbAsync<T>(Func<RepairRequestDbContext, Task<T>> action)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<RepairRequestDbContext>());
    }
}
