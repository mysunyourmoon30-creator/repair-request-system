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

/// <summary>API host with its own disposable LocalDB database for the Customer Re-acceptance round-2 tests.</summary>
public sealed class RoundTwoReacceptanceApiFactory : AuthApiFactory
{
    public override string ConnectionString =>
        "Server=(localdb)\\MSSQLLocalDB;Database=RepairRequestDb_RoundTwoReacceptanceApiTest;Trusted_Connection=True;TrustServerCertificate=True";
}

/// <summary>
/// Customer Re-acceptance round 2 (`UC-WO-024`; BR-07/BR-15) end to end, through the real Accept/Reject endpoints
/// (ACC-API-001/002, `docs/13` §4.16/§4.17) rather than seeding acceptance/corrective rows directly — the earlier
/// discovery for this ticket found every underlying mechanism (round/cycle number computation, state gates,
/// concurrency, the Close evidence query, the Work Order read model) already written round-generic, so this file's
/// purpose is to prove that end to end and lock it in as a regression guard, not to introduce a new business rule.
/// No cap on the number of rework cycles is asserted anywhere here — none exists in the baseline.
/// </summary>
public sealed class RoundTwoReacceptanceEndpointsTests : IClassFixture<RoundTwoReacceptanceApiFactory>
{
    private const string Requests = "/api/v1/repair-requests";
    private const string Routes = "/api/v1/approval-routes";
    private const string WorkOrders = "/api/v1/work-orders";
    private const string Visits = "/api/v1/service-visits";
    private const string Sessions = "/api/v1/work-sessions";
    private const string CorrectiveActions = "/api/v1/corrective-actions";
    private const string ValidHash = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";

    private readonly RoundTwoReacceptanceApiFactory _factory;

    public RoundTwoReacceptanceEndpointsTests(RoundTwoReacceptanceApiFactory factory)
    {
        _factory = factory;
    }

    private sealed record Caller(Guid UserId, Guid TenantId, string Token);

    /// <summary>A Work Order AWAITING_CUSTOMER_ACCEPTANCE for the *second* time — round 1 was a real Reject, the rework was completed, and Submit for Acceptance ran again with the same designated contact.</summary>
    private sealed record AwaitingAcceptanceRoundTwo(Guid TenantId, Site Site, Caller Owner, Guid WorkOrderId, string WorkOrderETag, Guid FirstCorrectiveActionId);

    // ---------------- Accept round 2: success ----------------

    [Fact]
    public async Task AcceptRoundTwo_Success_MovesToCompleted_AndBothAcceptanceRoundsAreRecorded()
    {
        var w = await AwaitingAcceptanceRoundTwoAsync();
        var before = DateTime.UtcNow.AddSeconds(-2);

        var response = await SendAsync(HttpMethod.Post, $"{WorkOrders}/{w.WorkOrderId}/accept", w.Owner, null, w.WorkOrderETag);
        var after = DateTime.UtcNow.AddSeconds(2);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal("COMPLETED", body.GetProperty("status").GetString());

        var workOrder = await WithDbAsync(db => db.WorkOrders.AsNoTracking().SingleAsync(item => item.Id == w.WorkOrderId));
        Assert.Equal(WorkOrderStatus.Completed, workOrder.Status);

        var acceptances = await WithDbAsync(db => db.CustomerAcceptances.AsNoTracking()
            .Where(item => item.WorkOrderId == w.WorkOrderId).OrderBy(item => item.AcceptanceRoundNo).ToListAsync());
        Assert.Equal(2, acceptances.Count);
        Assert.Equal(1, acceptances[0].AcceptanceRoundNo);
        Assert.Equal(AcceptanceDecision.Reject, acceptances[0].Decision);
        Assert.Equal(2, acceptances[1].AcceptanceRoundNo);
        Assert.Equal(AcceptanceDecision.Accept, acceptances[1].Decision);
        Assert.Equal(w.Owner.UserId, acceptances[1].AcceptanceContactId);
        Assert.Null(acceptances[1].DecisionReason);
        Assert.InRange(acceptances[1].DecidedAt, before, after);

        var acceptedAudit = await WithDbAsync(db => db.AuditHistory.AsNoTracking()
            .SingleAsync(a => a.EntityId == w.WorkOrderId && a.ActionCode == "WORK_ORDER_ACCEPTED"));
        Assert.Equal("AWAITING_CUSTOMER_ACCEPTANCE", acceptedAudit.FromState);
        Assert.Equal("COMPLETED", acceptedAudit.ToState);
        Assert.Equal(w.Owner.UserId, acceptedAudit.ActorId);
        Assert.InRange(acceptedAudit.OccurredAt, before, after);

        // Round 1's own audit row (the real Reject that started this cycle) is untouched history, not overwritten.
        Assert.Equal(1, await WithDbAsync(db => db.AuditHistory.AsNoTracking().CountAsync(a => a.EntityId == w.WorkOrderId && a.ActionCode == "WORK_ORDER_REJECTED")));
    }

