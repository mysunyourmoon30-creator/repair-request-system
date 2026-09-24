using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RepairRequest.Application.Attachments;
using RepairRequest.Domain.Files;
using RepairRequest.Domain.MasterData;
using RepairRequest.Domain.RepairRequests;
using RepairRequest.Domain.Security;
using RepairRequest.Domain.WorkOrders;
using RepairRequest.Infrastructure.Identity;
using RepairRequest.Infrastructure.Persistence;
using RepairRequest.Infrastructure.RepairRequests;

namespace RepairRequest.ApiTests.WorkOrders;

/// <summary>API host with its own disposable LocalDB database for the Customer Accept endpoint tests.</summary>
public sealed class CustomerAcceptApiFactory : AuthApiFactory
{
    public override string ConnectionString =>
        "Server=(localdb)\\MSSQLLocalDB;Database=RepairRequestDb_CustomerAcceptApiTest;Trusted_Connection=True;TrustServerCertificate=True";
}

/// <summary>
/// ACC-API-001 Customer Accept (ST-WO-005; UC-WO-021; `docs/13` §4.16) end to end. Only the exact designated
/// Acceptance Contact (a REQUESTER) may Accept — resource-specific identity, not a role-scoped query. If-Match
/// is checked against the Work Order's own RowVersion. This file covers Accept only, per `docs/13` §4.16's own
/// scope boundary: no Reject, Corrective Action, Close or invitation/activation.
/// </summary>
public sealed class CustomerAcceptEndpointsTests : IClassFixture<CustomerAcceptApiFactory>
{
    private const string Requests = "/api/v1/repair-requests";
    private const string Routes = "/api/v1/approval-routes";
    private const string WorkOrders = "/api/v1/work-orders";
    private const string Visits = "/api/v1/service-visits";
    private const string Sessions = "/api/v1/work-sessions";
    private const string ValidHash = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";

    private readonly CustomerAcceptApiFactory _factory;

    public CustomerAcceptEndpointsTests(CustomerAcceptApiFactory factory)
    {
        _factory = factory;
    }

    private sealed record Caller(Guid UserId, Guid TenantId, string Token);

    private sealed record AwaitingAcceptance(Guid TenantId, Site Site, Caller Owner, Guid WorkOrderId, string WorkOrderETag);

    // ---------------- Success ----------------

    [Fact]
    public async Task Accept_Success_MovesToCompleted_AndAudits_InOneTransaction()
    {
        var w = await AwaitingAcceptanceAsync();
        var before = DateTime.UtcNow.AddSeconds(-2);

        var response = await SendAsync(HttpMethod.Post, $"{WorkOrders}/{w.WorkOrderId}/accept", w.Owner, null, w.WorkOrderETag);
        var after = DateTime.UtcNow.AddSeconds(2);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal("COMPLETED", body.GetProperty("status").GetString());
        var newETag = $"\"{body.GetProperty("rowVersion").GetString()}\"";
        Assert.NotEqual(w.WorkOrderETag, newETag);

        var workOrder = await WithDbAsync(db => db.WorkOrders.AsNoTracking().SingleAsync(item => item.Id == w.WorkOrderId));
        Assert.Equal(WorkOrderStatus.Completed, workOrder.Status);

        var audit = await WithDbAsync(db => db.AuditHistory.AsNoTracking().SingleAsync(a => a.EntityId == w.WorkOrderId && a.ActionCode == "WORK_ORDER_ACCEPTED"));
        Assert.Equal("WORK_ORDER", audit.EntityType);
        Assert.Equal("AWAITING_CUSTOMER_ACCEPTANCE", audit.FromState);
        Assert.Equal("COMPLETED", audit.ToState);
        Assert.Null(audit.Reason);
        Assert.Equal(w.Owner.UserId, audit.ActorId);
        Assert.InRange(audit.OccurredAt, before, after);
        Assert.NotEqual(Guid.Empty, audit.CorrelationId);
    }

    // ---------------- Authorization / scope / IDOR ----------------

