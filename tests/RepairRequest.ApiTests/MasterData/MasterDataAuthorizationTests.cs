using System.Net;
using RepairRequest.Domain.Security;

namespace RepairRequest.ApiTests.MasterData;

/// <summary>
/// Master-data authorization through the S1-003 foundation: 401 without a principal, 403 for business roles on every
/// configuration command (checked before any lookup), ADMINISTRATOR limited to its own tenant, and business-role reads
/// limited to assigned Sites (RR-REQ-001 section 3; S1-003 decisions 1-4).
/// </summary>
[Collection(MasterDataApiCollection.Name)]
public sealed class MasterDataAuthorizationTests : MasterDataApiTestBase
{
    private const string AnyETag = "\"AAAAAAAAB9E=\"";

    public MasterDataAuthorizationTests(MasterDataApiFactory factory)
        : base(factory)
    {
    }

    public static TheoryData<string> BusinessRoles =>
    [
        RoleCodes.Requester,
        RoleCodes.Approver,
        RoleCodes.Coordinator,
        RoleCodes.Technician,
        RoleCodes.TeamLead,
        RoleCodes.Supervisor
    ];

    private static IEnumerable<(HttpMethod Method, string Url, object? Body, string? IfMatch)> ManageRequests(Guid customerId, Guid siteId, Guid equipmentId) =>
    [
        (HttpMethod.Post, "/api/v1/customers", new { customerCode = "NEW" }, null),
        (HttpMethod.Patch, $"/api/v1/customers/{customerId}", new { customerCode = "NEW" }, AnyETag),
        (HttpMethod.Post, $"/api/v1/customers/{customerId}/activate", null, AnyETag),
        (HttpMethod.Post, $"/api/v1/customers/{customerId}/deactivate", new { reason = "x" }, AnyETag),
        (HttpMethod.Post, $"/api/v1/customers/{customerId}/sites", new { siteCode = "NEW" }, null),
        (HttpMethod.Patch, $"/api/v1/sites/{siteId}", new { siteCode = "NEW" }, AnyETag),
        (HttpMethod.Post, $"/api/v1/sites/{siteId}/activate", null, AnyETag),
        (HttpMethod.Post, $"/api/v1/sites/{siteId}/deactivate", new { reason = "x" }, AnyETag),
        (HttpMethod.Post, $"/api/v1/sites/{siteId}/equipment", new { equipmentCode = "NEW" }, null),
        (HttpMethod.Patch, $"/api/v1/equipment/{equipmentId}", new { equipmentCode = "NEW" }, AnyETag),
        (HttpMethod.Post, $"/api/v1/equipment/{equipmentId}/activate", null, AnyETag),
        (HttpMethod.Post, $"/api/v1/equipment/{equipmentId}/deactivate", new { reason = "x" }, AnyETag)
    ];

    [Fact]
    public async Task EveryEndpoint_WithoutToken_Returns401()
    {
        var id = Guid.NewGuid();
        var reads = new[] { "/api/v1/customers", $"/api/v1/customers/{id}", $"/api/v1/customers/{id}/sites", $"/api/v1/sites/{id}", $"/api/v1/sites/{id}/equipment", $"/api/v1/equipment/{id}" };

        foreach (var url in reads)
        {
            await AssertProblemAsync(await SendAsync(HttpMethod.Get, url, null), HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
        }

        foreach (var (method, url, body, ifMatch) in ManageRequests(id, id, id))
        {
            await AssertProblemAsync(await SendAsync(method, url, null, body, ifMatch), HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
        }
    }

    [Theory]
    [MemberData(nameof(BusinessRoles))]
    public async Task BusinessRole_GetsNoConfigurationRights_OnInScopeOrUnknownRecords(string role)
    {
        var tenantId = Guid.NewGuid();
        var customer = await SeedCustomerAsync(tenantId, "CUST-1");
        var site = await SeedSiteAsync(customer, "SITE-1");
        var equipment = await SeedEquipmentAsync(site, "EQ-1");
        var caller = await CallerAsync(tenantId, [site], role);

        foreach (var (method, url, body, ifMatch) in ManageRequests(customer.Id, site.Id, equipment.Id)
                     .Concat(ManageRequests(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())))
        {
            await AssertProblemAsync(await SendAsync(method, url, caller, body, ifMatch), HttpStatusCode.Forbidden, "ACCESS_DENIED");
        }

        Assert.Empty(await AuditsAsync(customer.Id));
        Assert.Empty(await AuditsAsync(site.Id));
        Assert.Empty(await AuditsAsync(equipment.Id));
    }

    [Fact]
    public async Task BusinessRole_ReadsCustomersOnlyThroughAssignedSites()
    {
        var tenantId = Guid.NewGuid();
        var visibleCustomer = await SeedCustomerAsync(tenantId, "CUST-VISIBLE");
        var hiddenCustomer = await SeedCustomerAsync(tenantId, "CUST-HIDDEN");
        var site = await SeedSiteAsync(visibleCustomer, "SITE-1");
        await SeedSiteAsync(hiddenCustomer, "SITE-2");
        var requester = await CallerAsync(tenantId, [site], RoleCodes.Requester);

        var list = await JsonAsync(await SendAsync(HttpMethod.Get, "/api/v1/customers", requester));
        var visible = await SendAsync(HttpMethod.Get, $"/api/v1/customers/{visibleCustomer.Id}", requester);
        var hidden = await SendAsync(HttpMethod.Get, $"/api/v1/customers/{hiddenCustomer.Id}", requester);

        Assert.Equal(visibleCustomer.Id, Assert.Single(list.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.OK, visible.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
    }

    [Fact]
    public async Task Administrator_ManagesOwnTenantWithoutSiteAssignment_ButNeverAnotherTenant()
    {
        var tenantId = Guid.NewGuid();
        var ownCustomer = await SeedCustomerAsync(tenantId, "CUST-OWN");
        var ownSite = await SeedSiteAsync(ownCustomer, "SITE-OWN");
        var foreignCustomer = await SeedCustomerAsync(Guid.NewGuid(), "CUST-FOREIGN");
        var foreignSite = await SeedSiteAsync(foreignCustomer, "SITE-FOREIGN");
        var foreignEquipment = await SeedEquipmentAsync(foreignSite, "EQ-FOREIGN");
        var admin = await AdministratorAsync(tenantId);

        var ownList = await JsonAsync(await SendAsync(HttpMethod.Get, "/api/v1/customers", admin));
        var ownSiteCreate = await SendAsync(HttpMethod.Post, $"/api/v1/customers/{ownCustomer.Id}/sites", admin, new { siteCode = "SITE-NEW" });
        var ownEquipmentCreate = await SendAsync(HttpMethod.Post, $"/api/v1/sites/{ownSite.Id}/equipment", admin, new { equipmentCode = "EQ-NEW" });

        Assert.Equal(ownCustomer.Id, Assert.Single(ownList.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.Created, ownSiteCreate.StatusCode);
        Assert.Equal(HttpStatusCode.Created, ownEquipmentCreate.StatusCode);

        foreach (var (method, url, body, ifMatch) in ManageRequests(foreignCustomer.Id, foreignSite.Id, foreignEquipment.Id).Skip(1))
        {
            Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(method, url, admin, body, ifMatch)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"/api/v1/equipment/{foreignEquipment.Id}", admin)).StatusCode);
        Assert.Empty(await AuditsAsync(foreignCustomer.Id));
        Assert.Empty(await AuditsAsync(foreignSite.Id));
        Assert.Empty(await AuditsAsync(foreignEquipment.Id));
    }
}
