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

/// <summary>API host with its own disposable LocalDB database for the Cost Summary Prepare endpoint tests.</summary>
public sealed class CostSummaryPrepareApiFactory : AuthApiFactory
{
    public override string ConnectionString =>
        "Server=(localdb)\\MSSQLLocalDB;Database=RepairRequestDb_CostSummaryPrepareApiTest;Trusted_Connection=True;TrustServerCertificate=True";
}

/// <summary>
/// CST-API-001 Cost Summary Prepare (Team Lead only; BR-08; `docs/13` §4.18) end to end. First Prepare creates the
/// single Cost Summary row (If-Match against the Work Order's own RowVersion); every later edit updates it in
/// place (If-Match against the Cost Summary's own RowVersion instead — see <see cref="ICostSummaryStore"/>'s own
/// remarks on why the Work Order's RowVersion cannot detect a lost update once Prepare no longer changes it).
/// This file covers Prepare only, per `docs/13` §4.18's own scope boundary: no Review, no read/review-queue
/// endpoint, no reviewed_at mutation, no Work Order Close, no Corrective Action lifecycle.
/// </summary>
public sealed class CostSummaryPrepareEndpointsTests : IClassFixture<CostSummaryPrepareApiFactory>
{
    private const string Requests = "/api/v1/repair-requests";
    private const string Routes = "/api/v1/approval-routes";
    private const string WorkOrders = "/api/v1/work-orders";
    private const string Visits = "/api/v1/service-visits";
    private const string Sessions = "/api/v1/work-sessions";
    private const string ValidHash = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";

    private readonly CostSummaryPrepareApiFactory _factory;

    public CostSummaryPrepareEndpointsTests(CostSummaryPrepareApiFactory factory)
    {
        _factory = factory;
    }

    private sealed record Caller(Guid UserId, Guid TenantId, string Token);

    private sealed record CompletedWorkOrder(Guid TenantId, Site Site, Caller TeamLead, Guid WorkOrderId, string WorkOrderETag);

    // ---------------- Success ----------------

    [Fact]
    public async Task Prepare_FirstTime_CreatesCostSummary_AndAudits_InOneTransaction()
    {
        var w = await CompletedAsync();
        var before = DateTime.UtcNow.AddSeconds(-2);

        var response = await SendAsync(HttpMethod.Put, $"{WorkOrders}/{w.WorkOrderId}/cost-summary", w.TeamLead,
            new { totalAmount = 1234.56m, currencyCode = "USD", note = "Parts and labor." }, w.WorkOrderETag);
        var after = DateTime.UtcNow.AddSeconds(2);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal(1234.56m, body.GetProperty("totalAmount").GetDecimal());
        Assert.Equal("USD", body.GetProperty("currencyCode").GetString());
        Assert.Equal("Parts and labor.", body.GetProperty("note").GetString());
        Assert.Equal(w.TeamLead.UserId, body.GetProperty("preparedBy").GetGuid());
        Assert.True(body.GetProperty("reviewedBy").ValueKind == JsonValueKind.Null);
        Assert.True(body.GetProperty("reviewedAt").ValueKind == JsonValueKind.Null);

        var costSummary = await WithDbAsync(db => db.CostSummaries.AsNoTracking().SingleAsync(item => item.WorkOrderId == w.WorkOrderId));
        Assert.Equal(1234.56m, costSummary.TotalAmount);
        Assert.Equal("USD", costSummary.CurrencyCode);
        Assert.Equal(w.TeamLead.UserId, costSummary.PreparedBy);
        Assert.InRange(costSummary.PreparedAt, before, after);

        var workOrder = await WithDbAsync(db => db.WorkOrders.AsNoTracking().SingleAsync(item => item.Id == w.WorkOrderId));
        Assert.Equal(WorkOrderStatus.Completed, workOrder.Status);

        var audit = await WithDbAsync(db => db.AuditHistory.AsNoTracking().SingleAsync(a => a.EntityId == w.WorkOrderId && a.ActionCode == "COST_SUMMARY_PREPARED"));
        Assert.Equal("WORK_ORDER", audit.EntityType);
        Assert.Equal("COMPLETED", audit.FromState);
        Assert.Equal("COMPLETED", audit.ToState);
        Assert.Null(audit.Reason);
        Assert.Equal(w.TeamLead.UserId, audit.ActorId);
        Assert.InRange(audit.OccurredAt, before, after);
    }

