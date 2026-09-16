using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RepairRequest.Application.Attachments;
using RepairRequest.Application.RepairRequests;
using RepairRequest.Domain.Approvals;
using RepairRequest.Domain.Files;
using RepairRequest.Domain.MasterData;
using RepairRequest.Domain.RepairRequests;
using RepairRequest.Domain.Security;
using RepairRequest.Infrastructure.Identity;
using RepairRequest.Infrastructure.RepairRequests;

namespace RepairRequest.ApiTests.Approvals;

/// <summary>API host with its own disposable LocalDB database for the S1-008 Approve/Reject endpoint tests.</summary>
public sealed class RepairRequestDecisionApiFactory : AuthApiFactory
{
    public override string ConnectionString =>
        "Server=(localdb)\\MSSQLLocalDB;Database=RepairRequestDb_RepairRequestDecisionApiTest;Trusted_Connection=True;TrustServerCertificate=True";
}

/// <summary>
/// RR-API-006 Approve and RR-API-007 Reject end to end: the assigned approver decides an UNDER_REVIEW request; role, assignment
/// and self-decision denials are 403 ACCESS_DENIED; other tenants are 404; stale ETags and non-UNDER_REVIEW states are 409;
/// a missing reject reason is 422; no decision audit is written for any failure (S1-008; DEC-PRE-S1-008-01/03).
/// </summary>
public sealed class RepairRequestDecisionEndpointsTests : IClassFixture<RepairRequestDecisionApiFactory>
{
    private const string Requests = "/api/v1/repair-requests";
    private const string Routes = "/api/v1/approval-routes";
    private const string ValidHash = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";
    private const string AnyETag = "\"AAAAAAAAB9E=\"";

    private readonly RepairRequestDecisionApiFactory _factory;

    public RepairRequestDecisionEndpointsTests(RepairRequestDecisionApiFactory factory)
    {
        _factory = factory;
    }

    private sealed record Caller(Guid UserId, Guid TenantId, string Token);

    private sealed record Scenario(Guid TenantId, Site Site, Caller Admin, Caller Requester, Caller Approver, Guid RequestId, string ETag);

    // ---------------- Success ----------------

    [Fact]
    public async Task Approve_ByTheAssignedApprover_Returns200Approved_WithANewETag_AndAudits()
    {
        var scenario = await UnderReviewAsync();

        var response = await SendAsync(HttpMethod.Post, Approve(scenario), scenario.Approver, null, scenario.ETag);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal(scenario.RequestId, body.GetProperty("id").GetGuid());
        Assert.Equal("APPROVED", body.GetProperty("status").GetString());
        Assert.StartsWith("RR-", body.GetProperty("requestNo").GetString());
        Assert.NotEqual(scenario.ETag, response.Headers.ETag!.Tag);
        Assert.Equal(response.Headers.ETag.Tag, $"\"{body.GetProperty("rowVersion").GetString()}\"");

        Assert.Equal(RepairRequestStatus.Approved, await StatusAsync(scenario.RequestId));
        Assert.Equal(ApprovalStatus.Approved, await StepStatusAsync(scenario.RequestId));
        Assert.Equal((1, 0), await DecisionAuditCountsAsync(scenario.RequestId));

        // APPROVED is final for S1-008 decisions.
        await AssertProblemAsync(await SendAsync(HttpMethod.Post, Approve(scenario), scenario.Approver, null, response.Headers.ETag.Tag), HttpStatusCode.Conflict, "STATE_CONFLICT");
        await AssertProblemAsync(await SendAsync(HttpMethod.Post, Reject(scenario), scenario.Approver, new { reason = "Too late" }, response.Headers.ETag.Tag), HttpStatusCode.Conflict, "STATE_CONFLICT");
        Assert.Equal((1, 0), await DecisionAuditCountsAsync(scenario.RequestId));
    }