    // ---------------- Reject round 2: success ----------------

    [Fact]
    public async Task RejectRoundTwo_Success_CreatesTheSecondCorrectiveActionCycle_AndLeavesTheFirstCycleUntouched()
    {
        var w = await AwaitingAcceptanceRoundTwoAsync();
        var firstCycleBefore = await WithDbAsync(db => db.CorrectiveActions.AsNoTracking().SingleAsync(item => item.Id == w.FirstCorrectiveActionId));
        var before = DateTime.UtcNow.AddSeconds(-2);

        var response = await SendAsync(HttpMethod.Post, $"{WorkOrders}/{w.WorkOrderId}/reject", w.Owner, new { decisionReason = "Leak persists even after rework." }, w.WorkOrderETag);
        var after = DateTime.UtcNow.AddSeconds(2);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal("CORRECTIVE_ACTION_REQUIRED", body.GetProperty("status").GetString());

        var workOrder = await WithDbAsync(db => db.WorkOrders.AsNoTracking().SingleAsync(item => item.Id == w.WorkOrderId));
        Assert.Equal(WorkOrderStatus.CorrectiveActionRequired, workOrder.Status);

        var acceptances = await WithDbAsync(db => db.CustomerAcceptances.AsNoTracking()
            .Where(item => item.WorkOrderId == w.WorkOrderId).OrderBy(item => item.AcceptanceRoundNo).ToListAsync());
        Assert.Equal(2, acceptances.Count);
        var roundTwoAcceptance = acceptances[1];
        Assert.Equal(2, roundTwoAcceptance.AcceptanceRoundNo);
        Assert.Equal(AcceptanceDecision.Reject, roundTwoAcceptance.Decision);
        Assert.Equal("Leak persists even after rework.", roundTwoAcceptance.DecisionReason);
        Assert.InRange(roundTwoAcceptance.DecidedAt, before, after);

        var correctiveActions = await WithDbAsync(db => db.CorrectiveActions.AsNoTracking()
            .Where(item => item.WorkOrderId == w.WorkOrderId).OrderBy(item => item.CycleNo).ToListAsync());
        Assert.Equal(2, correctiveActions.Count);

        var secondCycle = correctiveActions[1];
        Assert.Equal(2, secondCycle.CycleNo);
        Assert.Equal(roundTwoAcceptance.Id, secondCycle.AcceptanceId);
        Assert.Equal(CorrectiveActionStatus.Draft, secondCycle.Status);
        Assert.Null(secondCycle.OwnerTeamLeadId);
        Assert.Null(secondCycle.PlanText);
        Assert.Null(secondCycle.CorrectiveServiceVisitId);

        // The first cycle (already Approved, with its own scheduled corrective Visit) is history now — proving
        // Reject round 2 only inserts a new row and never mutates an earlier cycle's own fields.
        var firstCycleAfter = correctiveActions[0];
        Assert.Equal(firstCycleBefore.Id, firstCycleAfter.Id);
        Assert.Equal(firstCycleBefore.Status, firstCycleAfter.Status);
        Assert.Equal(firstCycleBefore.OwnerTeamLeadId, firstCycleAfter.OwnerTeamLeadId);
        Assert.Equal(firstCycleBefore.PlanText, firstCycleAfter.PlanText);
        Assert.Equal(firstCycleBefore.PlanFileAssetId, firstCycleAfter.PlanFileAssetId);
        Assert.Equal(firstCycleBefore.ApprovedBy, firstCycleAfter.ApprovedBy);
        Assert.Equal(firstCycleBefore.ApprovedAt, firstCycleAfter.ApprovedAt);
        Assert.Equal(firstCycleBefore.CorrectiveServiceVisitId, firstCycleAfter.CorrectiveServiceVisitId);
        Assert.Equal(firstCycleBefore.RowVersion, firstCycleAfter.RowVersion);

        // The Work Order read model (`WorkOrderStore.cs`'s latest-cycle-only query) must now point at cycle 2, not cycle 1.
        var read = await JsonAsync(await SendAsync(HttpMethod.Get, $"{WorkOrders}/{w.WorkOrderId}", w.Owner, null, null));
        Assert.Equal(secondCycle.Id, read.GetProperty("correctiveActionId").GetGuid());
        Assert.Equal("DRAFT", read.GetProperty("correctiveActionStatus").GetString());
        Assert.Equal(JsonValueKind.Null, read.GetProperty("correctiveServiceVisitId").ValueKind);
    }

