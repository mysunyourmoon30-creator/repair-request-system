using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RepairRequest.Api.Http;
using RepairRequest.Application.Attachments;
using RepairRequest.Application.RepairRequests;
using RepairRequest.Domain.Files;
using RepairRequest.Domain.MasterData;
using RepairRequest.Domain.RepairRequests;
using RepairRequest.Domain.Security;
using RepairRequest.Domain.WorkOrders;
using RepairRequest.Infrastructure.Identity;
using RepairRequest.Infrastructure.Persistence;
using RepairRequest.Infrastructure.RepairRequests;

namespace RepairRequest.ApiTests.RepairRequests;

/// <summary>API host with its own disposable LocalDB database for the S2-002 Convert endpoint tests.</summary>
public sealed class RepairRequestConvertApiFactory : AuthApiFactory
{
    public override string ConnectionString =>
        "Server=(localdb)\\MSSQLLocalDB;Database=RepairRequestDb_RepairRequestConvertApiTest;Trusted_Connection=True;TrustServerCertificate=True";
}

/// <summary>
/// RR-API-010 Convert end to end (ST-RR-008; UC-WO-001; BR-03; S2-002): only a Coordinator within the request's Site scope
/// converts an APPROVED request; other roles are 403; out-of-scope/other-tenant callers are 404; any non-APPROVED source
/// state (including an already-CONVERTED request) is 409; stale ETags are 409; success creates exactly one Work Order
/// (OPEN, WO-{yyyy}-000001) and writes one audit record; a failure at save time rolls back everything.
/// </summary>
public sealed class RepairRequestConvertEndpointsTests : IClassFixture<RepairRequestConvertApiFactory>
{
    private const string Requests = "/api/v1/repair-requests";
    private const string Routes = "/api/v1/approval-routes";
    private const string WorkOrders = "/api/v1/work-orders";
    private const string ValidHash = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";
    private const string AnyETag = "\"AAAAAAAAB9E=\"";

    private readonly RepairRequestConvertApiFactory _factory;

    public RepairRequestConvertEndpointsTests(RepairRequestConvertApiFactory factory)
    {
        _factory = factory;
    }

    private sealed record Caller(Guid UserId, Guid TenantId, string Token);

    private static string Convert(Guid id) => $"{Requests}/{id}/convert-to-work-order";

    // ---------------- Success ----------------

    [Fact]
    public async Task Coordinator_ConvertsAnApprovedRequest_Returns200WithTheNewWorkOrder_AndOneAudit_AndIsThenTerminal()
    {
        var scenario = await ApprovedAsync();

        var response = await SendAsync(Convert(scenario.RequestId), scenario.Coordinator, scenario.ETag);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal(scenario.RequestId, body.GetProperty("repairRequestId").GetGuid());
        Assert.Equal("OPEN", body.GetProperty("status").GetString());
        var workOrderNo = body.GetProperty("workOrderNo").GetString();
        Assert.Equal($"WO-{DateTime.UtcNow.Year}-000001", workOrderNo);
        Assert.Equal(response.Headers.ETag!.Tag, $"\"{body.GetProperty("rowVersion").GetString()}\"");

        var storedRequest = await WithDbAsync(db => db.RepairRequests.AsNoTracking().SingleAsync(request => request.Id == scenario.RequestId));
        Assert.Equal(RepairRequestStatus.Converted, storedRequest.Status);

        var workOrderCount = await WithDbAsync(db => db.WorkOrders.CountAsync(wo => wo.RepairRequestId == scenario.RequestId));
        Assert.Equal(1, workOrderCount);
        Assert.Equal(1, await ConvertedAuditCountAsync(scenario.RequestId));

        // Fetching the returned Work Order id through the real S2-001 detail endpoint confirms the client can open it.
        var detail = await SendGetAsync($"{WorkOrders}/{body.GetProperty("workOrderId").GetGuid()}", scenario.Coordinator);
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        Assert.Equal(workOrderNo, (await JsonAsync(detail)).GetProperty("workOrderNo").GetString());

        // CONVERTED is terminal: converting again is denied, and no second Work Order or audit row is written.
        // The Convert response's ETag is the new Work Order's, not the Repair Request's, so re-fetch the request's own
        // current ETag first: otherwise the RowVersion guard (not the state guard) would trip instead.
        var converted = await SendGetAsync($"{Requests}/{scenario.RequestId}", scenario.Coordinator);
        await AssertProblemAsync(await SendAsync(Convert(scenario.RequestId), scenario.Coordinator, converted.Headers.ETag!.Tag), HttpStatusCode.Conflict, "STATE_CONFLICT");
        Assert.Equal(1, await WithDbAsync(db => db.WorkOrders.CountAsync(wo => wo.RepairRequestId == scenario.RequestId)));
        Assert.Equal(1, await ConvertedAuditCountAsync(scenario.RequestId));
    }

