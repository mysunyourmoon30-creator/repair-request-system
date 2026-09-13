using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RepairRequest.Domain.MasterData;
using RepairRequest.Domain.Security;

namespace RepairRequest.ApiTests.MasterData;

/// <summary>
/// Site and Equipment endpoints end to end: parent ownership from the route (DEC-PS1-002/003), inactive parent
/// on create and activate (decision D2), dependency guard without cascade (DEC-PS1-013), scoped uniqueness
/// (DEC-PS1-015), stale ETag, and site-scope enforcement for business roles (S1-003).
/// </summary>
[Collection(MasterDataApiCollection.Name)]
public sealed class SiteAndEquipmentEndpointsTests : MasterDataApiTestBase
{
    public SiteAndEquipmentEndpointsTests(MasterDataApiFactory factory)
        : base(factory)
    {
    }

    // ---------------- Site ----------------

    [Fact]
    public async Task CreateSite_UnderCustomerFromRoute_IgnoresSpoofedOwnershipFields()
    {
        var tenantId = Guid.NewGuid();
        var customer = await SeedCustomerAsync(tenantId, "CUST-1");
        var decoy = await SeedCustomerAsync(tenantId, "CUST-DECOY");
        var admin = await AdministratorAsync(tenantId);

        var response = await SendAsync(
            HttpMethod.Post,
            $"/api/v1/customers/{customer.Id}/sites",
            admin,
            new { siteCode = "SITE-1", customerId = decoy.Id, tenantId = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await JsonAsync(response);
        var siteId = body.GetProperty("id").GetGuid();
        Assert.Equal(customer.Id, body.GetProperty("customerId").GetGuid());
        Assert.Equal($"/api/v1/sites/{siteId}", response.Headers.Location!.OriginalString);

        var stored = await WithDbAsync(db => db.Sites.AsNoTracking().SingleAsync(site => site.Id == siteId));
        Assert.Equal(tenantId, stored.TenantId);
        Assert.Equal(customer.Id, stored.CustomerId);
        Assert.Equal("SITE_CREATED", Assert.Single(await AuditsAsync(siteId)).ActionCode);
    }

    [Fact]
    public async Task CreateSite_DuplicateWithinCustomer_Returns422_ButSameCodeUnderAnotherCustomerIsCreated()
    {
        var tenantId = Guid.NewGuid();
        var customerA = await SeedCustomerAsync(tenantId, "CUST-A");
        var customerB = await SeedCustomerAsync(tenantId, "CUST-B");
        await SeedSiteAsync(customerA, "SITE-1");
        var admin = await AdministratorAsync(tenantId);

        var duplicate = await SendAsync(HttpMethod.Post, $"/api/v1/customers/{customerA.Id}/sites", admin, new { siteCode = "SITE-1" });
        var otherCustomer = await SendAsync(HttpMethod.Post, $"/api/v1/customers/{customerB.Id}/sites", admin, new { siteCode = "SITE-1" });

        var problem = await AssertProblemAsync(duplicate, HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
        Assert.True(problem.GetProperty("errors").TryGetProperty("siteCode", out _));
        Assert.Equal(HttpStatusCode.Created, otherCustomer.StatusCode);
    }

    [Fact]
    public async Task CreateSite_UnderForeignOrUnknownCustomer_Returns404_UnderInactiveCustomer_Returns422()
    {
        var tenantId = Guid.NewGuid();
        var foreign = await SeedCustomerAsync(Guid.NewGuid(), "CUST-FOREIGN");
        var inactive = await SeedCustomerAsync(tenantId, "CUST-INACTIVE", inactiveReason: "Closed");
        var admin = await AdministratorAsync(tenantId);

        var foreignResponse = await SendAsync(HttpMethod.Post, $"/api/v1/customers/{foreign.Id}/sites", admin, new { siteCode = "SITE-1" });
        var unknownResponse = await SendAsync(HttpMethod.Post, $"/api/v1/customers/{Guid.NewGuid()}/sites", admin, new { siteCode = "SITE-1" });
        var inactiveResponse = await SendAsync(HttpMethod.Post, $"/api/v1/customers/{inactive.Id}/sites", admin, new { siteCode = "SITE-1" });

        Assert.Equal(await NotFoundShapeAsync(unknownResponse), await NotFoundShapeAsync(foreignResponse));
        var problem = await AssertProblemAsync(inactiveResponse, HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
        Assert.True(problem.GetProperty("errors").TryGetProperty("customerId", out _));
        Assert.False(await WithDbAsync(db => db.Sites.AnyAsync(site => site.CustomerId == foreign.Id || site.CustomerId == inactive.Id)));
    }

    [Fact]
    public async Task UpdateSite_ChangesCode_StaleETagReturns409()
    {
        var tenantId = Guid.NewGuid();
        var site = await SeedSiteAsync(await SeedCustomerAsync(tenantId, "CUST-1"), "SITE-1");
        var admin = await AdministratorAsync(tenantId);
        var url = $"/api/v1/sites/{site.Id}";
        var etag = await ETagAsync(url, admin);

        var updated = await SendAsync(HttpMethod.Patch, url, admin, new { siteCode = "SITE-9" }, etag);
        var stale = await SendAsync(HttpMethod.Patch, url, admin, new { siteCode = "SITE-10" }, etag);

        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal("SITE-9", (await JsonAsync(updated)).GetProperty("siteCode").GetString());
        await AssertProblemAsync(stale, HttpStatusCode.Conflict, "CONCURRENCY_CONFLICT");
    }

    [Fact]
    public async Task DeactivateSite_WithActiveEquipment_Returns409_AndNeverCascadesToEquipment()
    {
        var tenantId = Guid.NewGuid();
        var site = await SeedSiteAsync(await SeedCustomerAsync(tenantId, "CUST-1"), "SITE-1");
        var equipment = await SeedEquipmentAsync(site, "EQ-1");
        var admin = await AdministratorAsync(tenantId);
        var url = $"/api/v1/sites/{site.Id}";

        var blocked = await SendAsync(HttpMethod.Post, $"{url}/deactivate", admin, new { reason = "Closed" }, await ETagAsync(url, admin));

        var problem = await AssertProblemAsync(blocked, HttpStatusCode.Conflict, "STATE_CONFLICT");
        Assert.Equal(1, problem.GetProperty("activeChildCount").GetInt32());
        Assert.Equal(MasterDataStatus.Active, await WithDbAsync(db => db.Sites.Where(s => s.Id == site.Id).Select(s => s.Status).SingleAsync()));
        Assert.Equal(MasterDataStatus.Active, await WithDbAsync(db => db.Equipment.Where(e => e.Id == equipment.Id).Select(e => e.Status).SingleAsync()));
    }

    [Fact]
    public async Task ActivateSite_UnderInactiveCustomer_Returns422_AndUnderActiveCustomerClearsReason()
    {
        var tenantId = Guid.NewGuid();
        var inactiveCustomer = await SeedCustomerAsync(tenantId, "CUST-INACTIVE", inactiveReason: "Closed");
        var blockedSite = await SeedSiteAsync(inactiveCustomer, "SITE-1", inactiveReason: "Closed");
        var activeCustomer = await SeedCustomerAsync(tenantId, "CUST-ACTIVE");
        var allowedSite = await SeedSiteAsync(activeCustomer, "SITE-1", inactiveReason: "Closed");
        var admin = await AdministratorAsync(tenantId);

        var blockedUrl = $"/api/v1/sites/{blockedSite.Id}";
        var blocked = await SendAsync(HttpMethod.Post, $"{blockedUrl}/activate", admin, ifMatch: await ETagAsync(blockedUrl, admin));
        var allowedUrl = $"/api/v1/sites/{allowedSite.Id}";
        var allowed = await SendAsync(HttpMethod.Post, $"{allowedUrl}/activate", admin, ifMatch: await ETagAsync(allowedUrl, admin));

        var problem = await AssertProblemAsync(blocked, HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
        Assert.True(problem.GetProperty("errors").TryGetProperty("customerId", out _));
        Assert.Empty(await AuditsAsync(blockedSite.Id));

        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await JsonAsync(allowed)).GetProperty("deactivateReason").ValueKind);
    }

    [Fact]
    public async Task BusinessRole_SeesOnlyAssignedSites_AndCannotChangeThem()
    {
        var tenantId = Guid.NewGuid();
        var customer = await SeedCustomerAsync(tenantId, "CUST-1");
        var assigned = await SeedSiteAsync(customer, "SITE-ASSIGNED");
        var unassigned = await SeedSiteAsync(customer, "SITE-UNASSIGNED");
        var otherTenantSite = await SeedSiteAsync(await SeedCustomerAsync(Guid.NewGuid(), "CUST-1"), "SITE-ASSIGNED");
        var technician = await CallerAsync(tenantId, [assigned], RoleCodes.Technician);

        var list = await JsonAsync(await SendAsync(HttpMethod.Get, $"/api/v1/customers/{customer.Id}/sites", technician));
        var assignedDetail = await SendAsync(HttpMethod.Get, $"/api/v1/sites/{assigned.Id}", technician);
        var unassignedDetail = await SendAsync(HttpMethod.Get, $"/api/v1/sites/{unassigned.Id}", technician);
        var otherTenantDetail = await SendAsync(HttpMethod.Get, $"/api/v1/sites/{otherTenantSite.Id}", technician);
        var update = await SendAsync(HttpMethod.Patch, $"/api/v1/sites/{assigned.Id}", technician, new { siteCode = "HIJACK" }, assignedDetail.Headers.ETag!.Tag);

        Assert.Equal(assigned.Id, Assert.Single(list.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.OK, assignedDetail.StatusCode);
        Assert.Equal(await NotFoundShapeAsync(unassignedDetail), await NotFoundShapeAsync(otherTenantDetail));
        await AssertProblemAsync(update, HttpStatusCode.Forbidden, "ACCESS_DENIED");
    }

    // ---------------- Equipment ----------------

    [Fact]
    public async Task CreateEquipment_UnderSite_DuplicateWithinSiteIs422_SameCodeAtAnotherSiteIsCreated()
    {
        var tenantId = Guid.NewGuid();
        var customer = await SeedCustomerAsync(tenantId, "CUST-1");
        var siteA = await SeedSiteAsync(customer, "SITE-A");
        var siteB = await SeedSiteAsync(customer, "SITE-B");
        var admin = await AdministratorAsync(tenantId);

        var created = await SendAsync(HttpMethod.Post, $"/api/v1/sites/{siteA.Id}/equipment", admin, new { equipmentCode = "EQ-1", siteId = siteB.Id });
        var duplicate = await SendAsync(HttpMethod.Post, $"/api/v1/sites/{siteA.Id}/equipment", admin, new { equipmentCode = "eq-1" });
        var otherSite = await SendAsync(HttpMethod.Post, $"/api/v1/sites/{siteB.Id}/equipment", admin, new { equipmentCode = "EQ-1" });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await JsonAsync(created);
        Assert.Equal(siteA.Id, body.GetProperty("siteId").GetGuid());
        Assert.Equal($"/api/v1/equipment/{body.GetProperty("id").GetGuid()}", created.Headers.Location!.OriginalString);

        var problem = await AssertProblemAsync(duplicate, HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
        Assert.True(problem.GetProperty("errors").TryGetProperty("equipmentCode", out _));
        Assert.Equal(HttpStatusCode.Created, otherSite.StatusCode);
    }

    [Fact]
    public async Task CreateEquipment_UnderForeignSite_Returns404_UnderInactiveSite_Returns422()
    {
        var tenantId = Guid.NewGuid();
        var foreignSite = await SeedSiteAsync(await SeedCustomerAsync(Guid.NewGuid(), "CUST-F"), "SITE-F");
        var inactiveSite = await SeedSiteAsync(await SeedCustomerAsync(tenantId, "CUST-1"), "SITE-1", inactiveReason: "Closed");
        var admin = await AdministratorAsync(tenantId);

        var foreign = await SendAsync(HttpMethod.Post, $"/api/v1/sites/{foreignSite.Id}/equipment", admin, new { equipmentCode = "EQ-1" });
        var unknown = await SendAsync(HttpMethod.Post, $"/api/v1/sites/{Guid.NewGuid()}/equipment", admin, new { equipmentCode = "EQ-1" });
        var inactive = await SendAsync(HttpMethod.Post, $"/api/v1/sites/{inactiveSite.Id}/equipment", admin, new { equipmentCode = "EQ-1" });

        Assert.Equal(await NotFoundShapeAsync(unknown), await NotFoundShapeAsync(foreign));
        var problem = await AssertProblemAsync(inactive, HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
        Assert.True(problem.GetProperty("errors").TryGetProperty("siteId", out _));
    }

    [Fact]
    public async Task Equipment_UpdateStaleETag_DeactivateWithoutReason_ThenDeactivateAndReactivate()
    {
        var tenantId = Guid.NewGuid();
        var equipment = await SeedEquipmentAsync(await SeedSiteAsync(await SeedCustomerAsync(tenantId, "CUST-1"), "SITE-1"), "EQ-1");
        var admin = await AdministratorAsync(tenantId);
        var url = $"/api/v1/equipment/{equipment.Id}";
        var original = await ETagAsync(url, admin);

        var updated = await SendAsync(HttpMethod.Patch, url, admin, new { equipmentCode = "EQ-9" }, original);
        var stale = await SendAsync(HttpMethod.Patch, url, admin, new { equipmentCode = "EQ-10" }, original);
        var noReason = await SendAsync(HttpMethod.Post, $"{url}/deactivate", admin, new { reason = "" }, updated.Headers.ETag!.Tag);
        var deactivated = await SendAsync(HttpMethod.Post, $"{url}/deactivate", admin, new { reason = "Broken" }, updated.Headers.ETag!.Tag);
        var reactivated = await SendAsync(HttpMethod.Post, $"{url}/activate", admin, ifMatch: deactivated.Headers.ETag!.Tag);

        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        await AssertProblemAsync(stale, HttpStatusCode.Conflict, "CONCURRENCY_CONFLICT");
        await AssertProblemAsync(noReason, HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
        Assert.Equal("Broken", (await JsonAsync(deactivated)).GetProperty("deactivateReason").GetString());
        Assert.Equal(HttpStatusCode.OK, reactivated.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await JsonAsync(reactivated)).GetProperty("deactivateReason").ValueKind);

        Assert.Equal(
            new[] { "EQUIPMENT_UPDATED", "EQUIPMENT_DEACTIVATED", "EQUIPMENT_ACTIVATED" },
            (await AuditsAsync(equipment.Id)).Select(audit => audit.ActionCode));
    }

    [Fact]
    public async Task ActivateEquipment_UnderInactiveSite_Returns422()
    {
        var tenantId = Guid.NewGuid();
        var site = await SeedSiteAsync(await SeedCustomerAsync(tenantId, "CUST-1"), "SITE-1", inactiveReason: "Closed");
        var equipment = await SeedEquipmentAsync(site, "EQ-1", inactiveReason: "Broken");
        var admin = await AdministratorAsync(tenantId);
        var url = $"/api/v1/equipment/{equipment.Id}";

        var response = await SendAsync(HttpMethod.Post, $"{url}/activate", admin, ifMatch: await ETagAsync(url, admin));

        var problem = await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
        Assert.True(problem.GetProperty("errors").TryGetProperty("siteId", out _));
    }

    [Fact]
    public async Task BusinessRole_SeesEquipmentOnlyAtAssignedSites()
    {
        var tenantId = Guid.NewGuid();
        var customer = await SeedCustomerAsync(tenantId, "CUST-1");
        var assignedSite = await SeedSiteAsync(customer, "SITE-A");
        var otherSite = await SeedSiteAsync(customer, "SITE-B");
        var visible = await SeedEquipmentAsync(assignedSite, "EQ-1");
        var hidden = await SeedEquipmentAsync(otherSite, "EQ-1");
        var technician = await CallerAsync(tenantId, [assignedSite], RoleCodes.Technician);

        var visibleList = await SendAsync(HttpMethod.Get, $"/api/v1/sites/{assignedSite.Id}/equipment", technician);
        var hiddenList = await SendAsync(HttpMethod.Get, $"/api/v1/sites/{otherSite.Id}/equipment", technician);
        var visibleDetail = await SendAsync(HttpMethod.Get, $"/api/v1/equipment/{visible.Id}", technician);
        var hiddenDetail = await SendAsync(HttpMethod.Get, $"/api/v1/equipment/{hidden.Id}", technician);
        var randomDetail = await SendAsync(HttpMethod.Get, $"/api/v1/equipment/{Guid.NewGuid()}", technician);

        Assert.Equal(HttpStatusCode.OK, visibleList.StatusCode);
        Assert.Equal(visible.Id, Assert.Single((await JsonAsync(visibleList)).GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.NotFound, hiddenList.StatusCode);
        Assert.Equal(HttpStatusCode.OK, visibleDetail.StatusCode);
        Assert.Equal(await NotFoundShapeAsync(randomDetail), await NotFoundShapeAsync(hiddenDetail));
    }
}
