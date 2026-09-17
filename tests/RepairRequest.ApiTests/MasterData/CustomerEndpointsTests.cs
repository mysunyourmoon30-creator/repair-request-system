using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RepairRequest.Domain.MasterData;

namespace RepairRequest.ApiTests.MasterData;

/// <summary>
/// Customer endpoints end to end: create, duplicate (DEC-PS1-015), update with If-Match/ETag, stale token,
/// activate/deactivate (DEC-PS1-013/014, decision D1), cross-tenant non-leaking 404, paging and audit.
/// </summary>
[Collection(MasterDataApiCollection.Name)]
public sealed class CustomerEndpointsTests : MasterDataApiTestBase
{
    public CustomerEndpointsTests(MasterDataApiFactory factory)
        : base(factory)
    {
    }

    [Fact]
    public async Task Create_ReturnsCreatedInCallerTenant_WithLocationETagAndAudit()
    {
        var tenantId = Guid.NewGuid();
        var admin = await AdministratorAsync(tenantId);
        var correlationId = Guid.NewGuid();

        var response = await SendAsync(
            HttpMethod.Post,
            "/api/v1/customers",
            admin,
            new { customerCode = "  CUST-001 ", tenantId = Guid.NewGuid(), status = "INACTIVE" },
            correlationId: correlationId);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await JsonAsync(response);
        var id = body.GetProperty("id").GetGuid();
        Assert.Equal("CUST-001", body.GetProperty("customerCode").GetString());
        Assert.Equal("ACTIVE", body.GetProperty("status").GetString());
        Assert.Equal($"/api/v1/customers/{id}", response.Headers.Location!.OriginalString);
        Assert.Equal($"\"{body.GetProperty("rowVersion").GetString()}\"", response.Headers.ETag!.Tag);

        var stored = await WithDbAsync(db => db.Customers.AsNoTracking().SingleAsync(customer => customer.Id == id));
        Assert.Equal(tenantId, stored.TenantId);

        var audit = Assert.Single(await AuditsAsync(id));
        Assert.Equal("CUSTOMER", audit.EntityType);
        Assert.Equal("CUSTOMER_CREATED", audit.ActionCode);
        Assert.Equal(tenantId, audit.TenantId);
        Assert.Equal(admin.UserId, audit.ActorId);
        Assert.Equal(correlationId, audit.CorrelationId);
        Assert.Equal("ACTIVE", audit.ToState);
        Assert.Contains("CUST-001", audit.NewValueJson);
    }

