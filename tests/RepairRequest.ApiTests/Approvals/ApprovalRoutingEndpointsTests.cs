using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RepairRequest.Application.Approvals;
using RepairRequest.Application.Attachments;
using RepairRequest.Domain.Approvals;
using RepairRequest.Domain.Files;
using RepairRequest.Domain.MasterData;
using RepairRequest.Domain.RepairRequests;
using RepairRequest.Domain.Security;
using RepairRequest.Infrastructure.Identity;
using RepairRequest.Infrastructure.Persistence;
using RepairRequest.Infrastructure.RepairRequests;

namespace RepairRequest.ApiTests.Approvals;

/// <summary>API host with its own disposable LocalDB database for the S1-007R routing endpoint tests.</summary>
public sealed class ApprovalRoutingApiFactory : AuthApiFactory
{
    public override string ConnectionString =>
        "Server=(localdb)\\MSSQLLocalDB;Database=RepairRequestDb_ApprovalRoutingApiTest;Trusted_Connection=True;TrustServerCertificate=True";
}

/// <summary>
/// S1-007R endpoints end to end (DEC-PRE-S1-007R-01..10): route configuration by ADMINISTRATOR, Submit returning the routed
/// state, the metadata-only routing-issue list, Admin Retry Routing with If-Match, and the authorization boundary
/// (401 / 403 / non-leaking 404; ADMINISTRATOR still has no Repair Request detail).
/// </summary>
public sealed class ApprovalRoutingEndpointsTests : IClassFixture<ApprovalRoutingApiFactory>
{
    private const string Routes = "/api/v1/approval-routes";
    private const string RoutingIssues = "/api/v1/routing-issues";
    private const string Requests = "/api/v1/repair-requests";
    private const string ValidHash = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";
    private const string AnyETag = "\"AAAAAAAAB9E=\"";

    private readonly ApprovalRoutingApiFactory _factory;

    public ApprovalRoutingEndpointsTests(ApprovalRoutingApiFactory factory)
    {
        _factory = factory;
    }

    private sealed record Caller(Guid UserId, Guid TenantId, string Token);

    private sealed record World(Guid TenantId, Site SiteA, Site SiteB, Guid OtherTenantId, Site OtherTenantSite);

    // ---------------- Authorization boundary ----------------