    [Fact]
    public async Task Reject_ByTheAssignedApprover_Returns200Rejected_WithTheReasonStored()
    {
        var scenario = await UnderReviewAsync();

        var response = await SendAsync(HttpMethod.Post, Reject(scenario), scenario.Approver, new { reason = "  Covered by the service contract  " }, scenario.ETag);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("REJECTED", (await JsonAsync(response)).GetProperty("status").GetString());
        var (status, rejectReason) = await _factory.WithDbAsync(db => db.RepairRequests
            .Where(request => request.Id == scenario.RequestId)
            .Select(request => new ValueTuple<RepairRequestStatus, string?>(request.Status, request.RejectReason))
            .SingleAsync());
        Assert.Equal(RepairRequestStatus.Rejected, status);
        Assert.Equal("Covered by the service contract", rejectReason);
        Assert.Equal(ApprovalStatus.Rejected, await StepStatusAsync(scenario.RequestId));
        Assert.Equal((0, 1), await DecisionAuditCountsAsync(scenario.RequestId));

        await AssertProblemAsync(await SendAsync(HttpMethod.Post, Approve(scenario), scenario.Approver, null, response.Headers.ETag!.Tag), HttpStatusCode.Conflict, "STATE_CONFLICT");
        await AssertProblemAsync(await SendAsync(HttpMethod.Post, Reject(scenario), scenario.Approver, new { reason = "Again" }, response.Headers.ETag.Tag), HttpStatusCode.Conflict, "STATE_CONFLICT");
        Assert.Equal((0, 1), await DecisionAuditCountsAsync(scenario.RequestId));
    }

    // ---------------- Authorization ----------------

