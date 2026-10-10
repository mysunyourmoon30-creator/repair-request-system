using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RepairRequest.Application.Attachments;
using RepairRequest.Application.Audit;
using RepairRequest.Application.Common;
using RepairRequest.Domain.Auditing;
using RepairRequest.Domain.Files;
using RepairRequest.Domain.MasterData;
using RepairRequest.Domain.RepairRequests;
using RepairRequest.Domain.Security;
using RepairRequest.Domain.WorkOrders;
using RepairRequest.Infrastructure.Identity;
using RepairRequest.Infrastructure.Persistence;
using RepairRequest.Infrastructure.RepairRequests;

namespace RepairRequest.ApiTests.Audit;

/// <summary>API host with its own disposable LocalDB database for the Work Order timeline endpoint tests (AUD-API-001).</summary>
public sealed class AuditTimelineApiFactory : AuthApiFactory
{
    private readonly ConcurrentDictionary<string, Task<object>> _shared = new();

    public override string ConnectionString =>
        "Server=(localdb)\\MSSQLLocalDB;Database=RepairRequestDb_AuditTimelineApiTest;Trusted_Connection=True;TrustServerCertificate=True";

    /// <summary>Builds an expensive scenario once per factory (tests in one class run sequentially).</summary>
    public async Task<T> SharedAsync<T>(string key, Func<Task<T>> build) where T : class =>
        (T)await _shared.GetOrAdd(key, async _ => await build());
}

/// <summary>
/// AUD-API-001 — the Work Order timeline (UC-WO-002; FR-09; `docs/15` v1.1 D1–D12) end to end through the real host:
/// authorization (401/403), scope (one identical 404), the validation order, composition, ordering, keyset paging, the
/// redaction scan of the serialized body, actor classification, an empty timeline, read-only behaviour and more than
/// 2,100 child entity ids. Audit rows are produced by the real commands.
/// </summary>
public sealed class AuditTimelineEndpointsTests : IClassFixture<AuditTimelineApiFactory>
{
    private const string Requests = "/api/v1/repair-requests";
    private const string Routes = "/api/v1/approval-routes";
    private const string WorkOrders = "/api/v1/work-orders";
    private const string Visits = "/api/v1/service-visits";
    private const string Sessions = "/api/v1/work-sessions";
    private const string CorrectiveActions = "/api/v1/corrective-actions";
    private const string ValidHash = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";

    // Sentinels planted in every free-text field a command accepts; none may ever appear in a timeline response.
    private const string SummarySentinel = "SENTINEL-SUMMARY-TEXT-9f31";
    private const string PauseSentinel = "SENTINEL-PAUSE-REASON-9f31";
    private const string RejectSentinel = "SENTINEL-REJECT-REASON-9f31";
    private const string PlanSentinel = "SENTINEL-PLAN-TEXT-9f31";
    private const string CancelSentinel = "SENTINEL-CANCEL-REASON-9f31";
    private const string CostNoteSentinel = "SENTINEL-COST-NOTE-9f31";
    private const string CostCurrency = "XTS";
    private const string CostAmount = "98765.43";

    private static readonly string[] AllSentinels =
        [SummarySentinel, PauseSentinel, RejectSentinel, PlanSentinel, CancelSentinel, CostNoteSentinel, CostAmount, "9876543"];

    private static readonly HashSet<string> AllowedDetailKeys =
    [
        "workOrderId", "workOrderNo", "serviceVisitId", "ownerTeamId", "assignedTechnicianId", "scheduledStartAt", "scheduledEndAt",
        "workSessionId", "workSessionPauseId", "pausedAt", "resumedAt", "workSummaryId", "repairOutcomeCode", "customerAcceptanceId",
        "correctiveActionId", "costSummaryId", "assignedTeamId", "decision", "newServiceVisitId",
    ];

    private static readonly string[] ForbiddenPropertyNames =
        ["reason", "correlationId", "oldValue", "oldValueJson", "newValue", "newValueJson", "totalAmount", "currencyCode", "note", "email",
         "displayName", "userName", "phone", "acceptanceContactSnapshot", "summaryText", "planText", "decisionReason", "cancelReason"];

    private readonly AuditTimelineApiFactory _factory;

    public AuditTimelineEndpointsTests(AuditTimelineApiFactory factory)
    {
        _factory = factory;
    }

    private sealed record Caller(Guid UserId, Guid TenantId, string Token, string Email);

    private sealed record World(
        Guid TenantId, Site Site, Caller Owner, Caller Approver, Caller Coordinator, Caller Technician, Caller TeamLead, Caller Supervisor,
        Guid WorkOrderId, Guid RepairRequestId, Guid VisitId, string ETag);

    // ============================================================ authorization and scope

