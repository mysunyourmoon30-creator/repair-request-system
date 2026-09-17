using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RepairRequest.Api.Http;
using RepairRequest.ApiTests.Approvals;
using RepairRequest.Application.Attachments;
using RepairRequest.Application.RepairRequests;
using RepairRequest.Domain.Files;
using RepairRequest.Domain.MasterData;
using RepairRequest.Domain.RepairRequests;
using RepairRequest.Domain.Security;
using RepairRequest.Infrastructure.Identity;
using RepairRequest.Infrastructure.RepairRequests;

namespace RepairRequest.ApiTests.RepairRequests;

/// <summary>API host with its own disposable LocalDB database for the S1-009 Cancel endpoint tests.</summary>
public sealed class RepairRequestCancelApiFactory : AuthApiFactory
{
    public override string ConnectionString =>
        "Server=(localdb)\\MSSQLLocalDB;Database=RepairRequestDb_RepairRequestCancelApiTest;Trusted_Connection=True;TrustServerCertificate=True";
}

/// <summary>
/// RR-API-009 Cancel end to end: only the owning Requester cancels a DRAFT, SUBMITTED, UNDER_REVIEW or APPROVED request with a
/// reason; other roles are 403, other users and tenants 404; stale ETags and REJECTED/CANCELLED requests are 409; a missing
/// reason is 422; no cancel audit is written for any failure (S1-009; ST-RR-007; UC-RR-004).
/// </summary>
public sealed class RepairRequestCancelEndpointsTests : IClassFixture<RepairRequestCancelApiFactory>
{
    private const string Requests = "/api/v1/repair-requests";
    private const string Routes = "/api/v1/approval-routes";
    private const string ValidHash = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";
    private const string AnyETag = "\"AAAAAAAAB9E=\"";

    private readonly RepairRequestCancelApiFactory _factory;

    public RepairRequestCancelEndpointsTests(RepairRequestCancelApiFactory factory)
    {
        _factory = factory;
    }

    private sealed record Caller(Guid UserId, Guid TenantId, string Token);

    private static string Cancel(Guid id) => $"{Requests}/{id}/cancel";

    // ---------------- Success ----------------

    [Fact]
    public async Task Owner_CancelsADraft_Returns200Cancelled_WithANewETag_TheReason_AndOneAudit()
    {
        var tenantId = Guid.NewGuid();
        var site = await SiteAsync(tenantId);
        var owner = await CallerAsync(tenantId, [site], RoleCodes.Requester);
        var (id, etag) = await DraftAsync(owner, site);

        var response = await SendAsync(Cancel(id), owner, new { reason = "  Raised by mistake  " }, etag);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal(id, body.GetProperty("id").GetGuid());
        Assert.Equal("CANCELLED", body.GetProperty("status").GetString());
        Assert.NotEqual(etag, response.Headers.ETag!.Tag);
        Assert.Equal(response.Headers.ETag.Tag, $"\"{body.GetProperty("rowVersion").GetString()}\"");

        var stored = await _factory.WithDbAsync(db => db.RepairRequests.AsNoTracking().SingleAsync(request => request.Id == id));
        Assert.Equal(RepairRequestStatus.Cancelled, stored.Status);
        Assert.Equal("Raised by mistake", stored.CancelReason);
        Assert.Equal(1, await CancelAuditCountAsync(id));

        // CANCELLED is terminal.
        await AssertProblemAsync(await SendAsync(Cancel(id), owner, new { reason = "Again" }, response.Headers.ETag.Tag), HttpStatusCode.Conflict, "STATE_CONFLICT");
        Assert.Equal(1, await CancelAuditCountAsync(id));
    }

    [Fact]
    public async Task Owner_CancelsAnUnderReviewRequest_Returns200_AndTheApprovalRowStaysPending()
    {
        var scenario = await UnderReviewAsync();

        var response = await SendAsync(Cancel(scenario.RequestId), scenario.Owner, new { reason = "Fixed by the site team" }, scenario.ETag);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("CANCELLED", (await JsonAsync(response)).GetProperty("status").GetString());
        Assert.Equal("PENDING", await _factory.WithDbAsync(db => db.RepairRequestApprovals
            .Where(approval => approval.RepairRequestId == scenario.RequestId)
            .Select(approval => approval.Status.ToString().ToUpper())
            .SingleAsync()));

        // The assigned approver can no longer decide a cancelled request.
        await AssertProblemAsync(await SendAsync($"{Requests}/{scenario.RequestId}/approve", scenario.Approver, null, response.Headers.ETag!.Tag), HttpStatusCode.Conflict, ApiProblemResults.StateConflictCode);
    }

    // ---------------- Authorization ----------------