    [Fact]
    public async Task RoutingEndpoints_WithoutToken_Return401()
    {
        var id = Guid.NewGuid();

        foreach (var (method, url, body, ifMatch) in Endpoints(id))
        {
            await AssertProblemAsync(await SendAsync(method, url, null, body, ifMatch), HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
        }
    }

    [Theory]
    [InlineData(RoleCodes.Approver)]
    [InlineData(RoleCodes.Requester)]
    [InlineData(RoleCodes.Supervisor)]
    [InlineData(RoleCodes.Coordinator)]
    public async Task BusinessRoles_Get403_OnRouteConfigurationAndRoutingRecovery(string role)
    {
        var world = await WorldAsync();
        var caller = await CallerAsync(world.TenantId, [world.SiteA], role);

        foreach (var (method, url, body, ifMatch) in Endpoints(Guid.NewGuid()))
        {
            await AssertProblemAsync(await SendAsync(method, url, caller, body, ifMatch), HttpStatusCode.Forbidden, "ACCESS_DENIED");
        }
    }

    [Fact]
    public async Task Administrator_StillHasNoRepairRequestDetail_OrSubmit()
    {
        var world = await WorldAsync();
        var admin = await CallerAsync(world.TenantId, [], RoleCodes.Administrator);

        await AssertProblemAsync(await SendAsync(HttpMethod.Get, $"{Requests}/{Guid.NewGuid()}", admin), HttpStatusCode.Forbidden, "ACCESS_DENIED");
        await AssertProblemAsync(await SendAsync(HttpMethod.Post, $"{Requests}/{Guid.NewGuid()}/submit", admin, new { }, AnyETag), HttpStatusCode.Forbidden, "ACCESS_DENIED");
    }

    private static IEnumerable<(HttpMethod Method, string Url, object? Body, string? IfMatch)> Endpoints(Guid id) =>
    [
        (HttpMethod.Get, Routes, null, null),
        (HttpMethod.Get, $"{Routes}/{id}", null, null),
        (HttpMethod.Post, Routes, new { requestCategoryCode = "ELECTRICAL", approverRoleCode = "APPROVER" }, null),
        (HttpMethod.Post, $"{Routes}/{id}/activate", null, AnyETag),
        (HttpMethod.Post, $"{Routes}/{id}/deactivate", null, AnyETag),
        (HttpMethod.Get, RoutingIssues, null, null),
        (HttpMethod.Post, $"{Requests}/{id}/retry-routing", null, AnyETag)
    ];

    // ---------------- Route configuration ----------------

    [Fact]
    public async Task Administrator_CreatesDeactivatesAndActivatesARoute_WithETags()
    {
        var world = await WorldAsync();
        var admin = await CallerAsync(world.TenantId, [], RoleCodes.Administrator);
        var approver = await CallerAsync(world.TenantId, [world.SiteA], RoleCodes.Approver);

        var created = await SendAsync(HttpMethod.Post, Routes, admin, new
        {
            requestCategoryCode = "electrical",
            siteId = world.SiteA.Id,
            approverRoleCode = "APPROVER",
            approverUserId = approver.UserId,
            tenantId = Guid.NewGuid(),
            status = "INACTIVE"
        });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await JsonAsync(created);
        var routeId = body.GetProperty("id").GetGuid();
        Assert.Equal($"/api/v1/approval-routes/{routeId}", created.Headers.Location!.OriginalString);
        Assert.Equal("ELECTRICAL", body.GetProperty("requestCategoryCode").GetString());
        Assert.Equal("ACTIVE", body.GetProperty("status").GetString());
        Assert.Equal(1, body.GetProperty("stepNo").GetInt32());
        Assert.Equal("APPROVER", body.GetProperty("approverRoleCode").GetString());
        Assert.Equal(approver.UserId, body.GetProperty("approverUserId").GetGuid());
        Assert.Equal(world.TenantId, await _factory.WithDbAsync(db => db.ApprovalRoutes.Where(route => route.Id == routeId).Select(route => route.TenantId).SingleAsync()));

        var etag = created.Headers.ETag!.Tag;
        await AssertProblemAsync(await SendAsync(HttpMethod.Post, $"{Routes}/{routeId}/deactivate", admin), HttpStatusCode.BadRequest, "BAD_REQUEST");
        await AssertProblemAsync(await SendAsync(HttpMethod.Post, $"{Routes}/{routeId}/deactivate", admin, null, AnyETag), HttpStatusCode.Conflict, "CONCURRENCY_CONFLICT");

        var deactivated = await SendAsync(HttpMethod.Post, $"{Routes}/{routeId}/deactivate", admin, null, etag);
        Assert.Equal(HttpStatusCode.OK, deactivated.StatusCode);
        Assert.Equal("INACTIVE", (await JsonAsync(deactivated)).GetProperty("status").GetString());

        var activated = await SendAsync(HttpMethod.Post, $"{Routes}/{routeId}/activate", admin, null, deactivated.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.OK, activated.StatusCode);
        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{Routes}/{routeId}/activate", admin, null, activated.Headers.ETag!.Tag),
            HttpStatusCode.Conflict,
            "STATE_CONFLICT");

        var list = await JsonAsync(await SendAsync(HttpMethod.Get, $"{Routes}?status=ACTIVE", admin));
        Assert.Equal(routeId, Assert.Single(list.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task RouteCreate_ValidatesRoleApproverUserAndOneActiveRoutePerKey()
    {
        var world = await WorldAsync();
        var admin = await CallerAsync(world.TenantId, [], RoleCodes.Administrator);
        var supervisor = await CallerAsync(world.TenantId, [world.SiteA], RoleCodes.Supervisor);

        async Task AssertInvalidAsync(object body, string field)
        {
            var problem = await AssertProblemAsync(await SendAsync(HttpMethod.Post, Routes, admin, body), HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
            Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _), field);
        }

        await AssertInvalidAsync(new { requestCategoryCode = "ELECTRICAL", approverRoleCode = "COORDINATOR" }, "approverRoleCode");
        await AssertInvalidAsync(new { requestCategoryCode = "ELECTRICAL", approverRoleCode = "APPROVER", approverUserId = supervisor.UserId }, "approverUserId");
        await AssertInvalidAsync(new { requestCategoryCode = "NOT-A-CATEGORY", approverRoleCode = "APPROVER" }, "requestCategoryCode");
        await AssertInvalidAsync(new { requestCategoryCode = "ELECTRICAL", approverRoleCode = "APPROVER", siteId = world.OtherTenantSite.Id }, "siteId");

        Assert.Equal(HttpStatusCode.Created, (await SendAsync(HttpMethod.Post, Routes, admin, new { requestCategoryCode = "ELECTRICAL", approverRoleCode = "APPROVER" })).StatusCode);
        await AssertInvalidAsync(new { requestCategoryCode = "ELECTRICAL", approverRoleCode = "APPROVER" }, "requestCategoryCode");
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(HttpMethod.Post, Routes, admin, new { requestCategoryCode = "ELECTRICAL", siteId = world.SiteA.Id, approverRoleCode = "APPROVER" })).StatusCode);
    }