    // ---------------- Round 2: duplicate / concurrent ----------------

    [Fact]
    public async Task ASecondDecisionOnRoundTwo_Returns409StateConflict_AndRecordsNoThirdAcceptanceRound()
    {
        var w = await AwaitingAcceptanceRoundTwoAsync();
        var first = await SendAsync(HttpMethod.Post, $"{WorkOrders}/{w.WorkOrderId}/accept", w.Owner, null, w.WorkOrderETag);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var freshETag = first.Headers.ETag!.Tag;

        var second = await SendAsync(HttpMethod.Post, $"{WorkOrders}/{w.WorkOrderId}/accept", w.Owner, null, freshETag);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("application/problem+json", second.Content.Headers.ContentType?.MediaType);
        var problem = await JsonAsync(second);
        Assert.Equal("STATE_CONFLICT", problem.GetProperty("code").GetString());

        Assert.Equal(2, await WithDbAsync(db => db.CustomerAcceptances.AsNoTracking().CountAsync(item => item.WorkOrderId == w.WorkOrderId)));
        Assert.Equal(1, await WithDbAsync(db => db.AuditHistory.AsNoTracking().CountAsync(a => a.EntityId == w.WorkOrderId && a.ActionCode == "WORK_ORDER_ACCEPTED")));
    }

    [Fact]
    public async Task TwoConcurrentDecisionsOnRoundTwo_ExactlyOneSucceeds_AndOnlyOneSecondAcceptanceRoundIsRecorded()
    {
        var w = await AwaitingAcceptanceRoundTwoAsync();

        var responses = await Task.WhenAll(
            Task.Run(() => SendAsync(HttpMethod.Post, $"{WorkOrders}/{w.WorkOrderId}/accept", w.Owner, null, w.WorkOrderETag)),
            Task.Run(() => SendAsync(HttpMethod.Post, $"{WorkOrders}/{w.WorkOrderId}/accept", w.Owner, null, w.WorkOrderETag)));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);

        var acceptances = await WithDbAsync(db => db.CustomerAcceptances.AsNoTracking()
            .Where(item => item.WorkOrderId == w.WorkOrderId).ToListAsync());
        Assert.Equal(2, acceptances.Count);
        Assert.Single(acceptances, item => item.AcceptanceRoundNo == 2);