    [Fact]
    public async Task Prepare_ASecondTime_UpdatesTheSameRowInPlace_UsingTheCostSummarysOwnETag()
    {
        var w = await CompletedAsync();

        var first = await SendAsync(HttpMethod.Put, $"{WorkOrders}/{w.WorkOrderId}/cost-summary", w.TeamLead,
            new { totalAmount = 100m, currencyCode = "USD", note = "First pass." }, w.WorkOrderETag);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var costSummaryETag = first.Headers.ETag!.Tag;

        var second = await SendAsync(HttpMethod.Put, $"{WorkOrders}/{w.WorkOrderId}/cost-summary", w.TeamLead,
            new { totalAmount = 250.75m, currencyCode = "THB", note = "Revised after parts arrived." }, costSummaryETag);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var body = await JsonAsync(second);
        Assert.Equal(250.75m, body.GetProperty("totalAmount").GetDecimal());
        Assert.Equal("THB", body.GetProperty("currencyCode").GetString());

        Assert.Equal(1, await WithDbAsync(db => db.CostSummaries.AsNoTracking().CountAsync(item => item.WorkOrderId == w.WorkOrderId)));
        Assert.Equal(1, await WithDbAsync(db => db.AuditHistory.AsNoTracking().CountAsync(a => a.EntityId == w.WorkOrderId && a.ActionCode == "COST_SUMMARY_PREPARED")));
        Assert.Equal(1, await WithDbAsync(db => db.AuditHistory.AsNoTracking().CountAsync(a => a.EntityId == w.WorkOrderId && a.ActionCode == "COST_SUMMARY_UPDATED")));
    }

