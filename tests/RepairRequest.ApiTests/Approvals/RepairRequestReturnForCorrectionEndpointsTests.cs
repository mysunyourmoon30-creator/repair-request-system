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

/// <summary>API host with its own disposable LocalDB database for the S1-010 Return for Correction / Resubmit tests.</summary>
public sealed class RepairRequestReturnApiFactory : AuthApiFactory
{
    public override string ConnectionString =>
        "Server=(localdb)\\MSSQLLocalDB;Database=RepairRequestDb_RepairRequestReturnApiTest;Trusted_Connection=True;TrustServerCertificate=True";
}

/// <summary>
/// RR-API-008 Return for Correction end to end, plus Resubmit through RR-API-005 (S1-010; ST-RR-006; DEC-PRE-S1-010-01..04):
/// only the assigned approver of an UNDER_REVIEW request returns it with a reason; other roles are 403, the owner and
/// unassigned approvers 403, out-of-scope callers 404; stale ETags and other states are 409; a missing reason is 422. The
/// owner can then edit and resubmit the DRAFT, which keeps its Request No and submittedAt and is routed again.
/// </summary>
public sealed class RepairRequestReturnForCorrectionEndpointsTests : IClassFixture<RepairRequestReturnApiFactory>
{
    private const string Requests = "/api/v1/repair-requests";
    private const string Routes = "/api/v1/approval-routes";
    private const string ValidHash = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";
    private const string AnyETag = "\"AAAAAAAAB9E=\"";
    private const string Reason = "Photo does not show the fault";

    private readonly RepairRequestReturnApiFactory _factory;

    public RepairRequestReturnForCorrectionEndpointsTests(RepairRequestReturnApiFactory factory)
    {
        _factory = factory;
    }

    private sealed record Caller(Guid UserId, Guid TenantId, string Token);

    private sealed record Scenario(Guid TenantId, Site Site, Caller Owner, Caller Approver, Guid RequestId, string RequestNo, string SubmittedAt, string ETag);

    private static string Return(Guid id) => $"{Requests}/{id}/return-for-correction";

    // ---------------- Success ----------------

    [Fact]
    public async Task AssignedApprover_Returns_200Draft_ThenOwnerEditsAndResubmits_KeepingRequestNoAndSubmittedAt()
    {
        var scenario = await UnderReviewAsync();

        var returned = await SendAsync(HttpMethod.Post, Return(scenario.RequestId), scenario.Approver, new { reason = "  " + Reason + "  " }, scenario.ETag);

        Assert.Equal(HttpStatusCode.OK, returned.StatusCode);
        var body = await JsonAsync(returned);
        Assert.Equal("DRAFT", body.GetProperty("status").GetString());
        Assert.Equal(scenario.RequestNo, body.GetProperty("requestNo").GetString());
        Assert.Equal(scenario.SubmittedAt, body.GetProperty("submittedAt").GetString());
        Assert.NotEqual(scenario.ETag, returned.Headers.ETag!.Tag);

        var step = await _factory.WithDbAsync(db => db.RepairRequestApprovals.AsNoTracking().SingleAsync(approval => approval.RepairRequestId == scenario.RequestId));
        Assert.Equal(ApprovalStatus.ReturnedForCorrection, step.Status);
        Assert.Equal(Reason, step.DecisionReason);
        Assert.Equal(1, await AuditCountAsync(scenario.RequestId, RepairRequestAudit.ReturnedForCorrectionAction));

        var edited = await SendAsync(HttpMethod.Patch, $"{Requests}/{scenario.RequestId}", scenario.Owner, DraftBody(scenario.Owner, scenario.Site, "Pump leaking at the seal"), returned.Headers.ETag.Tag);
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);

        var resubmitted = await SendAsync(HttpMethod.Post, $"{Requests}/{scenario.RequestId}/submit", scenario.Owner, new { }, edited.Headers.ETag!.Tag);

        Assert.Equal(HttpStatusCode.OK, resubmitted.StatusCode);
        var resubmittedBody = await JsonAsync(resubmitted);
        Assert.Equal("UNDER_REVIEW", resubmittedBody.GetProperty("status").GetString());
        Assert.Equal(scenario.RequestNo, resubmittedBody.GetProperty("requestNo").GetString());
        Assert.Equal(scenario.SubmittedAt, resubmittedBody.GetProperty("submittedAt").GetString());
        Assert.Equal(1, await AuditCountAsync(scenario.RequestId, RepairRequestAudit.ResubmittedAction));