    [Fact]
    public async Task RouteOfAnotherTenant_IsNotFound()
    {
        var world = await WorldAsync();
        var admin = await CallerAsync(world.TenantId, [], RoleCodes.Administrator);
        var otherAdmin = await CallerAsync(world.OtherTenantId, [], RoleCodes.Administrator);
        var created = await SendAsync(HttpMethod.Post, Routes, admin, new { requestCategoryCode = "ELECTRICAL", approverRoleCode = "APPROVER" });
        var routeId = (await JsonAsync(created)).GetProperty("id").GetGuid();

        await AssertProblemAsync(await SendAsync(HttpMethod.Get, $"{Routes}/{routeId}", otherAdmin), HttpStatusCode.NotFound, "NOT_FOUND");
        await AssertProblemAsync(await SendAsync(HttpMethod.Post, $"{Routes}/{routeId}/deactivate", otherAdmin, null, created.Headers.ETag!.Tag), HttpStatusCode.NotFound, "NOT_FOUND");
        Assert.Empty((await JsonAsync(await SendAsync(HttpMethod.Get, Routes, otherAdmin))).GetProperty("items").EnumerateArray());
    }

    // ---------------- Submit + routing (DEC-PRE-S1-007R-01) ----------------

    [Fact]
    public async Task Submit_WithAConfiguredRoute_ReturnsUnderReview_WithAFreshETag_AndNeverAssignsTheRequesterApprover()
    {
        var world = await WorldAsync();
        var admin = await CallerAsync(world.TenantId, [], RoleCodes.Administrator);
        var requesterApprover = await CallerAsync(world.TenantId, [world.SiteA], RoleCodes.Requester, RoleCodes.Approver);
        var approver = await CallerAsync(world.TenantId, [world.SiteA], RoleCodes.Approver);
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(HttpMethod.Post, Routes, admin, new { requestCategoryCode = "ELECTRICAL", siteId = world.SiteA.Id, approverRoleCode = "APPROVER" })).StatusCode);
        var (id, etag) = await CompleteDraftAsync(requesterApprover, world.SiteA);
        await AttachAsync(requesterApprover, id);

        var submitted = await SendAsync(HttpMethod.Post, $"{Requests}/{id}/submit", requesterApprover, new { }, etag);

        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
        var body = await JsonAsync(submitted);
        Assert.Equal("UNDER_REVIEW", body.GetProperty("status").GetString());
        Assert.StartsWith("RR-", body.GetProperty("requestNo").GetString());
        Assert.Equal(submitted.Headers.ETag!.Tag, $"\"{body.GetProperty("rowVersion").GetString()}\"");

        var detail = await SendAsync(HttpMethod.Get, $"{Requests}/{id}", requesterApprover);
        Assert.Equal("UNDER_REVIEW", (await JsonAsync(detail)).GetProperty("status").GetString());
        Assert.Equal(submitted.Headers.ETag.Tag, detail.Headers.ETag!.Tag);

        var assigned = await _factory.WithDbAsync(db => db.RepairRequestApprovals.Where(approval => approval.RepairRequestId == id).Select(approval => approval.AssignedApproverId).SingleAsync());
        Assert.Equal(approver.UserId, assigned);
        Assert.NotEqual(requesterApprover.UserId, assigned);
    }

    [Fact]
    public async Task UnroutedSubmit_IsListedWithMetadataOnly_AndAdminRetryRoutesItAfterConfiguration()
    {
        var world = await WorldAsync();
        var admin = await CallerAsync(world.TenantId, [], RoleCodes.Administrator);
        var otherAdmin = await CallerAsync(world.OtherTenantId, [], RoleCodes.Administrator);
        var requester = await CallerAsync(world.TenantId, [world.SiteA], RoleCodes.Requester);
        await CallerAsync(world.TenantId, [world.SiteA], RoleCodes.Approver);
        var (id, etag) = await CompleteDraftAsync(requester, world.SiteA);
        await AttachAsync(requester, id);

        var submitted = await SendAsync(HttpMethod.Post, $"{Requests}/{id}/submit", requester, new { }, etag);
        Assert.Equal("SUBMITTED", (await JsonAsync(submitted)).GetProperty("status").GetString());

        var list = await SendAsync(HttpMethod.Get, $"{RoutingIssues}?page=1&pageSize=10", admin);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var item = Assert.Single((await JsonAsync(list)).GetProperty("items").EnumerateArray());
        Assert.Equal(
            ["lastRoutingFailureCode", "repairRequestId", "requestCategoryCode", "requestNo", "rowVersion", "siteId", "submittedAt"],
            item.EnumerateObject().Select(property => property.Name).Order());
        Assert.Equal(id, item.GetProperty("repairRequestId").GetGuid());
        Assert.Equal(RoutingFailureCodes.RouteNotFound, item.GetProperty("lastRoutingFailureCode").GetString());
        var listedETag = $"\"{item.GetProperty("rowVersion").GetString()}\"";
        Assert.Equal(submitted.Headers.ETag!.Tag, listedETag);

        Assert.Empty((await JsonAsync(await SendAsync(HttpMethod.Get, RoutingIssues, otherAdmin))).GetProperty("items").EnumerateArray());
        await AssertProblemAsync(await SendAsync(HttpMethod.Post, $"{Requests}/{id}/retry-routing", otherAdmin, null, listedETag), HttpStatusCode.NotFound, "NOT_FOUND");
        await AssertProblemAsync(await SendAsync(HttpMethod.Post, $"{Requests}/{id}/retry-routing", admin), HttpStatusCode.BadRequest, "BAD_REQUEST");
        await AssertProblemAsync(await SendAsync(HttpMethod.Post, $"{Requests}/{id}/retry-routing", admin, null, AnyETag), HttpStatusCode.Conflict, "CONCURRENCY_CONFLICT");

        var stillFailing = await SendAsync(HttpMethod.Post, $"{Requests}/{id}/retry-routing", admin, null, listedETag);
        Assert.Equal(HttpStatusCode.OK, stillFailing.StatusCode);
        var failedBody = await JsonAsync(stillFailing);
        Assert.Equal("SUBMITTED", failedBody.GetProperty("status").GetString());
        Assert.Equal(RoutingFailureCodes.RouteNotFound, failedBody.GetProperty("routingFailureCode").GetString());

        Assert.Equal(HttpStatusCode.Created, (await SendAsync(HttpMethod.Post, Routes, admin, new { requestCategoryCode = "ELECTRICAL", approverRoleCode = "APPROVER" })).StatusCode);
        var routed = await SendAsync(HttpMethod.Post, $"{Requests}/{id}/retry-routing", admin, null, stillFailing.Headers.ETag!.Tag);

        Assert.Equal(HttpStatusCode.OK, routed.StatusCode);
        var routedBody = await JsonAsync(routed);
        Assert.Equal("UNDER_REVIEW", routedBody.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, routedBody.GetProperty("routingFailureCode").ValueKind);
        Assert.Empty((await JsonAsync(await SendAsync(HttpMethod.Get, RoutingIssues, admin))).GetProperty("items").EnumerateArray());
        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{Requests}/{id}/retry-routing", admin, null, routed.Headers.ETag!.Tag),
            HttpStatusCode.Conflict,
            "STATE_CONFLICT");
    }

    // ---------------- Helpers ----------------

    private HttpClient CreateClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

    private async Task<World> WorldAsync()
    {
        var tenantId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();

        var world = await _factory.WithDbAsync(async db =>
        {
            var customer = new Customer(tenantId, "CUST-1");
            var otherCustomer = new Customer(otherTenantId, "CUST-1");
            db.Customers.AddRange(customer, otherCustomer);

            var siteA = new Site(tenantId, customer.Id, "SITE-A");
            var siteB = new Site(tenantId, customer.Id, "SITE-B");
            var otherTenantSite = new Site(otherTenantId, otherCustomer.Id, "SITE-A");
            db.Sites.AddRange(siteA, siteB, otherTenantSite);

            await db.SaveChangesAsync();
            return new World(tenantId, siteA, siteB, otherTenantId, otherTenantSite);
        });

        await using var scope = _factory.Services.CreateAsyncScope();
        var seeder = scope.ServiceProvider.GetRequiredService<RequestLookupSeeder>();
        await seeder.SeedTenantAsync(tenantId, CancellationToken.None);
        await seeder.SeedTenantAsync(otherTenantId, CancellationToken.None);
        return world;
    }

    private async Task<Caller> CallerAsync(Guid tenantId, Site[] assignedSites, params string[] roles)
    {
        var user = await _factory.CreateUserInTenantAsync(tenantId, roles);

        if (assignedSites.Length > 0)
        {
            await _factory.WithDbAsync(db =>
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

    private async Task<(Guid Id, string ETag)> CompleteDraftAsync(Caller owner, Site site)
    {
        var response = await SendAsync(HttpMethod.Post, Requests, owner, new
        {
            siteId = site.Id,
            requestCategoryCode = "ELECTRICAL",
            priorityCode = "HIGH",
            requestContactId = owner.UserId,
            description = "Pump leaking",
            preferredStartAt = "2026-09-20T08:00:00Z",
            preferredEndAt = "2026-09-20T10:00:00Z"
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return ((await JsonAsync(response)).GetProperty("id").GetGuid(), response.Headers.ETag!.Tag);
    }

    private Task AttachAsync(Caller owner, Guid requestId) =>
        _factory.WithDbAsync(async db =>
        {
            var file = new FileAsset(owner.TenantId, "evidence.png", "image/png", 1024, ValidHash, $"{owner.TenantId:N}/{Guid.NewGuid():N}", owner.UserId, DateTime.UtcNow);
            file.MarkClean();
            db.FileAssets.Add(file);
            db.RepairRequestAttachments.Add(new RepairRequestAttachment(requestId, file.Id, AttachmentFileRules.AccessScopeCode));
            await db.SaveChangesAsync();
        });

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, Caller? caller, object? body = null, string? ifMatch = null)
    {
        var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };

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
        return body;
    }
}

internal static class ApprovalRoutingApiFactoryExtensions
{
    public static async Task<T> WithDbAsync<T>(this AuthApiFactory factory, Func<RepairRequestDbContext, Task<T>> action)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<RepairRequestDbContext>());
    }

    public static async Task WithDbAsync(this AuthApiFactory factory, Func<RepairRequestDbContext, Task> action)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<RepairRequestDbContext>());
    }
}