    [Fact]
    public async Task Prepare_ReflectsInTheWorkOrderResponsesSignalFields_ForAllThreeStates()
    {
        var w = await CompletedAsync();

        // No Cost Summary yet: both signal fields null.
        var none = await JsonAsync(await SendAsync(HttpMethod.Get, $"{WorkOrders}/{w.WorkOrderId}", w.TeamLead, null, null));
        Assert.True(none.GetProperty("costSummaryRowVersion").ValueKind == JsonValueKind.Null);
        Assert.True(none.GetProperty("costSummaryReviewedAt").ValueKind == JsonValueKind.Null);

        var prepared = await SendAsync(HttpMethod.Put, $"{WorkOrders}/{w.WorkOrderId}/cost-summary", w.TeamLead,
            new { totalAmount = 10m, currencyCode = "USD", note = (string?)null }, w.WorkOrderETag);
        Assert.Equal(HttpStatusCode.OK, prepared.StatusCode);

        // Prepared, not yet reviewed: rowVersion has a value, reviewedAt is still null.
        var unreviewed = await JsonAsync(await SendAsync(HttpMethod.Get, $"{WorkOrders}/{w.WorkOrderId}", w.TeamLead, null, null));
        Assert.False(unreviewed.GetProperty("costSummaryRowVersion").ValueKind == JsonValueKind.Null);
        Assert.True(unreviewed.GetProperty("costSummaryReviewedAt").ValueKind == JsonValueKind.Null);

        // Nothing in this ticket's own scope can ever set reviewed_at (Review is Ticket 4b) — seeded directly to
        // verify the read-model contract for the third, future state too.
        await WithDbAsync(db => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE cost_summary SET reviewed_by = {w.TeamLead.UserId}, reviewed_at = {DateTime.UtcNow} WHERE work_order_id = {w.WorkOrderId}"));

        // Reviewed: both signal fields have a value.
        var reviewed = await JsonAsync(await SendAsync(HttpMethod.Get, $"{WorkOrders}/{w.WorkOrderId}", w.TeamLead, null, null));
        Assert.False(reviewed.GetProperty("costSummaryRowVersion").ValueKind == JsonValueKind.Null);
        Assert.False(reviewed.GetProperty("costSummaryReviewedAt").ValueKind == JsonValueKind.Null);
    }

    // ---------------- Authorization / scope ----------------

    [Fact]
    public async Task Prepare_ByANonTeamLead_Get403()
    {
        var w = await CompletedAsync();
        var supervisor = await CallerAsync(w.TenantId, [w.Site], RoleCodes.Supervisor);

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Put, $"{WorkOrders}/{w.WorkOrderId}/cost-summary", supervisor,
                new { totalAmount = 1m, currencyCode = "USD", note = (string?)null }, w.WorkOrderETag),
            HttpStatusCode.Forbidden, "ACCESS_DENIED");
    }

    [Fact]
    public async Task Prepare_ByATeamLeadOfAnotherTenant_Get404()
    {
        var w = await CompletedAsync();
        var foreign = await CallerAsync(Guid.NewGuid(), [], RoleCodes.TeamLead);

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Put, $"{WorkOrders}/{w.WorkOrderId}/cost-summary", foreign,
                new { totalAmount = 1m, currencyCode = "USD", note = (string?)null }, w.WorkOrderETag),
            HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task Prepare_ByATeamLeadWithNoSiteScopeOnThisWorkOrder_Get404()
    {
        var w = await CompletedAsync();
        var outOfScopeTeamLead = await CallerAsync(w.TenantId, [], RoleCodes.TeamLead);

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Put, $"{WorkOrders}/{w.WorkOrderId}/cost-summary", outOfScopeTeamLead,
                new { totalAmount = 1m, currencyCode = "USD", note = (string?)null }, w.WorkOrderETag),
            HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task Prepare_OfAnUnknownWorkOrder_Get404()
    {
        var w = await CompletedAsync();

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Put, $"{WorkOrders}/{Guid.NewGuid()}/cost-summary", w.TeamLead,
                new { totalAmount = 1m, currencyCode = "USD", note = (string?)null }, w.WorkOrderETag),
            HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task Prepare_WithoutIfMatch_Returns400_AndWithoutAToken_Returns401()
    {
        var w = await CompletedAsync();

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Put, $"{WorkOrders}/{w.WorkOrderId}/cost-summary", w.TeamLead,
                new { totalAmount = 1m, currencyCode = "USD", note = (string?)null }, null),
            HttpStatusCode.BadRequest, "BAD_REQUEST");
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await SendAsync(HttpMethod.Put, $"{WorkOrders}/{w.WorkOrderId}/cost-summary", null,
                new { totalAmount = 1m, currencyCode = "USD", note = (string?)null }, w.WorkOrderETag)).StatusCode);
    }

    // ---------------- State guards ----------------

    [Fact]
    public async Task Prepare_BeforeTheWorkOrderIsCompleted_Returns409StateConflict()
    {
        var c = await CheckedOutAndSummarySubmittedAsync();

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Put, $"{WorkOrders}/{c.WorkOrderId}/cost-summary", c.TeamLead,
                new { totalAmount = 1m, currencyCode = "USD", note = (string?)null }, c.WorkOrderETag),
            HttpStatusCode.Conflict, "STATE_CONFLICT");
    }

    [Fact]
    public async Task Prepare_AfterTheCostSummaryHasBeenReviewed_Returns409StateConflict_AndWritesNothing()
    {
        var w = await CompletedAsync();
        var prepared = await SendAsync(HttpMethod.Put, $"{WorkOrders}/{w.WorkOrderId}/cost-summary", w.TeamLead,
            new { totalAmount = 1m, currencyCode = "USD", note = (string?)null }, w.WorkOrderETag);
        Assert.Equal(HttpStatusCode.OK, prepared.StatusCode);

        // Nothing in this ticket's own scope can ever set reviewed_at (Review is a future ticket) — seeded
        // directly to simulate the state that ticket will eventually be able to produce, so the guard this
        // ticket's decisions require ("Prepare/Update after Review must return 409") is proven correct now.
        // SQL Server's rowversion column auto-advances on this raw write too, so the ETag used below must be
        // re-read afterward — the one from the Prepare response above is now stale, the same as it would be
        // for a real client who fetched the Cost Summary before someone else reviewed it.
        await WithDbAsync(db => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE cost_summary SET reviewed_by = {w.TeamLead.UserId}, reviewed_at = {DateTime.UtcNow} WHERE work_order_id = {w.WorkOrderId}"));
        var freshETag = $"\"{Convert.ToBase64String(await WithDbAsync(db => db.CostSummaries.AsNoTracking().Where(item => item.WorkOrderId == w.WorkOrderId).Select(item => item.RowVersion).SingleAsync()))}\"";

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Put, $"{WorkOrders}/{w.WorkOrderId}/cost-summary", w.TeamLead,
                new { totalAmount = 999m, currencyCode = "EUR", note = "Should be rejected." }, freshETag),
            HttpStatusCode.Conflict, "STATE_CONFLICT");

        var costSummary = await WithDbAsync(db => db.CostSummaries.AsNoTracking().SingleAsync(item => item.WorkOrderId == w.WorkOrderId));
        Assert.Equal(1m, costSummary.TotalAmount);
        Assert.Equal("USD", costSummary.CurrencyCode);
    }

    // ---------------- Validation ----------------

    [Theory]
    [InlineData("-1", "USD", "totalAmount")]
    [InlineData("1.005", "USD", "totalAmount")]
    [InlineData("1", "US", "currencyCode")]
    [InlineData("1", "USDD", "currencyCode")]
    public async Task Prepare_WithInvalidFields_Returns422_AndWritesNothing(string totalAmount, string currencyCode, string invalidField)
    {
        var w = await CompletedAsync();

        var body = await AssertProblemAsync(
            await SendAsync(HttpMethod.Put, $"{WorkOrders}/{w.WorkOrderId}/cost-summary", w.TeamLead,
                new { totalAmount = decimal.Parse(totalAmount), currencyCode, note = (string?)null }, w.WorkOrderETag),
            HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
        Assert.True(body.GetProperty("errors").TryGetProperty(invalidField, out _));
        Assert.Equal(0, await WithDbAsync(db => db.CostSummaries.AsNoTracking().CountAsync(item => item.WorkOrderId == w.WorkOrderId)));
    }

    [Fact]
    public async Task Prepare_WithALowerCaseCurrencyCode_IsNormalizedToUpperCase()
    {
        var w = await CompletedAsync();

        var response = await SendAsync(HttpMethod.Put, $"{WorkOrders}/{w.WorkOrderId}/cost-summary", w.TeamLead,
            new { totalAmount = 1m, currencyCode = "usd", note = (string?)null }, w.WorkOrderETag);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("USD", (await JsonAsync(response)).GetProperty("currencyCode").GetString());
    }

    [Fact]
    public async Task Prepare_WithANoteLongerThanTheMaxLength_Returns422()
    {
        var w = await CompletedAsync();

        var body = await AssertProblemAsync(
            await SendAsync(HttpMethod.Put, $"{WorkOrders}/{w.WorkOrderId}/cost-summary", w.TeamLead,
                new { totalAmount = 1m, currencyCode = "USD", note = new string('A', CostSummary.NoteMaxLength + 1) }, w.WorkOrderETag),
            HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
        Assert.True(body.GetProperty("errors").TryGetProperty("note", out _));
    }

    // ---------------- Concurrency ----------------

    [Fact]
    public async Task Prepare_FirstTime_WithAStaleWorkOrderRowVersion_Returns409ConcurrencyConflict_AndWritesNothing()
    {
        var w = await CompletedAsync();

        // Any write against the Work Order (Accept already happened) would change its RowVersion; simplest stale
        // token here is simply a mismatched one.
        var staleETag = "\"AAAAAAAAAAA=\"";

        await AssertProblemAsync(
            await SendAsync(HttpMethod.Put, $"{WorkOrders}/{w.WorkOrderId}/cost-summary", w.TeamLead,
                new { totalAmount = 1m, currencyCode = "USD", note = (string?)null }, staleETag),
            HttpStatusCode.Conflict, "CONCURRENCY_CONFLICT");
        Assert.Equal(0, await WithDbAsync(db => db.CostSummaries.AsNoTracking().CountAsync(item => item.WorkOrderId == w.WorkOrderId)));
    }

    [Fact]
    public async Task Prepare_ASecondTime_WithAStaleCostSummaryRowVersion_Returns409ConcurrencyConflict_AndWritesNothing()
    {
        var w = await CompletedAsync();
        var first = await SendAsync(HttpMethod.Put, $"{WorkOrders}/{w.WorkOrderId}/cost-summary", w.TeamLead,
            new { totalAmount = 1m, currencyCode = "USD", note = (string?)null }, w.WorkOrderETag);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // Re-using the Work Order's own (unchanged) ETag for a second edit must NOT be accepted as fresh — the
        // whole point of the two-phase design is that the Work Order's ETag never advances during Prepare.
        await AssertProblemAsync(
            await SendAsync(HttpMethod.Put, $"{WorkOrders}/{w.WorkOrderId}/cost-summary", w.TeamLead,
                new { totalAmount = 2m, currencyCode = "USD", note = (string?)null }, w.WorkOrderETag),
            HttpStatusCode.Conflict, "CONCURRENCY_CONFLICT");

        var costSummary = await WithDbAsync(db => db.CostSummaries.AsNoTracking().SingleAsync(item => item.WorkOrderId == w.WorkOrderId));
        Assert.Equal(1m, costSummary.TotalAmount);
    }

    [Fact]
    public async Task TwoConcurrentFirstTimePrepares_OfTheSameWorkOrder_ExactlyOneSucceeds_NoPartialUpdate()
    {
        var w = await CompletedAsync();

        var responses = await Task.WhenAll(
            Task.Run(() => SendAsync(HttpMethod.Put, $"{WorkOrders}/{w.WorkOrderId}/cost-summary", w.TeamLead,
                new { totalAmount = 1m, currencyCode = "USD", note = (string?)null }, w.WorkOrderETag)),
            Task.Run(() => SendAsync(HttpMethod.Put, $"{WorkOrders}/{w.WorkOrderId}/cost-summary", w.TeamLead,
                new { totalAmount = 2m, currencyCode = "USD", note = (string?)null }, w.WorkOrderETag)));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal(1, await WithDbAsync(db => db.CostSummaries.AsNoTracking().CountAsync(item => item.WorkOrderId == w.WorkOrderId)));
        Assert.Equal(1, await WithDbAsync(db => db.AuditHistory.AsNoTracking().CountAsync(a => a.EntityId == w.WorkOrderId && a.ActionCode == "COST_SUMMARY_PREPARED")));
    }

    // ---------------- Helpers ----------------

    /// <summary>A Work Order COMPLETED (Accept already happened), owner Requester already the designated contact, Team Lead available for Prepare.</summary>
    private async Task<CompletedWorkOrder> CompletedAsync()
    {
        var c = await CheckedOutAndSummarySubmittedAsync();

        var submitted = await SendAsync(HttpMethod.Post, $"{WorkOrders}/{c.WorkOrderId}/submit-for-acceptance", c.TeamLead,
            new { acceptanceContactId = c.Owner.UserId }, c.WorkOrderETag);
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
        var awaitingETag = $"\"{(await JsonAsync(submitted)).GetProperty("rowVersion").GetString()}\"";

        var accepted = await SendAsync(HttpMethod.Post, $"{WorkOrders}/{c.WorkOrderId}/accept", c.Owner, null, awaitingETag);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var completedETag = $"\"{(await JsonAsync(accepted)).GetProperty("rowVersion").GetString()}\"";

        return new CompletedWorkOrder(c.TenantId, c.Site, c.TeamLead, c.WorkOrderId, completedETag);
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