    [Fact]
    public async Task WithoutToken_Returns401()
    {
        await AssertProblemAsync(await SendAsync(Cancel(Guid.NewGuid()), null, new { reason = "x" }, AnyETag), HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
    }

    [Theory]
    [InlineData(RoleCodes.Approver)]
    [InlineData(RoleCodes.Supervisor)]
    [InlineData(RoleCodes.Coordinator)]
    [InlineData(RoleCodes.Administrator)]
    public async Task RolesWithoutRequester_Get403(string role)
    {
        var scenario = await UnderReviewAsync();
        var caller = await CallerAsync(scenario.TenantId, [scenario.Site], role);

        await AssertProblemAsync(await SendAsync(Cancel(scenario.RequestId), caller, new { reason = "No" }, scenario.ETag), HttpStatusCode.Forbidden, "ACCESS_DENIED");
        await AssertUnchangedAsync(scenario.RequestId, RepairRequestStatus.UnderReview);
    }

    [Fact]
    public async Task AnotherRequesterOfTheSite_AMultiRoleNonOwner_AndAnotherTenant_Get404()
    {
        var scenario = await UnderReviewAsync();
        var otherRequester = await CallerAsync(scenario.TenantId, [scenario.Site], RoleCodes.Requester);
        var multiRole = await CallerAsync(scenario.TenantId, [scenario.Site], RoleCodes.Requester, RoleCodes.Supervisor);
        var otherTenantId = Guid.NewGuid();
        var foreign = await CallerAsync(otherTenantId, [await SiteAsync(otherTenantId)], RoleCodes.Requester);

        foreach (var caller in new[] { otherRequester, multiRole, foreign })
        {
            await AssertProblemAsync(await SendAsync(Cancel(scenario.RequestId), caller, new { reason = "No" }, scenario.ETag), HttpStatusCode.NotFound, "NOT_FOUND");
        }

        await AssertUnchangedAsync(scenario.RequestId, RepairRequestStatus.UnderReview);
    }

    // ---------------- Request contract and state ----------------

    [Fact]
    public async Task MissingIfMatch_Returns400_StaleETag_Returns409_AndInvalidReason_Returns422()
    {
        var scenario = await UnderReviewAsync();

        await AssertProblemAsync(await SendAsync(Cancel(scenario.RequestId), scenario.Owner, new { reason = "No" }, null), HttpStatusCode.BadRequest, "BAD_REQUEST");
        await AssertProblemAsync(await SendAsync(Cancel(scenario.RequestId), scenario.Owner, new { reason = "No" }, AnyETag), HttpStatusCode.Conflict, "CONCURRENCY_CONFLICT");

        foreach (var body in new object?[] { null, new { }, new { reason = "  " }, new { reason = new string('R', 1001) } })
        {
            var problem = await AssertProblemAsync(await SendAsync(Cancel(scenario.RequestId), scenario.Owner, body, scenario.ETag), HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
            Assert.True(problem.GetProperty("errors").TryGetProperty("reason", out _));
        }

        await AssertUnchangedAsync(scenario.RequestId, RepairRequestStatus.UnderReview);
    }

    [Fact]
    public async Task RejectedRequest_Returns409StateConflict()
    {
        var scenario = await UnderReviewAsync();
        var rejected = await SendAsync($"{Requests}/{scenario.RequestId}/reject", scenario.Approver, new { reason = "Out of warranty" }, scenario.ETag);
        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);

        await AssertProblemAsync(await SendAsync(Cancel(scenario.RequestId), scenario.Owner, new { reason = "No" }, rejected.Headers.ETag!.Tag), HttpStatusCode.Conflict, "STATE_CONFLICT");
        await AssertUnchangedAsync(scenario.RequestId, RepairRequestStatus.Rejected);
    }

    // ---------------- Helpers ----------------

    private sealed record Scenario(Guid TenantId, Site Site, Caller Owner, Caller Approver, Guid RequestId, string ETag);

    /// <summary>Tenant + Site + Site route + one approver; the owner's request is submitted and routed to UNDER_REVIEW.</summary>
    private async Task<Scenario> UnderReviewAsync()
    {
        var tenantId = Guid.NewGuid();
        var site = await SiteAsync(tenantId);
        var admin = await CallerAsync(tenantId, [], RoleCodes.Administrator);
        var owner = await CallerAsync(tenantId, [site], RoleCodes.Requester);
        var approver = await CallerAsync(tenantId, [site], RoleCodes.Approver);
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(Routes, admin, new { requestCategoryCode = "ELECTRICAL", siteId = site.Id, approverRoleCode = "APPROVER" }, null)).StatusCode);

        var (id, draftETag) = await DraftAsync(owner, site);
        await _factory.WithDbAsync(async db =>
        {
            var file = new FileAsset(tenantId, "evidence.png", "image/png", 1024, ValidHash, $"{tenantId:N}/{Guid.NewGuid():N}", owner.UserId, DateTime.UtcNow);
            file.MarkClean();
            db.FileAssets.Add(file);
            db.RepairRequestAttachments.Add(new RepairRequestAttachment(id, file.Id, AttachmentFileRules.AccessScopeCode));
            await db.SaveChangesAsync();
        });

        var submitted = await SendAsync($"{Requests}/{id}/submit", owner, new { }, draftETag);
        Assert.Equal("UNDER_REVIEW", (await JsonAsync(submitted)).GetProperty("status").GetString());
        return new Scenario(tenantId, site, owner, approver, id, submitted.Headers.ETag!.Tag);
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
        var site = await _factory.WithDbAsync(async db =>
        {
            var customer = new Customer(tenantId, "CUST-1");
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
            await _factory.WithDbAsync(db =>
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

    private Task<int> CancelAuditCountAsync(Guid requestId) =>
        _factory.WithDbAsync(db => db.AuditHistory.CountAsync(audit => audit.EntityId == requestId && audit.ActionCode == RepairRequestAudit.CancelledAction));

    private async Task AssertUnchangedAsync(Guid requestId, RepairRequestStatus status)
    {
        var stored = await _factory.WithDbAsync(db => db.RepairRequests.AsNoTracking().SingleAsync(request => request.Id == requestId));
        Assert.Equal(status, stored.Status);
        Assert.Null(stored.CancelReason);
        Assert.Equal(0, await CancelAuditCountAsync(requestId));
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