    [Fact]
    public async Task EveryReaderRole_InScope_Gets200_WithTheContractShape()
    {
        var w = await CancelledLifecycleAsync();

        foreach (var caller in new[] { w.Supervisor, w.Coordinator, w.TeamLead, w.Approver, w.Owner })
        {
            var response = await TimelineAsync(caller, "WORK_ORDER", w.WorkOrderId);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await JsonAsync(response);
            Assert.Equal(new[] { "hasMore", "items", "nextCursor" }, body.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
            var item = body.GetProperty("items").EnumerateArray().First();
            Assert.Equal(
                new[] { "actionCode", "actor", "auditId", "details", "entityId", "entityType", "fromState", "occurredAt", "toState" },
                item.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
            Assert.Equal(new[] { "actorId", "kind" }, item.GetProperty("actor").EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
            Assert.Equal(16, body.GetProperty("items").GetArrayLength()); // the whole (short) lifecycle fits in one default page
            Assert.False(body.GetProperty("hasMore").GetBoolean());
        }
    }

    [Fact]
    public async Task TechnicianAndAdministrator_Get403_AlsoWithAMalformedCursor()
    {
        var w = await CancelledLifecycleAsync();
        var administrator = await CallerAsync(w.TenantId, [], RoleCodes.Administrator);

        foreach (var caller in new[] { w.Technician, administrator })
        {
            await AssertProblemAsync(await TimelineAsync(caller, "WORK_ORDER", w.WorkOrderId), HttpStatusCode.Forbidden, "ACCESS_DENIED");
            await AssertProblemAsync(await TimelineAsync(caller, "WORK_ORDER", w.WorkOrderId, cursor: "garbage"), HttpStatusCode.Forbidden, "ACCESS_DENIED");
        }
    }

    [Fact]
    public async Task Unauthenticated_Gets401_AlsoWithAMalformedCursor()
    {
        var w = await CancelledLifecycleAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await TimelineAsync(null, "WORK_ORDER", w.WorkOrderId)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await TimelineAsync(null, "WORK_ORDER", w.WorkOrderId, cursor: "garbage")).StatusCode);
    }

    [Fact]
    public async Task MissingOtherSiteOtherTenantNonOwnerAndUnsupportedEntityType_AllGetOneIdenticalNotFoundBody()
    {
        var w = await CancelledLifecycleAsync();
        var otherSite = await SiteAsync(w.TenantId);
        var otherSiteSupervisor = await CallerAsync(w.TenantId, [otherSite], RoleCodes.Supervisor);
        var otherTenantId = Guid.NewGuid();
        var otherTenantSite = await SiteAsync(otherTenantId);
        var otherTenantSupervisor = await CallerAsync(otherTenantId, [otherTenantSite], RoleCodes.Supervisor);
        var nonOwnerRequester = await CallerAsync(w.TenantId, [w.Site], RoleCodes.Requester);

        var responses = new List<HttpResponseMessage>
        {
            await TimelineAsync(w.Supervisor, "WORK_ORDER", Guid.NewGuid()),                 // missing
            await TimelineAsync(otherSiteSupervisor, "WORK_ORDER", w.WorkOrderId),            // another Site, same tenant
            await TimelineAsync(otherTenantSupervisor, "WORK_ORDER", w.WorkOrderId),          // another tenant
            await TimelineAsync(nonOwnerRequester, "WORK_ORDER", w.WorkOrderId),              // REQUESTER who does not own it
            await TimelineAsync(w.Supervisor, "USER", w.WorkOrderId),                        // unsupported entity types
            await TimelineAsync(w.Supervisor, "FILE_ASSET", w.WorkOrderId),
            await TimelineAsync(w.Supervisor, "REPAIR_REQUEST", w.RepairRequestId),
            await TimelineAsync(w.Supervisor, "SERVICE_VISIT", w.VisitId),
        };

        var bodies = new List<string>();
        foreach (var response in responses)
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            bodies.Add(NormalizedProblem(await response.Content.ReadAsStringAsync()));
        }

        Assert.Single(bodies.Distinct());
        Assert.Contains("NOT_FOUND", bodies[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task EntityType_IsNormalizedByUpperCasing()
    {
        var w = await CancelledLifecycleAsync();

        Assert.Equal(HttpStatusCode.OK, (await TimelineAsync(w.Supervisor, "work_order", w.WorkOrderId)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await TimelineAsync(w.Supervisor, "Work_Order", w.WorkOrderId)).StatusCode);
    }

    [Fact]
    public async Task ANonGuidEntityId_DoesNotMatchTheRoute()
    {
        var w = await CancelledLifecycleAsync();
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/entities/WORK_ORDER/not-a-guid/timeline");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", w.Supervisor.Token);

        Assert.Equal(HttpStatusCode.NotFound, (await CreateClient().SendAsync(request)).StatusCode);
    }

    // ============================================================ validation order (docs/15 §5)

    [Fact]
    public async Task MalformedCursor_IsAGenericBadRequest_IdenticalForAnExistingAndANonExistingWorkOrder()
    {
        var w = await CancelledLifecycleAsync();

        var existing = await TimelineAsync(w.Supervisor, "WORK_ORDER", w.WorkOrderId, cursor: "not-a-cursor");
        var missing = await TimelineAsync(w.Supervisor, "WORK_ORDER", Guid.NewGuid(), cursor: "not-a-cursor");
        var unsupported = await TimelineAsync(w.Supervisor, "USER", w.WorkOrderId, cursor: "not-a-cursor");

        Assert.Equal(HttpStatusCode.BadRequest, existing.StatusCode);
        Assert.Equal(NormalizedProblem(await existing.Content.ReadAsStringAsync()), NormalizedProblem(await missing.Content.ReadAsStringAsync()));
        Assert.Equal(HttpStatusCode.BadRequest, unsupported.StatusCode); // syntax is checked before the entity type
        Assert.Contains("BAD_REQUEST", await existing.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TamperedAndForeignBoundCursors_Get400_AndAnAuthenticCursorOnAMissingWorkOrderGets404()
    {
        var w = await CancelledLifecycleAsync();
        var other = await CompletedPathAsync();
        var first = await JsonAsync(await TimelineAsync(w.Supervisor, "WORK_ORDER", w.WorkOrderId, pageSize: 3));
        var cursor = first.GetProperty("nextCursor").GetString()!;

        // authentic for this Work Order: accepted
        Assert.Equal(HttpStatusCode.OK, (await TimelineAsync(w.Supervisor, "WORK_ORDER", w.WorkOrderId, pageSize: 3, cursor: cursor)).StatusCode);

        // tampered: one character changed
        var tampered = (cursor[0] == 'A' ? 'B' : 'A') + cursor[1..];
        Assert.Equal(HttpStatusCode.BadRequest, (await TimelineAsync(w.Supervisor, "WORK_ORDER", w.WorkOrderId, cursor: tampered)).StatusCode);

        // authentic, but issued for another Work Order (both in scope for the caller)
        var foreignWorkOrder = await TimelineAsync(other.Supervisor, "WORK_ORDER", other.WorkOrderId, cursor: cursor);
        Assert.Equal(HttpStatusCode.BadRequest, foreignWorkOrder.StatusCode);

        // authentic, but another tenant's caller replays it for the same Work Order id: bound to the tenant -> 400 (decided before any lookup)
        var otherTenantId = Guid.NewGuid();
        var otherTenantSupervisor = await CallerAsync(otherTenantId, [await SiteAsync(otherTenantId)], RoleCodes.Supervisor);
        Assert.Equal(HttpStatusCode.BadRequest, (await TimelineAsync(otherTenantSupervisor, "WORK_ORDER", w.WorkOrderId, cursor: cursor)).StatusCode);

        // authentic for a Work Order that does not exist (minted with the real protector): passes the HMAC, then 404
        var missingId = Guid.NewGuid();
        using var scope = _factory.Services.CreateScope();
        var protector = scope.ServiceProvider.GetRequiredService<ITimelineCursorProtector>();
        var minted = protector.Protect(w.TenantId, "WORK_ORDER", missingId, new TimelinePosition(DateTime.UtcNow, Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.NotFound, (await TimelineAsync(w.Supervisor, "WORK_ORDER", missingId, cursor: minted)).StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task PageSizeBelowOne_Is400(int pageSize)
    {
        var w = await CancelledLifecycleAsync();

        await AssertProblemAsync(await TimelineAsync(w.Supervisor, "WORK_ORDER", w.WorkOrderId, pageSize: pageSize), HttpStatusCode.BadRequest, "BAD_REQUEST");
    }

    [Fact]
    public async Task WithoutAPageSize_TheDefaultOf20IsUsed()
    {
        var big = await BigChildCountAsync();

        var body = await JsonAsync(await TimelineAsync(big.Supervisor, "WORK_ORDER", big.WorkOrderId));

        Assert.Equal(20, body.GetProperty("items").GetArrayLength());
        Assert.True(body.GetProperty("hasMore").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, body.GetProperty("nextCursor").ValueKind);
    }

    [Fact]
    public async Task PageSizeAboveTheMaximum_IsClampedTo100()
    {
        var big = await BigChildCountAsync();

        var body = await JsonAsync(await TimelineAsync(big.Supervisor, "WORK_ORDER", big.WorkOrderId, pageSize: 1000));

        Assert.Equal(100, body.GetProperty("items").GetArrayLength());
        Assert.True(body.GetProperty("hasMore").GetBoolean());
    }

    // ============================================================ composition, order, paging

    [Fact]
    public async Task FullLifecycle_ContainsEveryExpectedEvent_FromEveryEntityType_AndNothingElse()
    {
        var w = await CancelledLifecycleAsync();
        var otherWorkOrder = await CompletedPathAsync(); // same tenant is NOT shared: a second, unrelated Work Order exists in the database

        var items = await WalkAsync(w.Supervisor, w.WorkOrderId, 100);
        var codes = items.Select(i => i.GetProperty("actionCode").GetString()!).ToList();

        foreach (var expected in new[]
                 {
                     "REPAIR_REQUEST_CONVERTED", "WORK_ORDER_SCHEDULED", "SERVICE_VISIT_CHECKED_IN", "WORK_ORDER_STARTED", "WORK_SESSION_PAUSED",
                     "WORK_SESSION_RESUMED", "WORK_SESSION_CHECKED_OUT", "SERVICE_VISIT_COMPLETED", "WORK_SUMMARY_SUBMITTED",
                     "WORK_ORDER_SUBMITTED_FOR_ACCEPTANCE", "WORK_ORDER_REJECTED", "CORRECTIVE_ACTION_PLAN_SUBMITTED",
                     "CORRECTIVE_ACTION_PLAN_APPROVED", "CORRECTIVE_ACTION_REWORK_SCHEDULED", "WORK_ORDER_CANCELLED", "SERVICE_VISIT_CANCELLED",
                 })
        {
            Assert.Contains(expected, codes);
        }

        var types = items.Select(i => i.GetProperty("entityType").GetString()).ToHashSet();
        Assert.Equal(new[] { "CORRECTIVE_ACTION", "REPAIR_REQUEST", "SERVICE_VISIT", "WORK_ORDER", "WORK_SESSION" }, types.OrderBy(t => t, StringComparer.Ordinal));

        // The originating request contributes ONLY its convert event.
        Assert.Equal(1, items.Count(i => i.GetProperty("entityType").GetString() == "REPAIR_REQUEST"));

        // Exactly this Work Order's audit rows (SQL Server oracle) — nothing from the other Work Order.
        var oracle = await OracleAsync(w.WorkOrderId, w.RepairRequestId);
        Assert.Equal(oracle, items.Select(i => i.GetProperty("auditId").GetGuid()).ToList());
        var otherIds = (await OracleAsync(otherWorkOrder.WorkOrderId, otherWorkOrder.RepairRequestId)).ToHashSet();
        Assert.Empty(items.Select(i => i.GetProperty("auditId").GetGuid()).Where(otherIds.Contains));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(100)]
    public async Task PagingWalk_ReturnsEveryEventExactlyOnce_InSqlServerOrder_ForAnyPageSize(int pageSize)
    {
        var w = await CancelledLifecycleAsync();
        var oracle = await OracleAsync(w.WorkOrderId, w.RepairRequestId);

        var pages = new List<JsonElement>();
        string? cursor = null;
        do
        {
            var page = await JsonAsync(await TimelineAsync(w.Supervisor, "WORK_ORDER", w.WorkOrderId, pageSize: pageSize, cursor: cursor));
            pages.Add(page);
            cursor = page.GetProperty("nextCursor").GetString();
            Assert.Equal(cursor is not null, page.GetProperty("hasMore").GetBoolean());
        }
        while (cursor is not null);

        var ids = pages.SelectMany(p => p.GetProperty("items").EnumerateArray()).Select(i => i.GetProperty("auditId").GetGuid()).ToList();
        Assert.Equal(oracle, ids);                                  // no duplicate, none missing, SQL Server order
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(pages.Take(pages.Count - 1), p => Assert.Equal(pageSize, p.GetProperty("items").GetArrayLength()));
        Assert.False(pages[^1].GetProperty("hasMore").GetBoolean());
        Assert.Equal(JsonValueKind.Null, pages[^1].GetProperty("nextCursor").ValueKind);

        var times = pages.SelectMany(p => p.GetProperty("items").EnumerateArray()).Select(i => i.GetProperty("occurredAt").GetDateTime()).ToList();
        for (var i = 1; i < times.Count; i++)
        {
            Assert.True(times[i] <= times[i - 1]);                  // occurredAt DESC
        }
    }

    [Fact]
    public async Task OccurredAt_IsUtcWithAZSuffix()
    {
        var w = await CancelledLifecycleAsync();

        var body = await (await TimelineAsync(w.Supervisor, "WORK_ORDER", w.WorkOrderId)).Content.ReadAsStringAsync();

        Assert.Matches(@"""occurredAt"":""\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?Z""", body);
    }

    [Fact]
    public async Task OneTimelinePageIsWithin100Items_AndDetailsOfCostEventsExposeOnlyTheCostSummaryId()
    {
        var done = await CompletedPathAsync();

        var items = await WalkAsync(done.Supervisor, done.WorkOrderId, 100);

        var cost = items.Where(i => i.GetProperty("actionCode").GetString() is "COST_SUMMARY_PREPARED" or "COST_SUMMARY_REVIEWED" or "WORK_ORDER_CLOSED").ToList();
        Assert.Equal(3, cost.Count);
        Assert.All(cost, i => Assert.Equal(new[] { "costSummaryId" }, i.GetProperty("details").EnumerateObject().Select(p => p.Name)));
        Assert.Contains(items, i => i.GetProperty("actionCode").GetString() == "WORK_ORDER_ACCEPTED");
    }

    // ============================================================ redaction

    [Fact]
    public async Task SerializedResponses_NeverContainSentinelsAmountsEmailsOrForbiddenProperties()
    {
        var cancelled = await CancelledLifecycleAsync();
        var done = await CompletedPathAsync();

        foreach (var (world, caller) in new[] { (cancelled, cancelled.Supervisor), (cancelled, cancelled.Owner), (done, done.Supervisor), (done, done.Owner), (done, done.TeamLead) })
        {
            var raw = new StringBuilder();
            string? cursor = null;
            do
            {
                var response = await TimelineAsync(caller, "WORK_ORDER", world.WorkOrderId, pageSize: 7, cursor: cursor);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var text = await response.Content.ReadAsStringAsync();
                raw.AppendLine(text);
                cursor = JsonDocument.Parse(text).RootElement.GetProperty("nextCursor").GetString();
            }
            while (cursor is not null);

            var all = raw.ToString();
            foreach (var sentinel in AllSentinels)
            {
                Assert.DoesNotContain(sentinel, all, StringComparison.Ordinal);
            }

            foreach (var user in new[] { world.Owner, world.Supervisor, world.TeamLead, world.Coordinator, world.Technician, world.Approver })
            {
                Assert.DoesNotContain(user.Email, all, StringComparison.OrdinalIgnoreCase);
            }

            Assert.DoesNotMatch(@"[\w.+-]+@[\w-]+\.[\w.]+", all);
            Assert.DoesNotContain(CostCurrency, all, StringComparison.Ordinal);

            // No forbidden property name anywhere in the tree, and `details` only carries allowlisted scalar keys.
            foreach (var document in all.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonDocument.Parse(line)))
            {
                foreach (var name in PropertyNames(document.RootElement))
                {
                    Assert.DoesNotContain(name, ForbiddenPropertyNames);
                }

                foreach (var item in document.RootElement.GetProperty("items").EnumerateArray())
                {
                    foreach (var detail in item.GetProperty("details").EnumerateObject())
                    {
                        Assert.Contains(detail.Name, AllowedDetailKeys);
                        Assert.True(detail.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null);
                    }
                }
            }
        }
    }

    private static IEnumerable<string> PropertyNames(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    yield return property.Name;
                    foreach (var child in PropertyNames(property.Value))
                    {
                        yield return child;
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var child in PropertyNames(item))
                    {
                        yield return child;
                    }
                }

                break;
        }
    }

    // ============================================================ actors, empty, read-only

    [Fact]
    public async Task Actors_AreUserSystemOrUnknown_AndNeverCrossTenant()
    {
        var w = await CompletedPathAsync();
        var otherTenantUser = await _factory.CreateUserInTenantAsync(Guid.NewGuid(), RoleCodes.Supervisor);
        var deletedUserId = Guid.NewGuid();
        var at = DateTime.UtcNow.AddMinutes(5);
        var systemRow = await InsertAuditAsync(w, "WORK_ORDER", w.WorkOrderId, "WORK_ORDER_STARTED", SystemActors.ApprovalRouting, at);
        var unknownRow = await InsertAuditAsync(w, "WORK_ORDER", w.WorkOrderId, "WORK_ORDER_STARTED", deletedUserId, at.AddSeconds(1));
        var crossTenantRow = await InsertAuditAsync(w, "WORK_ORDER", w.WorkOrderId, "WORK_ORDER_STARTED", otherTenantUser.Id, at.AddSeconds(2));

        var items = await WalkAsync(w.Supervisor, w.WorkOrderId, 100);
        string KindOf(Guid auditId) => items.Single(i => i.GetProperty("auditId").GetGuid() == auditId).GetProperty("actor").GetProperty("kind").GetString()!;

        Assert.Equal("SYSTEM", KindOf(systemRow));
        Assert.Equal("UNKNOWN", KindOf(unknownRow));
        Assert.Equal("UNKNOWN", KindOf(crossTenantRow));  // an existing user of ANOTHER tenant is never classified USER
        Assert.Contains(items, i => i.GetProperty("actor").GetProperty("kind").GetString() == "USER");
        Assert.Equal(otherTenantUser.Id, items.Single(i => i.GetProperty("auditId").GetGuid() == crossTenantRow).GetProperty("actor").GetProperty("actorId").GetGuid());
    }

    [Fact]
    public async Task EmptyTimeline_Returns200_WithNoItems_NoMore_AndNoCursor()
    {
        var w = await OpenAsync();
        await WithDbAsync(db => db.AuditHistory.Where(a => a.TenantId == w.TenantId && (a.EntityId == w.WorkOrderId || a.EntityId == w.RepairRequestId)).ExecuteDeleteAsync());

        var response = await TimelineAsync(w.Supervisor, "WORK_ORDER", w.WorkOrderId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal(0, body.GetProperty("items").GetArrayLength());
        Assert.False(body.GetProperty("hasMore").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("nextCursor").ValueKind);
    }

    [Fact]
    public async Task ReadingTheTimeline_WritesNoAuditRow_ChangesNoRow_AndIsNotCached()
    {
        var w = await CancelledLifecycleAsync();
        var before = await WithDbAsync(db => db.AuditHistory.AsNoTracking().Where(a => a.TenantId == w.TenantId).OrderBy(a => a.Id).Select(a => a.Id).ToListAsync());

        HttpResponseMessage? last = null;
        for (var i = 0; i < 3; i++)
        {
            last = await TimelineAsync(w.Supervisor, "WORK_ORDER", w.WorkOrderId, pageSize: 5);
        }

        var after = await WithDbAsync(db => db.AuditHistory.AsNoTracking().Where(a => a.TenantId == w.TenantId).OrderBy(a => a.Id).Select(a => a.Id).ToListAsync());
        Assert.Equal(before, after);
        Assert.Equal("no-store", string.Join(",", last!.Headers.CacheControl!.NoStore ? ["no-store"] : []));
        Assert.Null(last.Headers.ETag);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task OtherHttpMethods_Are405_AndWriteNothing(string method)
    {
        var w = await CancelledLifecycleAsync();
        var before = await WithDbAsync(db => db.AuditHistory.AsNoTracking().CountAsync(a => a.TenantId == w.TenantId));
        var request = new HttpRequestMessage(new HttpMethod(method), $"/api/v1/entities/WORK_ORDER/{w.WorkOrderId}/timeline");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", w.Supervisor.Token);

        var response = await CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Equal(before, await WithDbAsync(db => db.AuditHistory.AsNoTracking().CountAsync(a => a.TenantId == w.TenantId)));
    }

    // ============================================================ more than 2,100 child entity ids

    [Fact]
    public async Task MoreThan2100ChildEntityIds_AreServedThroughTheEndpoint_AndWalkedExactlyOnce()
    {
        var big = await BigChildCountAsync();

        var items = await WalkAsync(big.Supervisor, big.WorkOrderId, 100);

        var oracle = await OracleAsync(big.WorkOrderId, big.RepairRequestId);
        Assert.True(oracle.Count > 2300);
        Assert.Equal(oracle, items.Select(i => i.GetProperty("auditId").GetGuid()).ToList());
        Assert.Equal(items.Count, items.Select(i => i.GetProperty("auditId").GetGuid()).Distinct().Count());
        Assert.True(items.Select(i => i.GetProperty("entityId").GetGuid()).Distinct().Count() > 2100);
    }

    // ============================================================ scenarios (real commands)

    /// <summary>
    /// Convert -> Schedule -> Check-in -> Pause -> Resume -> Check-out -> Work Summary -> Submit for Acceptance -> Reject ->
    /// Corrective plan submit/approve -> Schedule Rework -> Cancel (cascading the rework Visit). Free text carries sentinels.
    /// </summary>
    private Task<World> CancelledLifecycleAsync() => _factory.SharedAsync("cancelled", async () =>
    {
        var w = await ScheduledAsync();
        w = await CheckInAsync(w);

        var current = await JsonAsync(await SendAsync(HttpMethod.Get, $"{Sessions}/current", w.Technician, null, null));
        var sessionId = current.GetProperty("workSessionId").GetGuid();
        var paused = await SendAsync(HttpMethod.Post, $"{Sessions}/{sessionId}/pause", w.Technician, new { reason = PauseSentinel }, $"\"{current.GetProperty("rowVersion").GetString()}\"");
        Assert.Equal(HttpStatusCode.OK, paused.StatusCode);
        var resumed = await SendAsync(HttpMethod.Post, $"{Sessions}/{sessionId}/resume", w.Technician, null, paused.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.OK, resumed.StatusCode);

        var checkedOut = await SendAsync(HttpMethod.Post, $"{Sessions}/{sessionId}/check-out", w.Technician, null, resumed.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.OK, checkedOut.StatusCode);
        var workOrderETag = $"\"{(await JsonAsync(checkedOut)).GetProperty("workOrderRowVersion").GetString()}\"";

        var summary = await SendAsync(HttpMethod.Post, $"{WorkOrders}/{w.WorkOrderId}/submit-work-summary", w.Technician,
            new { summaryText = SummarySentinel, repairOutcomeCode = "REPAIRED" }, workOrderETag);
        Assert.Equal(HttpStatusCode.OK, summary.StatusCode);
        var accept = await SendAsync(HttpMethod.Post, $"{WorkOrders}/{w.WorkOrderId}/submit-for-acceptance", w.TeamLead,
            new { acceptanceContactId = w.Owner.UserId }, summary.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.OK, accept.StatusCode);
        var rejected = await SendAsync(HttpMethod.Post, $"{WorkOrders}/{w.WorkOrderId}/reject", w.Owner, new { decisionReason = RejectSentinel }, accept.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);

        var correctiveActionId = await WithDbAsync(db => db.CorrectiveActions.AsNoTracking().Where(c => c.WorkOrderId == w.WorkOrderId).Select(c => c.Id).SingleAsync());
        var planFileId = await WithDbAsync(async db =>
        {
            var file = new FileAsset(w.TenantId, "plan.pdf", "application/pdf", 512, ValidHash, $"{w.TenantId:N}/{Guid.NewGuid():N}", w.TeamLead.UserId, DateTime.UtcNow);
            file.MarkClean();
            db.FileAssets.Add(file);
            await db.SaveChangesAsync();
            return file.Id;
        });
        var submitted = await SendAsync(HttpMethod.Post, $"{CorrectiveActions}/{correctiveActionId}/submit-plan", w.TeamLead,
            new { planText = PlanSentinel, planFileAssetId = planFileId }, await WorkOrderETagAsync(w.WorkOrderId, w.Supervisor));
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
        var approved = await SendAsync(HttpMethod.Post, $"{CorrectiveActions}/{correctiveActionId}/approve-plan", w.Supervisor, null, await WorkOrderETagAsync(w.WorkOrderId, w.Supervisor));
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);

        var correctiveETag = $"\"{Convert.ToBase64String((await WithDbAsync(db => db.CorrectiveActions.AsNoTracking().SingleAsync(c => c.Id == correctiveActionId))).RowVersion)}\"";
        var rework = await SendAsync(HttpMethod.Post, $"{CorrectiveActions}/{correctiveActionId}/schedule-rework", w.Coordinator, new
        {
            assignedTeamId = Guid.NewGuid(),
            assignedTechnicianId = w.Technician.UserId,
            scheduledStartAt = "2026-10-05T08:00:00Z",
            scheduledEndAt = "2026-10-05T10:00:00Z"
        }, correctiveETag);
        Assert.Equal(HttpStatusCode.OK, rework.StatusCode);

        var cancelled = await SendAsync(HttpMethod.Post, $"{WorkOrders}/{w.WorkOrderId}/cancel", w.Supervisor, new { reason = CancelSentinel }, await WorkOrderETagAsync(w.WorkOrderId, w.Supervisor));
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        return w;
    });

    /// <summary>... -> Accept -> Cost Summary (sentinel amount/currency/note) -> Review (different user) -> Close.</summary>
    private Task<World> CompletedPathAsync() => _factory.SharedAsync("completed", async () =>
    {
        var w = await ScheduledAsync();
        w = await CheckInAsync(w);
        var current = await JsonAsync(await SendAsync(HttpMethod.Get, $"{Sessions}/current", w.Technician, null, null));
        var sessionId = current.GetProperty("workSessionId").GetGuid();
        var checkedOut = await SendAsync(HttpMethod.Post, $"{Sessions}/{sessionId}/check-out", w.Technician, null, $"\"{current.GetProperty("rowVersion").GetString()}\"");
        Assert.Equal(HttpStatusCode.OK, checkedOut.StatusCode);
        var summary = await SendAsync(HttpMethod.Post, $"{WorkOrders}/{w.WorkOrderId}/submit-work-summary", w.Technician,
            new { summaryText = SummarySentinel, repairOutcomeCode = "REPAIRED" }, $"\"{(await JsonAsync(checkedOut)).GetProperty("workOrderRowVersion").GetString()}\"");
        Assert.Equal(HttpStatusCode.OK, summary.StatusCode);
        var submitted = await SendAsync(HttpMethod.Post, $"{WorkOrders}/{w.WorkOrderId}/submit-for-acceptance", w.TeamLead,
            new { acceptanceContactId = w.Owner.UserId }, summary.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
        var accepted = await SendAsync(HttpMethod.Post, $"{WorkOrders}/{w.WorkOrderId}/accept", w.Owner, null, submitted.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        var prepared = await SendAsync(HttpMethod.Put, $"{WorkOrders}/{w.WorkOrderId}/cost-summary", w.TeamLead,
            new { totalAmount = decimal.Parse(CostAmount, System.Globalization.CultureInfo.InvariantCulture), currencyCode = CostCurrency, note = CostNoteSentinel },
            await WorkOrderETagAsync(w.WorkOrderId, w.TeamLead));
        Assert.Equal(HttpStatusCode.OK, prepared.StatusCode);
        var reviewed = await SendAsync(HttpMethod.Post, $"{WorkOrders}/{w.WorkOrderId}/review-cost-summary", w.Supervisor, null, prepared.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.OK, reviewed.StatusCode);
        var closed = await SendAsync(HttpMethod.Post, $"{WorkOrders}/{w.WorkOrderId}/close", w.Supervisor, null, await WorkOrderETagAsync(w.WorkOrderId, w.Supervisor));
        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);
        return w;
    });

    /// <summary>A Work Order with 1,150 real Visits, each with a Work Session (2,300 child entity ids) and one audit row per child.</summary>
    private Task<World> BigChildCountAsync() => _factory.SharedAsync("big", async () =>
    {
        var w = await OpenAsync();
        var technicianId = w.Technician.UserId;
        var ids = await WithDbAsync(async db =>
        {
            var visits = Enumerable.Range(0, 1150).Select(_ => ServiceVisit.Create(
                w.TenantId, w.WorkOrderId, ServiceVisitType.Initial, Guid.NewGuid(), technicianId, DateTime.UtcNow.AddDays(1), DateTime.UtcNow.AddDays(1).AddHours(2))).ToList();
            db.ServiceVisits.AddRange(visits);
            await db.SaveChangesAsync();
            var sessions = visits.Select(v =>
            {
                var session = WorkSession.Create(w.TenantId, v.Id, technicianId, DateTime.UtcNow.AddHours(-2));
                session.CheckOut(DateTime.UtcNow.AddHours(-1));
                return session;
            }).ToList();
            db.WorkSessions.AddRange(sessions);
            await db.SaveChangesAsync();
            var now = DateTime.UtcNow;
            db.AuditHistory.AddRange(
                visits.Select((v, i) => new AuditHistory(w.TenantId, "SERVICE_VISIT", v.Id, "SERVICE_VISIT_CHECKED_IN", null, null, null, null, null, technicianId, now.AddSeconds(-i % 90), Guid.NewGuid()))
                    .Concat(sessions.Select((s, i) => new AuditHistory(w.TenantId, "WORK_SESSION", s.Id, "WORK_SESSION_CHECKED_OUT", null, null, null, null, null, technicianId, now.AddSeconds(-i % 90), Guid.NewGuid()))));
            await db.SaveChangesAsync();
            return visits.Count + sessions.Count;
        });
        Assert.True(ids > 2100);
        return w;
    });

    // ============================================================ building blocks (real flow)

    private async Task<World> OpenAsync()
    {
        var tenantId = Guid.NewGuid();
        var site = await SiteAsync(tenantId);
        var admin = await CallerAsync(tenantId, [], RoleCodes.Administrator);
        var owner = await CallerAsync(tenantId, [site], RoleCodes.Requester);
        var approver = await CallerAsync(tenantId, [site], RoleCodes.Approver);
        var coordinator = await CallerAsync(tenantId, [site], RoleCodes.Coordinator);
        var technician = await CallerAsync(tenantId, [site], RoleCodes.Technician);
        var teamLead = await CallerAsync(tenantId, [site], RoleCodes.TeamLead);
        var supervisor = await CallerAsync(tenantId, [site], RoleCodes.Supervisor);

        Assert.Equal(HttpStatusCode.Created, (await SendAsync(HttpMethod.Post, Routes, admin, new { requestCategoryCode = "ELECTRICAL", siteId = site.Id, approverRoleCode = "APPROVER" }, null)).StatusCode);

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
        var repairRequestId = (await JsonAsync(created)).GetProperty("id").GetGuid();
        await WithDbAsync(async db =>
        {
            var evidence = new FileAsset(tenantId, "evidence.png", "image/png", 1024, ValidHash, $"{tenantId:N}/{Guid.NewGuid():N}", owner.UserId, DateTime.UtcNow);
            evidence.MarkClean();
            db.FileAssets.Add(evidence);
            db.RepairRequestAttachments.Add(new RepairRequestAttachment(repairRequestId, evidence.Id, AttachmentFileRules.AccessScopeCode));
            await db.SaveChangesAsync();
        });

        var submitted = await SendAsync(HttpMethod.Post, $"{Requests}/{repairRequestId}/submit", owner, new { duplicateContinuationReason = (string?)null }, created.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
        var approved = await SendAsync(HttpMethod.Post, $"{Requests}/{repairRequestId}/approve", approver, null, submitted.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        var converted = await SendAsync(HttpMethod.Post, $"{Requests}/{repairRequestId}/convert-to-work-order", coordinator, null, approved.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.OK, converted.StatusCode);
        var workOrderId = (await JsonAsync(converted)).GetProperty("workOrderId").GetGuid();

        return new World(tenantId, site, owner, approver, coordinator, technician, teamLead, supervisor, workOrderId, repairRequestId, Guid.Empty, await WorkOrderETagAsync(workOrderId, supervisor));
    }

    private async Task<World> ScheduledAsync()
    {
        var w = await OpenAsync();
        var scheduled = await SendAsync(HttpMethod.Post, $"{WorkOrders}/{w.WorkOrderId}/schedule", w.Coordinator, new
        {
            ownerTeamId = Guid.NewGuid(),
            assignedTechnicianId = w.Technician.UserId,
            scheduledStartAt = "2026-10-01T08:00:00Z",
            scheduledEndAt = "2026-10-01T10:00:00Z"
        }, w.ETag);
        Assert.Equal(HttpStatusCode.OK, scheduled.StatusCode);
        var visitId = Assert.Single((await JsonAsync(scheduled)).GetProperty("visits").EnumerateArray()).GetProperty("serviceVisitId").GetGuid();
        return w with { VisitId = visitId, ETag = await WorkOrderETagAsync(w.WorkOrderId, w.Supervisor) };
    }

    private async Task<World> CheckInAsync(World w)
    {
        var visit = await WithDbAsync(db => db.ServiceVisits.AsNoTracking().SingleAsync(v => v.Id == w.VisitId));
        var checkedIn = await SendAsync(HttpMethod.Post, $"{Visits}/{w.VisitId}/check-in", w.Technician, null, $"\"{Convert.ToBase64String(visit.RowVersion)}\"");
        Assert.Equal(HttpStatusCode.OK, checkedIn.StatusCode);
        return w with { ETag = await WorkOrderETagAsync(w.WorkOrderId, w.Supervisor) };
    }

    // ============================================================ helpers

    private Task<HttpResponseMessage> TimelineAsync(Caller? caller, string entityType, Guid entityId, int? pageSize = null, string? cursor = null)
    {
        var query = new List<string>();
        if (pageSize is not null) { query.Add($"pageSize={pageSize}"); }
        if (cursor is not null) { query.Add($"cursor={Uri.EscapeDataString(cursor)}"); }
        var url = $"/api/v1/entities/{entityType}/{entityId}/timeline" + (query.Count > 0 ? "?" + string.Join("&", query) : "");
        return SendAsync(HttpMethod.Get, url, caller, null, null);
    }

    private async Task<List<JsonElement>> WalkAsync(Caller caller, Guid workOrderId, int pageSize)
    {
        var items = new List<JsonElement>();
        string? cursor = null;
        do
        {
            var response = await TimelineAsync(caller, "WORK_ORDER", workOrderId, pageSize, cursor);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var page = await JsonAsync(response);
            items.AddRange(page.GetProperty("items").EnumerateArray().Select(i => i.Clone()));
            cursor = page.GetProperty("nextCursor").GetString();
        }
        while (cursor is not null);

        return items;
    }

    /// <summary>SQL Server's own ordering of this Work Order's audit rows (independent of the endpoint's keyset query).</summary>
    private async Task<List<Guid>> OracleAsync(Guid workOrderId, Guid repairRequestId)
    {
        var sql = $@"SELECT a.audit_history_id AS [Value] FROM audit_history a WHERE
     (a.entity_type = 'WORK_ORDER' AND a.entity_id = '{workOrderId}')
  OR (a.entity_type = 'SERVICE_VISIT' AND a.entity_id IN (SELECT service_visit_id FROM service_visit WHERE work_order_id = '{workOrderId}'))
  OR (a.entity_type = 'WORK_SESSION' AND a.entity_id IN (SELECT s.work_session_id FROM work_session s JOIN service_visit v ON v.service_visit_id = s.service_visit_id WHERE v.work_order_id = '{workOrderId}'))
  OR (a.entity_type = 'CORRECTIVE_ACTION' AND a.entity_id IN (SELECT corrective_action_id FROM corrective_action WHERE work_order_id = '{workOrderId}'))
  OR (a.entity_type = 'REPAIR_REQUEST' AND a.entity_id = '{repairRequestId}' AND a.action_code = 'REPAIR_REQUEST_CONVERTED')
ORDER BY a.occurred_at DESC, a.audit_history_id DESC";
        return await WithDbAsync(db => db.Database.SqlQueryRaw<Guid>(sql).ToListAsync());
    }

    private async Task<Guid> InsertAuditAsync(World w, string type, Guid entityId, string action, Guid actor, DateTime at) =>
        await WithDbAsync(async db =>
        {
            var row = new AuditHistory(w.TenantId, type, entityId, action, null, null, null, null, null, actor, at, Guid.NewGuid());
            db.AuditHistory.Add(row);
            await db.SaveChangesAsync();
            return row.Id;
        });

    private async Task<string> WorkOrderETagAsync(Guid workOrderId, Caller caller)
    {
        var response = await SendAsync(HttpMethod.Get, $"{WorkOrders}/{workOrderId}", caller, null, null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return response.Headers.ETag!.Tag;
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
        return new Caller(user.Id, tenantId, token, user.Email!);
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

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, (await JsonAsync(response)).GetProperty("code").GetString());
    }

    /// <summary>A problem body with its per-request identifiers removed, for byte-for-byte comparison of "identical" responses.</summary>
    private static string NormalizedProblem(string body)
    {
        using var document = JsonDocument.Parse(body);
        var members = document.RootElement.EnumerateObject()
            .Where(p => p.Name is not ("correlationId" or "traceId"))
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => $"{p.Name}={p.Value.GetRawText()}");
        return string.Join("|", members);
    }
}