    [Fact]
    public async Task WithoutToken_Returns401()
    {
        var id = Guid.NewGuid();

        await AssertProblemAsync(await SendAsync(HttpMethod.Post, $"{Requests}/{id}/approve", null, null, AnyETag), HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
        await AssertProblemAsync(await SendAsync(HttpMethod.Post, $"{Requests}/{id}/reject", null, new { reason = "x" }, AnyETag), HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
    }

    [Theory]
    [InlineData(RoleCodes.Requester)]
    [InlineData(RoleCodes.Supervisor)]
    [InlineData(RoleCodes.Administrator)]
    public async Task RolesWithoutApprover_Get403(string role)
    {
        var scenario = await UnderReviewAsync();
        var caller = await CallerAsync(scenario.TenantId, [scenario.Site], role);

        await AssertProblemAsync(await SendAsync(HttpMethod.Post, Approve(scenario), caller, null, scenario.ETag), HttpStatusCode.Forbidden, "ACCESS_DENIED");
        await AssertProblemAsync(await SendAsync(HttpMethod.Post, Reject(scenario), caller, new { reason = "No" }, scenario.ETag), HttpStatusCode.Forbidden, "ACCESS_DENIED");
        await AssertUndecidedAsync(scenario);
    }

    [Fact]
    public async Task ApproverNotAssigned_AndAdministratorPlusApproverNotAssigned_Get403()
    {
        var scenario = await UnderReviewAsync();
        var otherApprover = await CallerAsync(scenario.TenantId, [scenario.Site], RoleCodes.Approver);
        var adminApprover = await CallerAsync(scenario.TenantId, [scenario.Site], RoleCodes.Administrator, RoleCodes.Approver);

        await AssertProblemAsync(await SendAsync(HttpMethod.Post, Approve(scenario), otherApprover, null, scenario.ETag), HttpStatusCode.Forbidden, "ACCESS_DENIED");
        await AssertProblemAsync(await SendAsync(HttpMethod.Post, Reject(scenario), adminApprover, new { reason = "No" }, scenario.ETag), HttpStatusCode.Forbidden, "ACCESS_DENIED");
        await AssertUndecidedAsync(scenario);
    }

    [Fact]
    public async Task RequesterHoldingApprover_ApprovingOrRejectingTheirOwnRequest_Gets403()
    {
        var scenario = await UnderReviewAsync(RoleCodes.Requester, RoleCodes.Approver);

        await AssertProblemAsync(await SendAsync(HttpMethod.Post, Approve(scenario), scenario.Requester, null, scenario.ETag), HttpStatusCode.Forbidden, "ACCESS_DENIED");
        await AssertProblemAsync(await SendAsync(HttpMethod.Post, Reject(scenario), scenario.Requester, new { reason = "Mine" }, scenario.ETag), HttpStatusCode.Forbidden, "ACCESS_DENIED");
        await AssertUndecidedAsync(scenario);

        // The assigned approver (another user) can decide the same request.
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Post, Approve(scenario), scenario.Approver, null, scenario.ETag)).StatusCode);
    }

    [Fact]
    public async Task ApproverOfAnotherTenant_Gets404()
    {
        var scenario = await UnderReviewAsync();
        var otherTenantId = Guid.NewGuid();
        var otherSite = await SiteAsync(otherTenantId);
        var foreign = await CallerAsync(otherTenantId, [otherSite], RoleCodes.Approver);

        await AssertProblemAsync(await SendAsync(HttpMethod.Post, Approve(scenario), foreign, null, scenario.ETag), HttpStatusCode.NotFound, "NOT_FOUND");
        await AssertProblemAsync(await SendAsync(HttpMethod.Post, Reject(scenario), foreign, new { reason = "No" }, scenario.ETag), HttpStatusCode.NotFound, "NOT_FOUND");
        await AssertUndecidedAsync(scenario);
    }

    // ---------------- Request contract ----------------

    [Fact]
    public async Task MissingIfMatch_Returns400_AndStaleETag_Returns409ConcurrencyConflict()
    {
        var scenario = await UnderReviewAsync();

        await AssertProblemAsync(await SendAsync(HttpMethod.Post, Approve(scenario), scenario.Approver, null, null), HttpStatusCode.BadRequest, "BAD_REQUEST");
        await AssertProblemAsync(await SendAsync(HttpMethod.Post, Reject(scenario), scenario.Approver, new { reason = "No" }, null), HttpStatusCode.BadRequest, "BAD_REQUEST");
        await AssertProblemAsync(await SendAsync(HttpMethod.Post, Approve(scenario), scenario.Approver, null, AnyETag), HttpStatusCode.Conflict, "CONCURRENCY_CONFLICT");
        await AssertProblemAsync(await SendAsync(HttpMethod.Post, Reject(scenario), scenario.Approver, new { reason = "No" }, AnyETag), HttpStatusCode.Conflict, "CONCURRENCY_CONFLICT");
        await AssertUndecidedAsync(scenario);
    }

    [Fact]
    public async Task Reject_WithoutAValidReason_Returns422OnReason()
    {
        var scenario = await UnderReviewAsync();

        foreach (var body in new object?[] { null, new { }, new { reason = "   " }, new { reason = new string('R', 1001) } })
        {
            var problem = await AssertProblemAsync(await SendAsync(HttpMethod.Post, Reject(scenario), scenario.Approver, body, scenario.ETag), HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
            Assert.True(problem.GetProperty("errors").TryGetProperty("reason", out _));
        }

        await AssertUndecidedAsync(scenario);
    }

    [Fact]
    public async Task SubmittedRequestThatWasNeverRouted_Returns409StateConflict()
    {
        var tenantId = Guid.NewGuid();
        var site = await SiteAsync(tenantId);
        var requester = await CallerAsync(tenantId, [site], RoleCodes.Requester);
        var approver = await CallerAsync(tenantId, [site], RoleCodes.Approver);
        var (requestId, etag) = await SubmitAsync(requester, site);

        await AssertProblemAsync(await SendAsync(HttpMethod.Post, $"{Requests}/{requestId}/approve", approver, null, etag), HttpStatusCode.Conflict, "STATE_CONFLICT");
        await AssertProblemAsync(await SendAsync(HttpMethod.Post, $"{Requests}/{requestId}/reject", approver, new { reason = "No" }, etag), HttpStatusCode.Conflict, "STATE_CONFLICT");
        Assert.Equal(RepairRequestStatus.Submitted, await StatusAsync(requestId));
        Assert.Equal((0, 0), await DecisionAuditCountsAsync(requestId));
    }

    // ---------------- Helpers ----------------

    private static string Approve(Scenario scenario) => $"{Requests}/{scenario.RequestId}/approve";

    private static string Reject(Scenario scenario) => $"{Requests}/{scenario.RequestId}/reject";

    /// <summary>Tenant + Site + Site route + one approver; the request is submitted and routed to UNDER_REVIEW.</summary>
    private async Task<Scenario> UnderReviewAsync(params string[] requesterRoles)
    {
        var tenantId = Guid.NewGuid();
        var site = await SiteAsync(tenantId);
        var admin = await CallerAsync(tenantId, [], RoleCodes.Administrator);
        var requester = await CallerAsync(tenantId, [site], requesterRoles.Length == 0 ? [RoleCodes.Requester] : requesterRoles);
        var approver = await CallerAsync(tenantId, [site], RoleCodes.Approver);

        var route = await SendAsync(HttpMethod.Post, Routes, admin, new { requestCategoryCode = "ELECTRICAL", siteId = site.Id, approverRoleCode = "APPROVER" }, null);
        Assert.Equal(HttpStatusCode.Created, route.StatusCode);

        var (requestId, etag) = await SubmitAsync(requester, site);
        Assert.Equal(RepairRequestStatus.UnderReview, await StatusAsync(requestId));
        return new Scenario(tenantId, site, admin, requester, approver, requestId, etag);
    }

    private async Task<(Guid Id, string ETag)> SubmitAsync(Caller owner, Site site)
    {
        var created = await SendAsync(HttpMethod.Post, Requests, owner, new
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
        var id = (await JsonAsync(created)).GetProperty("id").GetGuid();

        await _factory.WithDbAsync(async db =>
        {
            var file = new FileAsset(owner.TenantId, "evidence.png", "image/png", 1024, ValidHash, $"{owner.TenantId:N}/{Guid.NewGuid():N}", owner.UserId, DateTime.UtcNow);
            file.MarkClean();
            db.FileAssets.Add(file);
            db.RepairRequestAttachments.Add(new RepairRequestAttachment(id, file.Id, AttachmentFileRules.AccessScopeCode));
            await db.SaveChangesAsync();
        });

        var submitted = await SendAsync(HttpMethod.Post, $"{Requests}/{id}/submit", owner, new { }, created.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
        return (id, submitted.Headers.ETag!.Tag);
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

    private Task<RepairRequestStatus> StatusAsync(Guid requestId) =>
        _factory.WithDbAsync(db => db.RepairRequests.Where(request => request.Id == requestId).Select(request => request.Status).SingleAsync());

    private Task<ApprovalStatus> StepStatusAsync(Guid requestId) =>
        _factory.WithDbAsync(db => db.RepairRequestApprovals.Where(approval => approval.RepairRequestId == requestId).Select(approval => approval.Status).SingleAsync());

    private async Task<(int Approved, int Rejected)> DecisionAuditCountsAsync(Guid requestId) =>
        (await _factory.WithDbAsync(db => db.AuditHistory.CountAsync(audit => audit.EntityId == requestId && audit.ActionCode == RepairRequestAudit.ApprovedAction)),
         await _factory.WithDbAsync(db => db.AuditHistory.CountAsync(audit => audit.EntityId == requestId && audit.ActionCode == RepairRequestAudit.RejectedAction)));

    private async Task AssertUndecidedAsync(Scenario scenario)
    {
        Assert.Equal(RepairRequestStatus.UnderReview, await StatusAsync(scenario.RequestId));
        Assert.Equal(ApprovalStatus.Pending, await StepStatusAsync(scenario.RequestId));
        Assert.Equal((0, 0), await DecisionAuditCountsAsync(scenario.RequestId));
    }

    private HttpClient CreateClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, Caller? caller, object? body, string? ifMatch)
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
        Assert.True(body.TryGetProperty("correlationId", out _));
        return body;
    }
}