    // ---------------- Authorization ----------------

    [Fact]
    public async Task WithoutToken_Returns401()
    {
        await AssertProblemAsync(await SendAsync(Convert(Guid.NewGuid()), null, AnyETag), HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
    }

    [Theory]
    [InlineData(RoleCodes.Requester)]
    [InlineData(RoleCodes.Approver)]
    [InlineData(RoleCodes.Technician)]
    [InlineData(RoleCodes.TeamLead)]
    [InlineData(RoleCodes.Supervisor)]
    [InlineData(RoleCodes.Administrator)]
    public async Task RolesWithoutCoordinator_Get403(string role)
    {
        var scenario = await ApprovedAsync();
        var caller = await CallerAsync(scenario.TenantId, [scenario.Site], role);

        await AssertProblemAsync(await SendAsync(Convert(scenario.RequestId), caller, scenario.ETag), HttpStatusCode.Forbidden, "ACCESS_DENIED");
        await AssertUnchangedAsync(scenario.RequestId, RepairRequestStatus.Approved);
    }

    [Fact]
    public async Task CoordinatorOutOfSiteScope_AndAnotherTenant_Get404()
    {
        var scenario = await ApprovedAsync();
        var otherSiteCoordinator = await CallerAsync(scenario.TenantId, [await SiteAsync(scenario.TenantId)], RoleCodes.Coordinator);
        var otherTenantId = Guid.NewGuid();
        var foreign = await CallerAsync(otherTenantId, [await SiteAsync(otherTenantId)], RoleCodes.Coordinator);

        foreach (var caller in new[] { otherSiteCoordinator, foreign })
        {
            await AssertProblemAsync(await SendAsync(Convert(scenario.RequestId), caller, scenario.ETag), HttpStatusCode.NotFound, "NOT_FOUND");
        }

        await AssertUnchangedAsync(scenario.RequestId, RepairRequestStatus.Approved);
    }

    // ---------------- Request contract and state ----------------

    [Fact]
    public async Task MissingIfMatch_Returns400_AndStaleETag_Returns409()
    {
        var scenario = await ApprovedAsync();

        await AssertProblemAsync(await SendAsync(Convert(scenario.RequestId), scenario.Coordinator, null), HttpStatusCode.BadRequest, "BAD_REQUEST");
        await AssertProblemAsync(await SendAsync(Convert(scenario.RequestId), scenario.Coordinator, AnyETag), HttpStatusCode.Conflict, "CONCURRENCY_CONFLICT");

        await AssertUnchangedAsync(scenario.RequestId, RepairRequestStatus.Approved);
    }

    [Fact]
    public async Task UnderReviewRequest_Returns409StateConflict()
    {
        var scenario = await UnderReviewAsync();

        await AssertProblemAsync(await SendAsync(Convert(scenario.RequestId), scenario.Coordinator, scenario.ETag), HttpStatusCode.Conflict, "STATE_CONFLICT");
        await AssertUnchangedAsync(scenario.RequestId, RepairRequestStatus.UnderReview);
    }

    [Fact]
    public async Task RejectedRequest_Returns409StateConflict()
    {
        var scenario = await UnderReviewAsync();
        var rejected = await SendAsync($"{Requests}/{scenario.RequestId}/reject", scenario.Approver, new { reason = "Out of warranty" }, scenario.ETag);
        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);

        await AssertProblemAsync(await SendAsync(Convert(scenario.RequestId), scenario.Coordinator, rejected.Headers.ETag!.Tag), HttpStatusCode.Conflict, "STATE_CONFLICT");
        await AssertUnchangedAsync(scenario.RequestId, RepairRequestStatus.Rejected);
    }