    [Fact]
    public async Task Create_DuplicateCodeInTenant_Returns422_ButOtherTenantMayReuseIt()
    {
        var tenantId = Guid.NewGuid();
        await SeedCustomerAsync(tenantId, "CUST-DUP");
        var admin = await AdministratorAsync(tenantId);
        var otherAdmin = await AdministratorAsync(Guid.NewGuid());

        var duplicate = await SendAsync(HttpMethod.Post, "/api/v1/customers", admin, new { customerCode = "cust-dup" });
        var otherTenant = await SendAsync(HttpMethod.Post, "/api/v1/customers", otherAdmin, new { customerCode = "CUST-DUP" });

        var problem = await AssertProblemAsync(duplicate, HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
        Assert.True(problem.GetProperty("errors").TryGetProperty("customerCode", out _));
        Assert.Equal(HttpStatusCode.Created, otherTenant.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZ012345")]
    public async Task Create_InvalidCode_Returns422(string code)
    {
        var admin = await AdministratorAsync(Guid.NewGuid());

        var response = await SendAsync(HttpMethod.Post, "/api/v1/customers", admin, new { customerCode = code });

        var problem = await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
        Assert.True(problem.GetProperty("errors").TryGetProperty("customerCode", out _));
    }

    [Fact]
    public async Task Update_WithCurrentETag_ChangesCode_ReturnsNewETag_AndAuditsOldAndNew()
    {
        var tenantId = Guid.NewGuid();
        var customer = await SeedCustomerAsync(tenantId, "CUST-OLD");
        var admin = await AdministratorAsync(tenantId);
        var url = $"/api/v1/customers/{customer.Id}";
        var etag = await ETagAsync(url, admin);

        var response = await SendAsync(HttpMethod.Patch, url, admin, new { customerCode = "CUST-NEW" }, etag);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("CUST-NEW", (await JsonAsync(response)).GetProperty("customerCode").GetString());
        Assert.NotEqual(etag, response.Headers.ETag!.Tag);

        var audit = Assert.Single(await AuditsAsync(customer.Id));
        Assert.Equal("CUSTOMER_UPDATED", audit.ActionCode);
        Assert.Contains("CUST-OLD", audit.OldValueJson);
        Assert.Contains("CUST-NEW", audit.NewValueJson);
    }

    [Fact]
    public async Task Update_WithStaleETag_Returns409ConcurrencyConflict_WithoutChangeOrAudit()
    {
        var tenantId = Guid.NewGuid();
        var customer = await SeedCustomerAsync(tenantId, "CUST-1");
        var admin = await AdministratorAsync(tenantId);
        var url = $"/api/v1/customers/{customer.Id}";
        var staleETag = await ETagAsync(url, admin);

        var first = await SendAsync(HttpMethod.Patch, url, admin, new { customerCode = "CUST-FIRST" }, staleETag);
        var second = await SendAsync(HttpMethod.Patch, url, admin, new { customerCode = "CUST-SECOND" }, staleETag);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        await AssertProblemAsync(second, HttpStatusCode.Conflict, "CONCURRENCY_CONFLICT");
        Assert.Equal("CUST-FIRST", await WithDbAsync(db => db.Customers.Where(c => c.Id == customer.Id).Select(c => c.CustomerCode).SingleAsync()));
        Assert.Single(await AuditsAsync(customer.Id));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("*")]
    [InlineData("W/\"AAAAAAAAB9E=\"")]
    [InlineData("\"not-a-row-version\"")]
    public async Task Update_WithoutUsableIfMatch_Returns400(string? ifMatch)
    {
        var tenantId = Guid.NewGuid();
        var customer = await SeedCustomerAsync(tenantId, "CUST-1");
        var admin = await AdministratorAsync(tenantId);

        var response = await SendAsync(HttpMethod.Patch, $"/api/v1/customers/{customer.Id}", admin, new { customerCode = "CUST-2" }, ifMatch);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "BAD_REQUEST");
    }

    [Fact]
    public async Task Deactivate_WithoutReason_Returns422()
    {
        var tenantId = Guid.NewGuid();
        var customer = await SeedCustomerAsync(tenantId, "CUST-1");
        var admin = await AdministratorAsync(tenantId);
        var url = $"/api/v1/customers/{customer.Id}";

        var response = await SendAsync(HttpMethod.Post, $"{url}/deactivate", admin, new { reason = "  " }, await ETagAsync(url, admin));

        var problem = await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
        Assert.True(problem.GetProperty("errors").TryGetProperty("reason", out _));
    }

    [Fact]
    public async Task Deactivate_WithActiveSite_Returns409StateConflict_ThenSucceedsOnceSiteIsInactive()
    {
        var tenantId = Guid.NewGuid();
        var customer = await SeedCustomerAsync(tenantId, "CUST-1");
        var site = await SeedSiteAsync(customer, "SITE-1");
        var admin = await AdministratorAsync(tenantId);
        var url = $"/api/v1/customers/{customer.Id}";

        var blocked = await SendAsync(HttpMethod.Post, $"{url}/deactivate", admin, new { reason = "Contract ended" }, await ETagAsync(url, admin));

        var problem = await AssertProblemAsync(blocked, HttpStatusCode.Conflict, "STATE_CONFLICT");
        Assert.Equal(1, problem.GetProperty("activeChildCount").GetInt32());
        Assert.Empty(await AuditsAsync(customer.Id));

        var siteUrl = $"/api/v1/sites/{site.Id}";
        var siteDeactivated = await SendAsync(HttpMethod.Post, $"{siteUrl}/deactivate", admin, new { reason = "Closed" }, await ETagAsync(siteUrl, admin));
        Assert.Equal(HttpStatusCode.OK, siteDeactivated.StatusCode);

        var allowed = await SendAsync(HttpMethod.Post, $"{url}/deactivate", admin, new { reason = "Contract ended" }, await ETagAsync(url, admin));

        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        var body = await JsonAsync(allowed);
        Assert.Equal("INACTIVE", body.GetProperty("status").GetString());
        Assert.Equal("Contract ended", body.GetProperty("deactivateReason").GetString());

        var audit = Assert.Single(await AuditsAsync(customer.Id));
        Assert.Equal("CUSTOMER_DEACTIVATED", audit.ActionCode);
        Assert.Equal("Contract ended", audit.Reason);
    }

    [Fact]
    public async Task Activate_ClearsReason_AndActivatingAgainReturns409()
    {
        var tenantId = Guid.NewGuid();
        var customer = await SeedCustomerAsync(tenantId, "CUST-1", inactiveReason: "Contract ended");
        var admin = await AdministratorAsync(tenantId);
        var url = $"/api/v1/customers/{customer.Id}";

        var activated = await SendAsync(HttpMethod.Post, $"{url}/activate", admin, ifMatch: await ETagAsync(url, admin));

        Assert.Equal(HttpStatusCode.OK, activated.StatusCode);
        var body = await JsonAsync(activated);
        Assert.Equal("ACTIVE", body.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("deactivateReason").ValueKind);

        var audit = Assert.Single(await AuditsAsync(customer.Id));
        Assert.Equal("CUSTOMER_ACTIVATED", audit.ActionCode);
        Assert.Contains("Contract ended", audit.OldValueJson);

        var again = await SendAsync(HttpMethod.Post, $"{url}/activate", admin, ifMatch: activated.Headers.ETag!.Tag);
        await AssertProblemAsync(again, HttpStatusCode.Conflict, "STATE_CONFLICT");
    }

    [Fact]
    public async Task OtherTenantCustomer_IsIndistinguishableFromNonexistent_ForEveryOperation()
    {
        var foreign = await SeedCustomerAsync(Guid.NewGuid(), "CUST-FOREIGN");
        var admin = await AdministratorAsync(Guid.NewGuid());
        var anyETag = "\"AAAAAAAAB9E=\"";
        var random = Guid.NewGuid();

        var foreignDetail = await SendAsync(HttpMethod.Get, $"/api/v1/customers/{foreign.Id}", admin);
        var randomDetail = await SendAsync(HttpMethod.Get, $"/api/v1/customers/{random}", admin);
        var update = await SendAsync(HttpMethod.Patch, $"/api/v1/customers/{foreign.Id}", admin, new { customerCode = "X" }, anyETag);
        var activate = await SendAsync(HttpMethod.Post, $"/api/v1/customers/{foreign.Id}/activate", admin, ifMatch: anyETag);
        var deactivate = await SendAsync(HttpMethod.Post, $"/api/v1/customers/{foreign.Id}/deactivate", admin, new { reason = "x" }, anyETag);
        var sites = await SendAsync(HttpMethod.Get, $"/api/v1/customers/{foreign.Id}/sites", admin);

        var expected = await NotFoundShapeAsync(randomDetail);
        Assert.Equal(expected, await NotFoundShapeAsync(foreignDetail));
        Assert.Equal(expected, await NotFoundShapeAsync(update));
        Assert.Equal(expected, await NotFoundShapeAsync(activate));
        Assert.Equal(expected, await NotFoundShapeAsync(deactivate));
        Assert.Equal(expected, await NotFoundShapeAsync(sites));
        Assert.Equal("CUST-FOREIGN", await WithDbAsync(db => db.Customers.Where(c => c.Id == foreign.Id).Select(c => c.CustomerCode).SingleAsync()));
    }

    [Fact]
    public async Task List_IsTenantScoped_FiltersByStatus_AndClampsPageSize()
    {
        var tenantId = Guid.NewGuid();
        await SeedCustomerAsync(tenantId, "CUST-B");
        await SeedCustomerAsync(tenantId, "CUST-A");
        await SeedCustomerAsync(tenantId, "CUST-C", inactiveReason: "Closed");
        await SeedCustomerAsync(Guid.NewGuid(), "CUST-OTHER-TENANT");
        var admin = await AdministratorAsync(tenantId);

        var all = await JsonAsync(await SendAsync(HttpMethod.Get, "/api/v1/customers?pageSize=1000", admin));
        var active = await JsonAsync(await SendAsync(HttpMethod.Get, "/api/v1/customers?status=ACTIVE&page=1&pageSize=1", admin));

        Assert.Equal(3, all.GetProperty("totalCount").GetInt32());
        Assert.Equal(100, all.GetProperty("pageSize").GetInt32());
        Assert.Equal(
            new[] { "CUST-A", "CUST-B", "CUST-C" },
            all.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("customerCode").GetString()));

        Assert.Equal(2, active.GetProperty("totalCount").GetInt32());
        Assert.Equal("CUST-A", Assert.Single(active.GetProperty("items").EnumerateArray()).GetProperty("customerCode").GetString());
    }

    [Theory]
    [InlineData("page=0")]
    [InlineData("pageSize=0")]
    [InlineData("status=DELETED")]
    public async Task List_InvalidPagingOrStatus_Returns400(string query)
    {
        var admin = await AdministratorAsync(Guid.NewGuid());

        var response = await SendAsync(HttpMethod.Get, $"/api/v1/customers?{query}", admin);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "BAD_REQUEST");
    }

    [Fact]
    public async Task Detail_ReturnsETagMatchingRowVersion()
    {
        var tenantId = Guid.NewGuid();
        var customer = await SeedCustomerAsync(tenantId, "CUST-1");
        var admin = await AdministratorAsync(tenantId);

        var response = await SendAsync(HttpMethod.Get, $"/api/v1/customers/{customer.Id}", admin);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal($"\"{Convert.ToBase64String(customer.RowVersion)}\"", response.Headers.ETag!.Tag);
        Assert.Equal(MasterDataStatus.Active.ToString().ToUpperInvariant(), (await JsonAsync(response)).GetProperty("status").GetString());
    }
}
