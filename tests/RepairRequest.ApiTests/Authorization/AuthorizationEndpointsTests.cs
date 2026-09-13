using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using RepairRequest.Domain.MasterData;
using RepairRequest.Domain.Security;
using RepairRequest.Infrastructure.Authentication;
using RepairRequest.Infrastructure.Identity;
using RepairRequest.Infrastructure.Persistence;
using RepairRequestAggregate = RepairRequest.Domain.RepairRequests.RepairRequest;

namespace RepairRequest.ApiTests.Authorization;

/// <summary>
/// End-to-end authorization behaviour through the real API pipeline (S1-003 decisions 1-4;
/// RR-API-001 section 2 non-leaking 403/404; TC-SEC-001 cross-tenant/site IDOR).
/// </summary>
public class AuthorizationEndpointsTests : IClassFixture<AuthorizationApiFactory>
{
    private readonly AuthorizationApiFactory _factory;

    public AuthorizationEndpointsTests(AuthorizationApiFactory factory)
    {
        _factory = factory;
    }

    private sealed record World(Guid TenantId, Guid OtherTenantId, Site SiteA1, Site SiteB1, Site OtherTenantSite);

    private HttpClient CreateClient(string? accessToken = null)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        if (accessToken is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        return client;
    }

    private async Task<World> CreateWorldAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RepairRequestDbContext>();

        var tenantId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();
        var customer = new Customer(tenantId, "CUST-1");
        var otherCustomer = new Customer(otherTenantId, "CUST-1");
        db.Customers.AddRange(customer, otherCustomer);

        var siteA1 = new Site(tenantId, customer.Id, "SITE-A1");
        var siteB1 = new Site(tenantId, customer.Id, "SITE-B1");
        var otherTenantSite = new Site(otherTenantId, otherCustomer.Id, "SITE-A1");
        db.Sites.AddRange(siteA1, siteB1, otherTenantSite);