        var cycles = await _factory.WithDbAsync(db => db.RepairRequestApprovals.AsNoTracking()
            .Where(approval => approval.RepairRequestId == scenario.RequestId)
            .OrderBy(approval => approval.ApprovalCycleNo)
            .Select(approval => new { approval.ApprovalCycleNo, approval.Status })
            .ToListAsync());
        Assert.Equal(2, cycles.Count);
        Assert.Equal(ApprovalStatus.ReturnedForCorrection, cycles[0].Status);
        Assert.Equal(ApprovalStatus.Pending, cycles[1].Status);
    }

    // ---------------- Authorization ----------------

    [Fact]
    public async Task WithoutToken_Returns401()
    {
        await AssertProblemAsync(await SendAsync(HttpMethod.Post, Return(Guid.NewGuid()), null, new { reason = Reason }, AnyETag), HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
    }

    [Theory]
    [InlineData(RoleCodes.Requester)]
    [InlineData(RoleCodes.Supervisor)]
    [InlineData(RoleCodes.Coordinator)]
    [InlineData(RoleCodes.Administrator)]
    public async Task RolesWithoutApprover_Get403(string role)
    {
        var scenario = await UnderReviewAsync();
        var caller = await CallerAsync(scenario.TenantId, [scenario.Site], role);

        await AssertProblemAsync(await SendAsync(HttpMethod.Post, Return(scenario.RequestId), caller, new { reason = Reason }, scenario.ETag), HttpStatusCode.Forbidden, "ACCESS_DENIED");
        await AssertUnchangedAsync(scenario.RequestId);
    }

    [Fact]
    public async Task UnassignedApprover_AndTheOwnerHoldingApprover_Get403_AndOtherTenant_Gets404()
    {
        var scenario = await UnderReviewAsync();
        var unassigned = await CallerAsync(scenario.TenantId, [scenario.Site], RoleCodes.Approver);
        var otherTenantId = Guid.NewGuid();
        var foreign = await CallerAsync(otherTenantId, [await SiteAsync(otherTenantId)], RoleCodes.Approver);

        await AssertProblemAsync(await SendAsync(HttpMethod.Post, Return(scenario.RequestId), unassigned, new { reason = Reason }, scenario.ETag), HttpStatusCode.Forbidden, "ACCESS_DENIED");
        await AssertProblemAsync(await SendAsync(HttpMethod.Post, Return(scenario.RequestId), foreign, new { reason = Reason }, scenario.ETag), HttpStatusCode.NotFound, "NOT_FOUND");
        await AssertUnchangedAsync(scenario.RequestId);

        // The creator is never the assigned approver; holding APPROVER as well does not let them decide their own request.
        var ownerApprover = await OwnerApproverScenarioAsync();
        await AssertProblemAsync(await SendAsync(HttpMethod.Post, Return(ownerApprover.RequestId), ownerApprover.Owner, new { reason = Reason }, ownerApprover.ETag), HttpStatusCode.Forbidden, "ACCESS_DENIED");
        await AssertUnchangedAsync(ownerApprover.RequestId);
    }

    // ---------------- Request contract and state ----------------

    [Fact]
    public async Task MissingIfMatch_Returns400_StaleETag_Returns409_AndInvalidReason_Returns422()
    {
        var scenario = await UnderReviewAsync();

        await AssertProblemAsync(await SendAsync(HttpMethod.Post, Return(scenario.RequestId), scenario.Approver, new { reason = Reason }, null), HttpStatusCode.BadRequest, "BAD_REQUEST");
        await AssertProblemAsync(await SendAsync(HttpMethod.Post, Return(scenario.RequestId), scenario.Approver, new { reason = Reason }, AnyETag), HttpStatusCode.Conflict, "CONCURRENCY_CONFLICT");

        foreach (var body in new object?[] { null, new { }, new { reason = "  " }, new { reason = new string('R', 1001) } })
        {
            var problem = await AssertProblemAsync(await SendAsync(HttpMethod.Post, Return(scenario.RequestId), scenario.Approver, body, scenario.ETag), HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
            Assert.True(problem.GetProperty("errors").TryGetProperty("reason", out _));
        }

        await AssertUnchangedAsync(scenario.RequestId);
    }

    [Fact]
    public async Task ApprovedOrAlreadyReturnedRequest_Returns409StateConflict()
    {
        var approved = await UnderReviewAsync();
        var approval = await SendAsync(HttpMethod.Post, $"{Requests}/{approved.RequestId}/approve", approved.Approver, null, approved.ETag);
        Assert.Equal(HttpStatusCode.OK, approval.StatusCode);
        await AssertProblemAsync(await SendAsync(HttpMethod.Post, Return(approved.RequestId), approved.Approver, new { reason = Reason }, approval.Headers.ETag!.Tag), HttpStatusCode.Conflict, "STATE_CONFLICT");
        Assert.Equal(0, await AuditCountAsync(approved.RequestId, RepairRequestAudit.ReturnedForCorrectionAction));

        var returned = await UnderReviewAsync();
        var first = await SendAsync(HttpMethod.Post, Return(returned.RequestId), returned.Approver, new { reason = Reason }, returned.ETag);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        await AssertProblemAsync(await SendAsync(HttpMethod.Post, Return(returned.RequestId), returned.Approver, new { reason = Reason }, first.Headers.ETag!.Tag), HttpStatusCode.Conflict, "STATE_CONFLICT");
        Assert.Equal(1, await AuditCountAsync(returned.RequestId, RepairRequestAudit.ReturnedForCorrectionAction));
    }

    // ---------------- Helpers ----------------

    /// <summary>Tenant + Site + Site route + one approver; the owner's request is submitted and routed to UNDER_REVIEW.</summary>
    private async Task<Scenario> UnderReviewAsync()
    {
        var tenantId = Guid.NewGuid();
        var site = await SiteAsync(tenantId);
        var admin = await CallerAsync(tenantId, [], RoleCodes.Administrator);
        var owner = await CallerAsync(tenantId, [site], RoleCodes.Requester);
        var approver = await CallerAsync(tenantId, [site], RoleCodes.Approver);
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(HttpMethod.Post, Routes, admin, new { requestCategoryCode = "ELECTRICAL", siteId = site.Id, approverRoleCode = "APPROVER" }, null)).StatusCode);
        return await SubmitScenarioAsync(tenantId, site, owner, approver);
    }

    /// <summary>The owner also holds APPROVER; a second approver exists so routing (which excludes the creator) can assign.</summary>
    private async Task<Scenario> OwnerApproverScenarioAsync()
    {
        var tenantId = Guid.NewGuid();
        var site = await SiteAsync(tenantId);
        var admin = await CallerAsync(tenantId, [], RoleCodes.Administrator);
        var owner = await CallerAsync(tenantId, [site], RoleCodes.Requester, RoleCodes.Approver);
        var approver = await CallerAsync(tenantId, [site], RoleCodes.Approver);
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(HttpMethod.Post, Routes, admin, new { requestCategoryCode = "ELECTRICAL", siteId = site.Id, approverRoleCode = "APPROVER" }, null)).StatusCode);
        return await SubmitScenarioAsync(tenantId, site, owner, approver);
    }

    private async Task<Scenario> SubmitScenarioAsync(Guid tenantId, Site site, Caller owner, Caller approver)
    {
        var created = await SendAsync(HttpMethod.Post, Requests, owner, DraftBody(owner, site, "Pump leaking"), null);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await JsonAsync(created)).GetProperty("id").GetGuid();

        await _factory.WithDbAsync(async db =>
        {
            var file = new FileAsset(tenantId, "evidence.png", "image/png", 1024, ValidHash, $"{tenantId:N}/{Guid.NewGuid():N}", owner.UserId, DateTime.UtcNow);
            file.MarkClean();
            db.FileAssets.Add(file);
            db.RepairRequestAttachments.Add(new RepairRequestAttachment(id, file.Id, AttachmentFileRules.AccessScopeCode));
            await db.SaveChangesAsync();
        });

        var submitted = await SendAsync(HttpMethod.Post, $"{Requests}/{id}/submit", owner, new { }, created.Headers.ETag!.Tag);
        var body = await JsonAsync(submitted);
        Assert.Equal("UNDER_REVIEW", body.GetProperty("status").GetString());
        return new Scenario(
            tenantId,
            site,
            owner,
            approver,
            id,
            body.GetProperty("requestNo").GetString()!,
            body.GetProperty("submittedAt").GetString()!,
            submitted.Headers.ETag!.Tag);
    }

    private static object DraftBody(Caller owner, Site site, string description) => new
    {
        siteId = site.Id,
        requestCategoryCode = "ELECTRICAL",
        priorityCode = "HIGH",
        requestContactId = owner.UserId,
        description,
        preferredStartAt = "2026-09-20T08:00:00Z",
        preferredEndAt = "2026-09-20T10:00:00Z"
    };

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

    private Task<int> AuditCountAsync(Guid requestId, string action) =>
        _factory.WithDbAsync(db => db.AuditHistory.CountAsync(audit => audit.EntityId == requestId && audit.ActionCode == action));

    private async Task AssertUnchangedAsync(Guid requestId)
    {
        var stored = await _factory.WithDbAsync(db => db.RepairRequests.AsNoTracking().SingleAsync(request => request.Id == requestId));
        Assert.Equal(RepairRequestStatus.UnderReview, stored.Status);
        var step = await _factory.WithDbAsync(db => db.RepairRequestApprovals.AsNoTracking().SingleAsync(approval => approval.RepairRequestId == requestId));
        Assert.True(step.IsAssignedPending);
        Assert.Null(step.DecisionReason);
        Assert.Equal(0, await AuditCountAsync(requestId, RepairRequestAudit.ReturnedForCorrectionAction));
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