    // ---------------- Rollback on failed creation ----------------

    [Fact]
    public async Task WhenAWorkOrderAlreadyExistsForTheRequest_ConvertFails_AndRollsBackEverything()
    {
        var scenario = await ApprovedAsync();

        // Simulate a Work Order that already exists for this Repair Request (WO-004's unique FK), bypassing Convert
        // entirely, so the RowVersion/state guards still pass and only the database's uniqueness backstop fires.
        await WithDbAsync(async db =>
        {
            db.WorkOrders.Add(WorkOrder.Create(scenario.TenantId, scenario.RequestId, "WO-PRESEEDED", DateTime.UtcNow));
            await db.SaveChangesAsync();
        });

        await AssertProblemAsync(await SendAsync(Convert(scenario.RequestId), scenario.Coordinator, scenario.ETag), HttpStatusCode.Conflict, "CONCURRENCY_CONFLICT");

        var storedRequest = await WithDbAsync(db => db.RepairRequests.AsNoTracking().SingleAsync(request => request.Id == scenario.RequestId));
        Assert.Equal(RepairRequestStatus.Approved, storedRequest.Status);
        Assert.Equal(1, await WithDbAsync(db => db.WorkOrders.CountAsync(wo => wo.RepairRequestId == scenario.RequestId)));
        Assert.Equal(0, await ConvertedAuditCountAsync(scenario.RequestId));
    }

    // ---------------- Concurrency ----------------

    [Fact]
    public async Task TwoConcurrentConvertsOfTheSameApprovedRequest_ExactlyOneSucceeds_WithOneWorkOrderAndOneAudit()
    {
        var scenario = await ApprovedAsync();

        var responses = await Task.WhenAll(
            Task.Run(() => SendAsync(Convert(scenario.RequestId), scenario.Coordinator, scenario.ETag)),
            Task.Run(() => SendAsync(Convert(scenario.RequestId), scenario.Coordinator, scenario.ETag)));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);