        var workOrder = await WithDbAsync(db => db.WorkOrders.AsNoTracking().SingleAsync(item => item.Id == w.WorkOrderId));
        Assert.Equal(WorkOrderStatus.Completed, workOrder.Status);
    }

    // ---------------- Helpers ----------------

    private static JsonElement SingleVisit(JsonElement workOrderResponse) =>
        Assert.Single(workOrderResponse.GetProperty("visits").EnumerateArray());

    /// <summary>
    /// Builds a Work Order all the way through a real round-2 Submit for Acceptance: Draft -&gt; Submit -&gt;
    /// Approve -&gt; Convert -&gt; Schedule -&gt; Check-in -&gt; Check-out -&gt; Submit Work Summary -&gt;
    /// Submit for Acceptance -&gt; Reject (round 1) -&gt; Submit Corrective Plan -&gt; Approve Corrective Plan
    /// -&gt; Schedule Rework -&gt; Check-in on the corrective Visit -&gt; Check-out -&gt; Submit Work Summary
    /// (round 2) -&gt; Submit for Acceptance (round 2, same contact) — mirrors
    /// <c>ReworkCompletionEndpointsTests.BuildToCorrectiveCheckInAsync</c>/<c>ReworkReadyAsync</c> exactly, then
    /// carries the flow one step further into the round-2 Accept/Reject decision this ticket covers.
    /// </summary>
    private async Task<AwaitingAcceptanceRoundTwo> AwaitingAcceptanceRoundTwoAsync()
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

        var (id, draftETag) = await DraftAsync(owner, site);
        await WithDbAsync(async db =>
        {
            var evidence = new FileAsset(tenantId, "evidence.png", "image/png", 1024, ValidHash, $"{tenantId:N}/{Guid.NewGuid():N}", owner.UserId, DateTime.UtcNow);
            evidence.MarkClean();
            db.FileAssets.Add(evidence);
            db.RepairRequestAttachments.Add(new RepairRequestAttachment(id, evidence.Id, AttachmentFileRules.AccessScopeCode));
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
        var initialVisit = SingleVisit(await JsonAsync(scheduled));
        var initialVisitId = initialVisit.GetProperty("serviceVisitId").GetGuid();
        var initialVisitETag = $"\"{initialVisit.GetProperty("rowVersion").GetString()}\"";

        var checkedIn = await SendAsync(HttpMethod.Post, $"{Visits}/{initialVisitId}/check-in", technician, null, initialVisitETag);
        Assert.Equal(HttpStatusCode.OK, checkedIn.StatusCode);
        var current = await JsonAsync(await SendAsync(HttpMethod.Get, $"{Sessions}/current", technician, null, null));
        var sessionId = current.GetProperty("workSessionId").GetGuid();
        var sessionETag = $"\"{current.GetProperty("rowVersion").GetString()}\"";

        var checkedOut = await SendAsync(HttpMethod.Post, $"{Sessions}/{sessionId}/check-out", technician, null, sessionETag);
        Assert.Equal(HttpStatusCode.OK, checkedOut.StatusCode);
        var workOrderETag = $"\"{(await JsonAsync(checkedOut)).GetProperty("workOrderRowVersion").GetString()}\"";

        var summarySubmitted = await SendAsync(HttpMethod.Post, $"{WorkOrders}/{workOrderId}/submit-work-summary", technician,
            new { summaryText = "Replaced the pump seal.", repairOutcomeCode = "REPAIRED" }, workOrderETag);
        Assert.Equal(HttpStatusCode.OK, summarySubmitted.StatusCode);
        var reviewEtag = $"\"{(await JsonAsync(summarySubmitted)).GetProperty("rowVersion").GetString()}\"";

        var submittedForAcceptance = await SendAsync(HttpMethod.Post, $"{WorkOrders}/{workOrderId}/submit-for-acceptance", teamLead,
            new { acceptanceContactId = owner.UserId }, reviewEtag);
        Assert.Equal(HttpStatusCode.OK, submittedForAcceptance.StatusCode);
        var awaitingEtag = $"\"{(await JsonAsync(submittedForAcceptance)).GetProperty("rowVersion").GetString()}\"";

        // Round 1: a real Reject, not a seeded row.
        var rejected = await SendAsync(HttpMethod.Post, $"{WorkOrders}/{workOrderId}/reject", owner, new { decisionReason = "Leak persists." }, awaitingEtag);
        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);

        var correctiveActionId = await WithDbAsync(db => db.CorrectiveActions.AsNoTracking().Where(item => item.WorkOrderId == workOrderId).Select(item => item.Id).SingleAsync());
        var planFileAssetId = await WithDbAsync(async db =>
        {
            var plan = new FileAsset(tenantId, "plan.pdf", "application/pdf", 512, ValidHash, $"{tenantId:N}/{Guid.NewGuid():N}", teamLead.UserId, DateTime.UtcNow);
            plan.MarkClean();
            db.FileAssets.Add(plan);
            await db.SaveChangesAsync();
            return plan.Id;
        });

        var currentWorkOrderEtag = await WorkOrderETagAsync(workOrderId, teamLead);
        var submittedPlan = await SendAsync(HttpMethod.Post, $"{CorrectiveActions}/{correctiveActionId}/submit-plan", teamLead,
            new { planText = "Replace the fitting and retest.", planFileAssetId }, currentWorkOrderEtag);
        Assert.Equal(HttpStatusCode.OK, submittedPlan.StatusCode);

        currentWorkOrderEtag = await WorkOrderETagAsync(workOrderId, supervisor);
        var approvedPlan = await SendAsync(HttpMethod.Post, $"{CorrectiveActions}/{correctiveActionId}/approve-plan", supervisor, null, currentWorkOrderEtag);
        Assert.Equal(HttpStatusCode.OK, approvedPlan.StatusCode);

        var correctiveActionEtag = await CorrectiveActionETagAsync(workOrderId, coordinator);
        var reworkScheduled = await SendAsync(HttpMethod.Post, $"{CorrectiveActions}/{correctiveActionId}/schedule-rework", coordinator, new
        {
            assignedTeamId = Guid.NewGuid(),
            assignedTechnicianId = technician.UserId,
            scheduledStartAt = "2026-10-05T08:00:00Z",
            scheduledEndAt = "2026-10-05T10:00:00Z"
        }, correctiveActionEtag);
        Assert.Equal(HttpStatusCode.OK, reworkScheduled.StatusCode);
        var correctiveVisitId = (await JsonAsync(reworkScheduled)).GetProperty("correctiveServiceVisitId").GetGuid();
        var correctiveVisit = await WithDbAsync(db => db.ServiceVisits.AsNoTracking().SingleAsync(v => v.Id == correctiveVisitId));

        var correctiveCheckIn = await SendAsync(HttpMethod.Post, $"{Visits}/{correctiveVisitId}/check-in", technician, null,
            $"\"{Convert.ToBase64String(correctiveVisit.RowVersion)}\"");
        Assert.Equal(HttpStatusCode.OK, correctiveCheckIn.StatusCode);

        var reworkCurrent = await JsonAsync(await SendAsync(HttpMethod.Get, $"{Sessions}/current", technician, null, null));
        var reworkSessionId = reworkCurrent.GetProperty("workSessionId").GetGuid();
        var reworkSessionETag = $"\"{reworkCurrent.GetProperty("rowVersion").GetString()}\"";

        var reworkCheckedOut = await SendAsync(HttpMethod.Post, $"{Sessions}/{reworkSessionId}/check-out", technician, null, reworkSessionETag);
        Assert.Equal(HttpStatusCode.OK, reworkCheckedOut.StatusCode);
        var reworkWorkOrderETag = $"\"{(await JsonAsync(reworkCheckedOut)).GetProperty("workOrderRowVersion").GetString()}\"";

        // Submit Work Summary (round 2, the corrective cycle's own Visit).
        var reworkSummarySubmitted = await SendAsync(HttpMethod.Post, $"{WorkOrders}/{workOrderId}/submit-work-summary", technician,
            new { summaryText = "Replaced the fitting; retested, no leak.", repairOutcomeCode = "REPAIRED" }, reworkWorkOrderETag);
        Assert.Equal(HttpStatusCode.OK, reworkSummarySubmitted.StatusCode);
        var reworkReviewEtag = $"\"{(await JsonAsync(reworkSummarySubmitted)).GetProperty("rowVersion").GetString()}\"";

        // Submit for Acceptance (round 2) — must reuse round 1's own designated contact (owner).
        var reworkSubmittedForAcceptance = await SendAsync(HttpMethod.Post, $"{WorkOrders}/{workOrderId}/submit-for-acceptance", teamLead,
            new { acceptanceContactId = owner.UserId }, reworkReviewEtag);
        Assert.Equal(HttpStatusCode.OK, reworkSubmittedForAcceptance.StatusCode);
        var roundTwoEtag = $"\"{(await JsonAsync(reworkSubmittedForAcceptance)).GetProperty("rowVersion").GetString()}\"";

        return new AwaitingAcceptanceRoundTwo(tenantId, site, owner, workOrderId, roundTwoEtag, correctiveActionId);
    }

    private async Task<string> WorkOrderETagAsync(Guid workOrderId, Caller caller)
    {
        var response = await SendAsync(HttpMethod.Get, $"{WorkOrders}/{workOrderId}", caller, null, null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return response.Headers.ETag!.Tag;
    }

    private async Task<string> CorrectiveActionETagAsync(Guid workOrderId, Caller caller)
    {
        var body = await JsonAsync(await SendAsync(HttpMethod.Get, $"{WorkOrders}/{workOrderId}", caller, null, null));
        return $"\"{body.GetProperty("correctiveActionRowVersion").GetString()}\"";
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
}