        await db.SaveChangesAsync();
        return new World(tenantId, otherTenantId, siteA1, siteB1, otherTenantSite);
    }

    private async Task<ApplicationUser> CreateUserAsync(Guid tenantId, Site[] assignedSites, params string[] roles)
    {
        var user = await _factory.CreateUserInTenantAsync(tenantId, roles);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RepairRequestDbContext>();
        foreach (var site in assignedSites)
        {
            db.UserSiteScopes.Add(new UserSiteScope(tenantId, user.Id, site.Id));
        }

        await db.SaveChangesAsync();
        return user;
    }

    private async Task<Guid> AddRequestAsync(Guid tenantId, Guid createdBy, Site site)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RepairRequestDbContext>();
        var request = RepairRequestAggregate.CreateDraft(tenantId, createdBy);
        db.RepairRequests.Add(request).Property(r => r.SiteId).CurrentValue = site.Id;
        await db.SaveChangesAsync();
        return request.Id;
    }

    private async Task<string> LoginAsync(ApplicationUser user)
    {
        var response = await CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { email = user.Email, password = AuthApiFactory.ValidPassword });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
    }

    private string MintToken(Guid subject, params string[] roleClaims)
    {
        var options = _factory.Services.GetRequiredService<IOptions<JwtOptions>>().Value;
        var now = DateTime.UtcNow;
        var identity = new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, subject.ToString())]);
        foreach (var role in roleClaims)
        {
            identity.AddClaim(new Claim("role", role));
        }

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = options.Issuer,
            Audience = options.Audience,
            Subject = identity,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddMinutes(15),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Convert.FromBase64String(_factory.SigningKey)),
                SecurityAlgorithms.HmacSha256)
        });
    }

    private static async Task<(string Code, string Normalized)> ProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        Assert.NotNull(problem["correlationId"]);
        var code = problem["code"]!.GetValue<string>();
        problem.Remove("correlationId");
        return (code, problem.ToJsonString());
    }

    // ---------------- Authentication / caller resolution ----------------

    [Fact]
    public async Task ProtectedEndpoint_WithoutToken_Returns401Problem()
    {
        var response = await CreateClient().GetAsync("/test/authorization/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(response.Headers.WwwAuthenticate, header => header.Scheme == "Bearer");
        Assert.Equal("UNAUTHENTICATED", (await ProblemAsync(response)).Code);
    }

    [Fact]
    public async Task ValidTokenForUnknownUser_Returns401()
    {
        var response = await CreateClient(MintToken(Guid.NewGuid(), RoleCodes.Administrator)).GetAsync("/test/authorization/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("UNAUTHENTICATED", (await ProblemAsync(response)).Code);
    }

    [Fact]
    public async Task AuthenticatedCaller_IsResolvedWithTenantAndRolesFromTheDatabase()
    {
        var tenantId = Guid.NewGuid();
        var user = await _factory.CreateUserInTenantAsync(tenantId, RoleCodes.Requester, RoleCodes.Approver);

        var response = await CreateClient(await LoginAsync(user)).GetAsync("/test/authorization/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(user.Id, body.GetProperty("userId").GetGuid());
        Assert.Equal(tenantId, body.GetProperty("tenantId").GetGuid());
        Assert.Equal(
            [RoleCodes.Approver, RoleCodes.Requester],
            body.GetProperty("roles").EnumerateArray().Select(role => role.GetString()));
    }

    // ---------------- Role authorization (403) ----------------

    [Fact]
    public async Task ForgedRoleClaim_DoesNotGrantPermission()
    {
        var requester = await _factory.CreateUserAsync(RoleCodes.Requester);

        var response = await CreateClient(MintToken(requester.Id, RoleCodes.Administrator)).PostAsync("/test/authorization/master-data/manage", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("ACCESS_DENIED", (await ProblemAsync(response)).Code);
    }

    [Fact]
    public async Task ApprovedRole_IsAllowed_UnapprovedRole_Gets403()
    {
        var administrator = await _factory.CreateUserAsync(RoleCodes.Administrator);
        var requester = await _factory.CreateUserAsync(RoleCodes.Requester);

        var allowed = await CreateClient(await LoginAsync(administrator)).PostAsync("/test/authorization/master-data/manage", null);
        var denied = await CreateClient(await LoginAsync(requester)).PostAsync("/test/authorization/master-data/manage", null);

        Assert.Equal(HttpStatusCode.NoContent, allowed.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Fact]
    public async Task AdministratorWithoutBusinessRole_Gets403ForRepairRequests_BeforeAnyLookup()
    {
        var administrator = await _factory.CreateUserAsync(RoleCodes.Administrator);

        var response = await CreateClient(await LoginAsync(administrator)).GetAsync($"/test/authorization/repair-requests/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("ACCESS_DENIED", (await ProblemAsync(response)).Code);
    }

    // ---------------- Data scope / IDOR (404) ----------------

    [Fact]
    public async Task SiteLookup_InScopeReturns200_OutOfScopeCrossTenantAndUnknownReturnIdentical404()
    {
        var world = await CreateWorldAsync();
        var approver = await CreateUserAsync(world.TenantId, [world.SiteA1], RoleCodes.Approver);
        var client = CreateClient(await LoginAsync(approver));

        var inScope = await client.GetAsync($"/test/authorization/sites/{world.SiteA1.Id}");
        var unassigned = await client.GetAsync($"/test/authorization/sites/{world.SiteB1.Id}");
        var crossTenant = await client.GetAsync($"/test/authorization/sites/{world.OtherTenantSite.Id}");
        var unknown = await client.GetAsync($"/test/authorization/sites/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.OK, inScope.StatusCode);

        var expected = await ProblemAsync(unknown);
        Assert.Equal("NOT_FOUND", expected.Code);
        foreach (var denied in new[] { unassigned, crossTenant })
        {
            Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
            Assert.Equal(expected.Normalized, (await ProblemAsync(denied)).Normalized);
        }
    }

    [Fact]
    public async Task SiteList_ContainsOnlyAssignedSites()
    {
        var world = await CreateWorldAsync();
        var coordinator = await CreateUserAsync(world.TenantId, [world.SiteA1], RoleCodes.Coordinator);

        var siteIds = await CreateClient(await LoginAsync(coordinator)).GetFromJsonAsync<Guid[]>("/test/authorization/sites");

        Assert.NotNull(siteIds);
        Assert.Equal([world.SiteA1.Id], siteIds);
    }

    [Fact]
    public async Task UserWithNoSiteAssignment_CannotReadAnySite()
    {
        var world = await CreateWorldAsync();
        var supervisor = await CreateUserAsync(world.TenantId, [], RoleCodes.Supervisor);
        var client = CreateClient(await LoginAsync(supervisor));

        Assert.Empty((await client.GetFromJsonAsync<Guid[]>("/test/authorization/sites"))!);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/test/authorization/sites/{world.SiteA1.Id}")).StatusCode);
    }

    [Fact]
    public async Task RepairRequestLookup_AcrossSitesAndTenants_Returns404()
    {
        var world = await CreateWorldAsync();
        var requester = await CreateUserAsync(world.TenantId, [world.SiteA1, world.SiteB1], RoleCodes.Requester);
        var foreignRequester = await CreateUserAsync(world.OtherTenantId, [world.OtherTenantSite], RoleCodes.Requester);
        var approver = await CreateUserAsync(world.TenantId, [world.SiteA1], RoleCodes.Approver);

        var atA1 = await AddRequestAsync(world.TenantId, requester.Id, world.SiteA1);
        var atB1 = await AddRequestAsync(world.TenantId, requester.Id, world.SiteB1);
        var foreign = await AddRequestAsync(world.OtherTenantId, foreignRequester.Id, world.OtherTenantSite);

        var client = CreateClient(await LoginAsync(approver));

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/test/authorization/repair-requests/{atA1}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/test/authorization/repair-requests/{atB1}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/test/authorization/repair-requests/{foreign}")).StatusCode);
    }

    [Fact]
    public async Task ClientSuppliedTenantAndSiteIds_CannotBypassScope()
    {
        var world = await CreateWorldAsync();
        var requester = await CreateUserAsync(world.TenantId, [world.SiteA1], RoleCodes.Requester);
        var client = CreateClient(await LoginAsync(requester));

        var spoofedTenant = await client.PostAsJsonAsync("/test/authorization/repair-requests/site-check", new { tenantId = world.OtherTenantId, siteId = world.SiteA1.Id });
        var foreignSite = await client.PostAsJsonAsync("/test/authorization/repair-requests/site-check", new { tenantId = world.OtherTenantId, siteId = world.OtherTenantSite.Id });
        var unassignedSite = await client.PostAsJsonAsync("/test/authorization/repair-requests/site-check", new { tenantId = world.TenantId, siteId = world.SiteB1.Id });

        Assert.Equal(HttpStatusCode.OK, spoofedTenant.StatusCode);
        Assert.Equal(world.TenantId, (await spoofedTenant.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("tenantId").GetGuid());
        Assert.Equal(HttpStatusCode.NotFound, foreignSite.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unassignedSite.StatusCode);
    }

    // ---------------- Anonymous surface ----------------

    [Fact]
    public async Task ExplicitlyAnonymousEndpoints_RemainAccessibleWithoutToken()
    {
        var client = CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/v1/auth/revoke", null)).StatusCode);
    }
}