        var storedRequest = await WithDbAsync(db => db.RepairRequests.AsNoTracking().SingleAsync(request => request.Id == scenario.RequestId));
        Assert.Equal(RepairRequestStatus.Converted, storedRequest.Status);
        Assert.Equal(1, await WithDbAsync(db => db.WorkOrders.CountAsync(wo => wo.RepairRequestId == scenario.RequestId)));
        Assert.Equal(1, await ConvertedAuditCountAsync(scenario.RequestId));
    }

    [Fact]
    public async Task ConcurrentConvertsOfDifferentApprovedRequestsInTheSameTenant_AllocateDistinctGaplessWorkOrderNumbers()
    {
        var world = await ApprovedAsync();
        var scenarios = new List<(Guid RequestId, string ETag)> { (world.RequestId, world.ETag) };
        for (var index = 0; index < 4; index++)
        {
            scenarios.Add(await AnotherApprovedRequestAsync(world));
        }

        var responses = await Task.WhenAll(scenarios.Select(scenario =>
            Task.Run(() => SendAsync(Convert(scenario.RequestId), world.Coordinator, scenario.ETag))));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));

        var numbers = new List<string>();
        foreach (var response in responses)
        {
            numbers.Add((await JsonAsync(response)).GetProperty("workOrderNo").GetString()!);
        }

        Assert.Equal(
            Enumerable.Range(1, scenarios.Count).Select(sequence => $"WO-{DateTime.UtcNow.Year}-{sequence:D6}").Order(),
            numbers.Order());
    }

    // ---------------- Helpers ----------------

    private sealed record Scenario(Guid TenantId, Site Site, Caller Owner, Caller Approver, Caller Coordinator, Guid RequestId, string ETag);

    /// <summary>An UNDER_REVIEW request plus a Site-scoped Coordinator, not yet decided.</summary>
    private async Task<Scenario> UnderReviewAsync()
    {
        var tenantId = Guid.NewGuid();
        var site = await SiteAsync(tenantId);
        var admin = await CallerAsync(tenantId, [], RoleCodes.Administrator);
        var owner = await CallerAsync(tenantId, [site], RoleCodes.Requester);
        var approver = await CallerAsync(tenantId, [site], RoleCodes.Approver);
        var coordinator = await CallerAsync(tenantId, [site], RoleCodes.Coordinator);
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
        Assert.Equal("UNDER_REVIEW", (await JsonAsync(submitted)).GetProperty("status").GetString());
        return new Scenario(tenantId, site, owner, approver, coordinator, id, submitted.Headers.ETag!.Tag);
    }

    /// <summary>An APPROVED request plus a Site-scoped Coordinator, ready to convert.</summary>
    private async Task<Scenario> ApprovedAsync()
    {
        var underReview = await UnderReviewAsync();
        var approved = await SendAsync($"{Requests}/{underReview.RequestId}/approve", underReview.Approver, null, underReview.ETag);
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        Assert.Equal("APPROVED", (await JsonAsync(approved)).GetProperty("status").GetString());
        return underReview with { ETag = approved.Headers.ETag!.Tag };
    }

    /// <summary>
    /// A further APPROVED request in an existing Scenario's tenant/Site, for testing concurrent Converts of
    /// different requests. Always sends a continuation reason: Submit's 24-hour duplicate window (BR-14) would
    /// otherwise block every request after the first for the same Site/Category/Equipment; a reason sent when
    /// there is in fact no duplicate is simply ignored (RepairRequestSubmitService).
    /// </summary>
    private async Task<(Guid RequestId, string ETag)> AnotherApprovedRequestAsync(Scenario world)
    {
        var (id, draftETag) = await DraftAsync(world.Owner, world.Site);
        await WithDbAsync(async db =>
        {
            var file = new FileAsset(world.TenantId, "evidence.png", "image/png", 1024, ValidHash, $"{world.TenantId:N}/{Guid.NewGuid():N}", world.Owner.UserId, DateTime.UtcNow);
            file.MarkClean();
            db.FileAssets.Add(file);
            db.RepairRequestAttachments.Add(new RepairRequestAttachment(id, file.Id, AttachmentFileRules.AccessScopeCode));
            await db.SaveChangesAsync();
        });

        var submitted = await SendAsync(
            $"{Requests}/{id}/submit", world.Owner, new { duplicateContinuationReason = "Concurrency test fixture" }, draftETag);
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);

        var approved = await SendAsync($"{Requests}/{id}/approve", world.Approver, null, submitted.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        return (id, approved.Headers.ETag!.Tag);
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

    private Task<int> ConvertedAuditCountAsync(Guid requestId) =>
        WithDbAsync(db => db.AuditHistory.CountAsync(audit => audit.EntityId == requestId && audit.ActionCode == RepairRequestAudit.ConvertedAction));

    private async Task AssertUnchangedAsync(Guid requestId, RepairRequestStatus status)
    {
        var stored = await WithDbAsync(db => db.RepairRequests.AsNoTracking().SingleAsync(request => request.Id == requestId));
        Assert.Equal(status, stored.Status);
        Assert.Equal(0, await WithDbAsync(db => db.WorkOrders.CountAsync(wo => wo.RepairRequestId == requestId)));
        Assert.Equal(0, await ConvertedAuditCountAsync(requestId));
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

    /// <summary>Convert takes no body; If-Match is the only required header.</summary>
    private Task<HttpResponseMessage> SendAsync(string url, Caller? caller, string? ifMatch) =>
        SendAsync(url, caller, body: null, ifMatch);

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
