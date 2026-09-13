using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RepairRequest.Domain.Auditing;
using RepairRequest.Domain.MasterData;
using RepairRequest.Domain.Security;
using RepairRequest.Infrastructure.Identity;
using RepairRequest.Infrastructure.Persistence;

namespace RepairRequest.ApiTests.MasterData;

/// <summary>API host with its own disposable LocalDB database for the master-data endpoint tests.</summary>
public sealed class MasterDataApiFactory : AuthApiFactory
{
    public override string ConnectionString =>
        "Server=(localdb)\\MSSQLLocalDB;Database=RepairRequestDb_MasterDataApiTest;Trusted_Connection=True;TrustServerCertificate=True";
}

/// <summary>One shared host/database for every master-data API test class; classes run sequentially.</summary>
[CollectionDefinition(Name)]
public sealed class MasterDataApiCollection : ICollectionFixture<MasterDataApiFactory>
{
    public const string Name = "Master data API";
}

public sealed record ApiCaller(Guid UserId, Guid TenantId, string Token);

/// <summary>
/// Helpers for end-to-end master-data tests. Every test uses fresh tenant ids, so tests never observe each other's
/// data. Seeding goes straight to the database; behaviour under test always goes through HTTP.
/// </summary>
public abstract class MasterDataApiTestBase
{
    protected MasterDataApiTestBase(MasterDataApiFactory factory)
    {
        Factory = factory;
    }

    protected MasterDataApiFactory Factory { get; }

    protected HttpClient CreateClient() =>
        Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

    protected Task<ApiCaller> AdministratorAsync(Guid tenantId) => CallerAsync(tenantId, [], RoleCodes.Administrator);

    protected async Task<ApiCaller> CallerAsync(Guid tenantId, Site[] assignedSites, params string[] roles)
    {
        var user = await Factory.CreateUserInTenantAsync(tenantId, roles);

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

        return new ApiCaller(user.Id, tenantId, token);
    }

    protected async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string url,
        ApiCaller? caller,
        object? body = null,
        string? ifMatch = null,
        Guid? correlationId = null)
    {
        var request = new HttpRequestMessage(method, url);

        if (caller is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", caller.Token);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        if (correlationId is not null)
        {
            request.Headers.Add("X-Correlation-ID", correlationId.Value.ToString());
        }

        return await CreateClient().SendAsync(request);
    }

    /// <summary>Reads the current ETag of a resource through its detail endpoint.</summary>
    protected async Task<string> ETagAsync(string url, ApiCaller caller)
    {
        var response = await SendAsync(HttpMethod.Get, url, caller);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return response.Headers.ETag!.Tag;
    }

    protected static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    protected static async Task<JsonElement> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await JsonAsync(response);
        Assert.Equal(code, body.GetProperty("code").GetString());
        return body;
    }

    /// <summary>Problem body without per-request correlation data, for comparing non-leaking 404 responses.</summary>
    protected static async Task<string> NotFoundShapeAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        Assert.Equal("NOT_FOUND", (string?)node["code"]);
        node.Remove("correlationId");
        node.Remove("traceId");
        return node.ToJsonString();
    }

    protected async Task<T> WithDbAsync<T>(Func<RepairRequestDbContext, Task<T>> action)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<RepairRequestDbContext>());
    }

    protected async Task WithDbAsync(Func<RepairRequestDbContext, Task> action)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<RepairRequestDbContext>());
    }

    protected Task<Customer> SeedCustomerAsync(Guid tenantId, string code, string? inactiveReason = null) =>
        SeedAsync(new Customer(tenantId, code), inactiveReason);

    protected Task<Site> SeedSiteAsync(Customer customer, string code, string? inactiveReason = null) =>
        SeedAsync(new Site(customer.TenantId, customer.Id, code), inactiveReason);

    protected Task<Equipment> SeedEquipmentAsync(Site site, string code, string? inactiveReason = null) =>
        SeedAsync(new Equipment(site.TenantId, site.Id, code), inactiveReason);

    protected Task<List<AuditHistory>> AuditsAsync(Guid entityId) =>
        WithDbAsync(db => db.AuditHistory.AsNoTracking().Where(audit => audit.EntityId == entityId).OrderBy(audit => audit.OccurredAt).ToListAsync());

    private async Task<TEntity> SeedAsync<TEntity>(TEntity entity, string? inactiveReason)
        where TEntity : MasterDataEntity
    {
        if (inactiveReason is not null)
        {
            entity.Deactivate(inactiveReason);
        }

        await WithDbAsync(db =>
        {
            db.Add(entity);
            return db.SaveChangesAsync();
        });

        return entity;
    }
}