    [Fact]
    public async Task Accept_ByANonRequester_Get403()
    {
        var w = await AwaitingAcceptanceAsync();
        var supervisor = await CallerAsync(w.TenantId, [w.Site], RoleCodes.Supervisor);

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{WorkOrders}/{w.WorkOrderId}/accept", supervisor, null, w.WorkOrderETag),
            HttpStatusCode.Forbidden, "ACCESS_DENIED");
        await AssertStillAwaitingAcceptanceAsync(w.WorkOrderId);
    }

    [Fact]
    public async Task Accept_ByADifferentRequesterInTheSameTenant_Get404()
    {
        var w = await AwaitingAcceptanceAsync();
        var otherRequester = await CallerAsync(w.TenantId, [w.Site], RoleCodes.Requester);

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{WorkOrders}/{w.WorkOrderId}/accept", otherRequester, null, w.WorkOrderETag),
            HttpStatusCode.NotFound, "NOT_FOUND");
        await AssertStillAwaitingAcceptanceAsync(w.WorkOrderId);
    }

    [Fact]
    public async Task Accept_ByARequesterOfAnotherTenant_Get404()
    {
        var w = await AwaitingAcceptanceAsync();
        var foreign = await CallerAsync(Guid.NewGuid(), [], RoleCodes.Requester);

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{WorkOrders}/{w.WorkOrderId}/accept", foreign, null, w.WorkOrderETag),
            HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task Accept_ByTheContactWhoseSiteScopeWasRevoked_Get404()
    {
        var w = await AwaitingAcceptanceAsync();
        await WithDbAsync(db => db.UserSiteScopes
            .Where(scope => scope.TenantId == w.TenantId && scope.UserId == w.Owner.UserId && scope.SiteId == w.Site.Id)
            .ExecuteDeleteAsync());

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{WorkOrders}/{w.WorkOrderId}/accept", w.Owner, null, w.WorkOrderETag),
            HttpStatusCode.NotFound, "NOT_FOUND");
        await AssertStillAwaitingAcceptanceAsync(w.WorkOrderId);
    }

    [Fact]
    public async Task Accept_OfAnUnknownWorkOrder_Get404()
    {
        var w = await AwaitingAcceptanceAsync();

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{WorkOrders}/{Guid.NewGuid()}/accept", w.Owner, null, w.WorkOrderETag),
            HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task Accept_WithoutIfMatch_Returns400_AndWithoutAToken_Returns401()
    {
        var w = await AwaitingAcceptanceAsync();

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{WorkOrders}/{w.WorkOrderId}/accept", w.Owner, null, null),
            HttpStatusCode.BadRequest, "BAD_REQUEST");
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await SendAsync(HttpMethod.Post, $"{WorkOrders}/{w.WorkOrderId}/accept", null, null, w.WorkOrderETag)).StatusCode);
        await AssertStillAwaitingAcceptanceAsync(w.WorkOrderId);
    }

    // ---------------- State / duplicate / concurrency ----------------

    [Fact]
    public async Task Accept_BeforeSubmitForAcceptance_Returns404_BecauseNoContactIsDesignatedYet()
    {
        // AcceptanceContactId is only ever set atomically with the ST-WO-004 transition (submit-for-acceptance),
        // so before that call, no one — not even the eventual owner — matches the resource-specific scope check
        // yet. This is 404 (scope), not 409 (state): the two are checked in that order, the same convention
        // every other command in this codebase uses.
        var c = await CheckedOutAndSummarySubmittedAsync();

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{WorkOrders}/{c.WorkOrderId}/accept", c.Owner, null, c.WorkOrderETag),
            HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task ASecondAccept_OfAnAlreadyAcceptedWorkOrder_Returns409StateConflict()
    {
        var w = await AwaitingAcceptanceAsync();
        var first = await SendAsync(HttpMethod.Post, $"{WorkOrders}/{w.WorkOrderId}/accept", w.Owner, null, w.WorkOrderETag);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var freshETag = first.Headers.ETag!.Tag;

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{WorkOrders}/{w.WorkOrderId}/accept", w.Owner, null, freshETag),
            HttpStatusCode.Conflict, "STATE_CONFLICT");

        Assert.Equal(1, await WithDbAsync(db => db.AuditHistory.AsNoTracking().CountAsync(a => a.EntityId == w.WorkOrderId && a.ActionCode == "WORK_ORDER_ACCEPTED")));
    }

    [Fact]
    public async Task Accept_WithAStaleRowVersion_Returns409ConcurrencyConflict_AndWritesNothing()
    {
        var w = await AwaitingAcceptanceAsync();
        var first = await SendAsync(HttpMethod.Post, $"{WorkOrders}/{w.WorkOrderId}/accept", w.Owner, null, w.WorkOrderETag);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Post, $"{WorkOrders}/{w.WorkOrderId}/accept", w.Owner, null, w.WorkOrderETag),
            HttpStatusCode.Conflict, "CONCURRENCY_CONFLICT");
        Assert.Equal(1, await WithDbAsync(db => db.AuditHistory.AsNoTracking().CountAsync(a => a.EntityId == w.WorkOrderId && a.ActionCode == "WORK_ORDER_ACCEPTED")));
    }

    [Fact]
    public async Task TwoConcurrentAccepts_OfTheSameWorkOrder_ExactlyOneSucceeds_NoPartialUpdate()
    {
        var w = await AwaitingAcceptanceAsync();

        var responses = await Task.WhenAll(
            Task.Run(() => SendAsync(HttpMethod.Post, $"{WorkOrders}/{w.WorkOrderId}/accept", w.Owner, null, w.WorkOrderETag)),
            Task.Run(() => SendAsync(HttpMethod.Post, $"{WorkOrders}/{w.WorkOrderId}/accept", w.Owner, null, w.WorkOrderETag)));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal(1, await WithDbAsync(db => db.AuditHistory.AsNoTracking().CountAsync(a => a.EntityId == w.WorkOrderId && a.ActionCode == "WORK_ORDER_ACCEPTED")));

        var workOrder = await WithDbAsync(db => db.WorkOrders.AsNoTracking().SingleAsync(item => item.Id == w.WorkOrderId));
        Assert.Equal(WorkOrderStatus.Completed, workOrder.Status);
    }

    // ---------------- Helpers ----------------

    private async Task AssertStillAwaitingAcceptanceAsync(Guid workOrderId)
    {
        var workOrder = await WithDbAsync(db => db.WorkOrders.AsNoTracking().SingleAsync(item => item.Id == workOrderId));
        Assert.Equal(WorkOrderStatus.AwaitingCustomerAcceptance, workOrder.Status);
        Assert.Equal(0, await WithDbAsync(db => db.AuditHistory.AsNoTracking().CountAsync(a => a.EntityId == workOrderId && a.ActionCode == "WORK_ORDER_ACCEPTED")));
    }

    /// <summary>A Work Order AWAITING_CUSTOMER_ACCEPTANCE, with the owning Requester designated as the Acceptance Contact.</summary>
    private async Task<AwaitingAcceptance> AwaitingAcceptanceAsync()
    {
        var c = await CheckedOutAndSummarySubmittedAsync();

        var response = await SendAsync(HttpMethod.Post, $"{WorkOrders}/{c.WorkOrderId}/submit-for-acceptance", c.TeamLead,
            new { acceptanceContactId = c.Owner.UserId }, c.WorkOrderETag);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var etag = $"\"{(await JsonAsync(response)).GetProperty("rowVersion").GetString()}\"";

        return new AwaitingAcceptance(c.TenantId, c.Site, c.Owner, c.WorkOrderId, etag);
    }

    private sealed record ReviewReady(Guid TenantId, Site Site, Caller Owner, Caller TeamLead, Guid WorkOrderId, string WorkOrderETag);

    /// <summary>The full flow up to a submitted Work Summary (AWAITING_SUPERVISOR_REVIEW): Schedule, Check-in, Check-out, Submit Work Summary.</summary>
    private async Task<ReviewReady> CheckedOutAndSummarySubmittedAsync()
    {
        var tenantId = Guid.NewGuid();
        var site = await SiteAsync(tenantId);
        var admin = await CallerAsync(tenantId, [], RoleCodes.Administrator);
        var owner = await CallerAsync(tenantId, [site], RoleCodes.Requester);
        var approver = await CallerAsync(tenantId, [site], RoleCodes.Approver);
        var coordinator = await CallerAsync(tenantId, [site], RoleCodes.Coordinator);
        var technician = await CallerAsync(tenantId, [site], RoleCodes.Technician);
        var teamLead = await CallerAsync(tenantId, [site], RoleCodes.TeamLead);

        Assert.Equal(HttpStatusCode.Created, (await SendAsync(HttpMethod.Post, Routes, admin, new { requestCategoryCode = "ELECTRICAL", siteId = site.Id, approverRoleCode = "APPROVER" }, null)).StatusCode);

        var (id, draftETag) = await DraftAsync(owner, site);
        await WithDbAsync(async db =>
        {
            var file = new FileAsset(tenantId, "evidence.png", "image/png", 1024, ValidHash, $"{tenantId:N}/{Guid.NewGuid():N}", owner.UserId, DateTime.UtcNow);
            file.MarkClean();
            db.FileAssets.Add(file);
            db.RepairRequestAttachments.Add(new RepairRequestAttachment(id, file.Id, AttachmentFileRules.AccessScopeCode));
            await db.SaveChangesAsync();
        });

        var submitted = await SendAsync(HttpMethod.Post, $"{Requests}/{id}/submit", owner, new { duplicateContinuationReason = (string?)null }, draftETag);
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);

        var approved = await SendAsync(HttpMethod.Post, $"{Requests}/{id}/approve", approver, null, submitted.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);

        var converted = await SendAsync(HttpMethod.Post, $"{Requests}/{id}/convert-to-work-order", coordinator, null, approved.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.OK, converted.StatusCode);
        var workOrderId = (await JsonAsync(converted)).GetProperty("workOrderId").GetGuid();

        var scheduled = await SendAsync(HttpMethod.Post, $"{WorkOrders}/{workOrderId}/schedule", coordinator, new
        {
            ownerTeamId = Guid.NewGuid(),
            assignedTechnicianId = technician.UserId,
            scheduledStartAt = "2026-10-01T08:00:00Z",
            scheduledEndAt = "2026-10-01T10:00:00Z"
        }, converted.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.OK, scheduled.StatusCode);
        var visit = Assert.Single((await JsonAsync(scheduled)).GetProperty("visits").EnumerateArray());
        var visitId = visit.GetProperty("serviceVisitId").GetGuid();
        var visitETag = $"\"{visit.GetProperty("rowVersion").GetString()}\"";

        var checkedIn = await SendAsync(HttpMethod.Post, $"{Visits}/{visitId}/check-in", technician, null, visitETag);
        Assert.Equal(HttpStatusCode.OK, checkedIn.StatusCode);

        var current = await SendAsync(HttpMethod.Get, $"{Sessions}/current", technician, null, null);
        var currentBody = await JsonAsync(current);
        var sessionId = currentBody.GetProperty("workSessionId").GetGuid();
        var sessionETag = $"\"{currentBody.GetProperty("rowVersion").GetString()}\"";

        var checkedOut = await SendAsync(HttpMethod.Post, $"{Sessions}/{sessionId}/check-out", technician, null, sessionETag);
        Assert.Equal(HttpStatusCode.OK, checkedOut.StatusCode);
        var workOrderETag = $"\"{(await JsonAsync(checkedOut)).GetProperty("workOrderRowVersion").GetString()}\"";

        var summarySubmitted = await SendAsync(HttpMethod.Post, $"{WorkOrders}/{workOrderId}/submit-work-summary", technician,
            new { summaryText = "Replaced the pump seal.", repairOutcomeCode = "REPAIRED" }, workOrderETag);
        Assert.Equal(HttpStatusCode.OK, summarySubmitted.StatusCode);
        var reviewEtag = $"\"{(await JsonAsync(summarySubmitted)).GetProperty("rowVersion").GetString()}\"";

        return new ReviewReady(tenantId, site, owner, teamLead, workOrderId, reviewEtag);
    }

    private async Task<(Guid Id, string ETag)> DraftAsync(Caller owner, Site site)
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
