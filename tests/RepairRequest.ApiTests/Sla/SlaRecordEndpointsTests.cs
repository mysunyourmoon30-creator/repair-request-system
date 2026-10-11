using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RepairRequest.ApiTests.Approvals;
using RepairRequest.Application.Attachments;
using RepairRequest.Domain.Files;
using RepairRequest.Domain.MasterData;
using RepairRequest.Domain.RepairRequests;
using RepairRequest.Domain.Security;
using RepairRequest.Domain.Sla;
using RepairRequest.Infrastructure.Identity;
using RepairRequest.Infrastructure.RepairRequests;

namespace RepairRequest.ApiTests.Sla;

/// <summary>API host with its own disposable LocalDB database for the SLA-API-001 endpoint tests.</summary>
public sealed class SlaRecordApiFactory : AuthApiFactory
{
    public override string ConnectionString =>
        "Server=(localdb)\\MSSQLLocalDB;Database=RepairRequestDb_SlaRecordApiTest;Trusted_Connection=True;TrustServerCertificate=True";
}

/// <summary>
/// SLA-API-001 `GET /api/v1/sla-records/{repairRequestId}` end to end (`docs/16` D5-D7): the exact contract for ACTIVE, STOPPED and
/// NOT_TRACKED_LEGACY, the first-Submit start and the Reject / Cancel stops through the real endpoints, the non-leaking 404 for
/// missing / out-of-scope / never-submitted requests, tenant / Site / Requester scope, Administrator 403 and a read that never writes.
/// </summary>
public sealed class SlaRecordEndpointsTests : IClassFixture<SlaRecordApiFactory>
{
    private const string Requests = "/api/v1/repair-requests";
    private const string Routes = "/api/v1/approval-routes";
    private const string SlaRecords = "/api/v1/sla-records";
    private const string ValidHash = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";

    private static readonly string[] ContractFields =
        ["dueAt", "repairRequestId", "riskAt", "slaPolicyVersion", "startedAt", "stoppedAt", "stoppedReason", "trackingStatus"];

    private readonly SlaRecordApiFactory _factory;

    public SlaRecordEndpointsTests(SlaRecordApiFactory factory)
    {
        _factory = factory;
    }

    private sealed record Caller(Guid UserId, Guid TenantId, string Token);

    private sealed record Scenario(
        Guid TenantId, Site Site, Caller Admin, Caller Owner, Caller Approver, Caller Coordinator, Guid RequestId, string ETag, string Status);

    // ---------------- Start and the contract ----------------

    [Fact]
    public async Task AfterTheFirstSubmit_TheRecordIsActive_WithTheExactContract_PolicyDueAndRisk_AndNoBreachField()
    {
        var s = await SubmittedAsync();
        var submittedAt = (await _factory.WithDbAsync(db => db.RepairRequests.AsNoTracking().SingleAsync(request => request.Id == s.RequestId))).SubmittedAt!.Value;

        var response = await GetAsync(s.RequestId, s.Owner);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var body = await JsonAsync(response);
        Assert.Equal(ContractFields, body.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(s.RequestId, body.GetProperty("repairRequestId").GetGuid());
        Assert.Equal("ACTIVE", body.GetProperty("trackingStatus").GetString());
        Assert.Equal("SLA-004/v1", body.GetProperty("slaPolicyVersion").GetString());
        Assert.Equal(submittedAt, body.GetProperty("startedAt").GetDateTime().ToUniversalTime());
        Assert.Equal(submittedAt.AddHours(24), body.GetProperty("dueAt").GetDateTime().ToUniversalTime());
        Assert.Equal(submittedAt.AddHours(20), body.GetProperty("riskAt").GetDateTime().ToUniversalTime());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("stoppedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("stoppedReason").ValueKind);
        Assert.EndsWith("Z", body.GetProperty("startedAt").GetString());

        // The start wrote one record and one SYSTEM audit with the Submit command's correlation id.
        var record = await _factory.WithDbAsync(db => db.SlaRecords.AsNoTracking().SingleAsync(item => item.RepairRequestId == s.RequestId));
        var audit = await _factory.WithDbAsync(db => db.AuditHistory.AsNoTracking().SingleAsync(item => item.EntityId == record.Id));
        Assert.Equal("SLA_STARTED", audit.ActionCode);
        Assert.Equal(RepairRequest.Application.Common.SystemActors.SlaTracking, audit.ActorId);
        var submit = await _factory.WithDbAsync(db => db.AuditHistory.AsNoTracking().SingleAsync(item => item.EntityId == s.RequestId && item.ActionCode == "REPAIR_REQUEST_SUBMITTED"));
        Assert.Equal(submit.CorrelationId, audit.CorrelationId);
    }

    [Fact]
    public async Task ADraftThatWasNeverSubmitted_IsTheSameNonLeaking404_AsAMissingRequest()
    {
        var s = await SubmittedAsync();
        var (draftId, _) = await DraftAsync(s.Owner, s.Site);

        var draft = await AssertNotFoundAsync(await GetAsync(draftId, s.Owner));
        var missing = await AssertNotFoundAsync(await GetAsync(Guid.NewGuid(), s.Owner));

        Assert.Equal(missing.GetProperty("code").GetString(), draft.GetProperty("code").GetString());
        Assert.Equal(missing.GetProperty("title").GetString(), draft.GetProperty("title").GetString());
        Assert.Equal(0, await _factory.WithDbAsync(db => db.SlaRecords.CountAsync(item => item.RepairRequestId == draftId)));
    }

    [Fact]
    public async Task ASubmitThatFails_StartsNoSla()
    {
        var s = await SubmittedAsync(withRoute: false);
        var (draftId, draftETag) = await DraftAsync(s.Owner, s.Site);

        // No CLEAN photo: 422; stale ETag: 409. Neither starts an SLA.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await SendAsync(HttpMethod.Post, $"{Requests}/{draftId}/submit", s.Owner, new { }, draftETag)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(HttpMethod.Post, $"{Requests}/{draftId}/submit", s.Owner, new { }, "\"AAAAAAAAAAA=\"")).StatusCode);

        Assert.Equal(0, await _factory.WithDbAsync(db => db.SlaRecords.CountAsync(item => item.RepairRequestId == draftId)));
        AssertNotFound(await GetAsync(draftId, s.Owner));
    }

    // ---------------- Stop through the real endpoints ----------------

    [Fact]
    public async Task Reject_StopsTheRecord_AndTheReadIsStoppedWithRequestRejected()
    {
        var s = await SubmittedAsync();
        var before = await JsonAsync(await GetAsync(s.RequestId, s.Owner));

        var rejected = await SendAsync(HttpMethod.Post, $"{Requests}/{s.RequestId}/reject", s.Approver, new { reason = "Covered by the service contract" }, s.ETag);
        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);

        var body = await JsonAsync(await GetAsync(s.RequestId, s.Owner));
        Assert.Equal("STOPPED", body.GetProperty("trackingStatus").GetString());
        Assert.Equal("REQUEST_REJECTED", body.GetProperty("stoppedReason").GetString());
        Assert.False(body.GetProperty("stoppedAt").ValueKind == JsonValueKind.Null);
        Assert.Equal(before.GetProperty("startedAt").GetString(), body.GetProperty("startedAt").GetString());
        Assert.Equal(before.GetProperty("dueAt").GetString(), body.GetProperty("dueAt").GetString());
        Assert.Equal(before.GetProperty("riskAt").GetString(), body.GetProperty("riskAt").GetString());

        // A second Reject is a 409 and changes nothing.
        var again = await SendAsync(HttpMethod.Post, $"{Requests}/{s.RequestId}/reject", s.Approver, new { reason = "Again" }, rejected.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        var after = await JsonAsync(await GetAsync(s.RequestId, s.Owner));
        Assert.Equal(body.GetProperty("stoppedAt").GetString(), after.GetProperty("stoppedAt").GetString());
        Assert.Equal(1, await _factory.WithDbAsync(db => db.AuditHistory.CountAsync(item => item.ActionCode == "SLA_STOPPED" && item.TenantId == s.TenantId)));
    }

    [Fact]
    public async Task Cancel_StopsTheRecord_AndTheReadIsStoppedWithRequestCancelled()
    {
        var s = await SubmittedAsync();

        var cancelled = await SendAsync(HttpMethod.Post, $"{Requests}/{s.RequestId}/cancel", s.Owner, new { reason = "No longer needed" }, s.ETag);
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);

        var body = await JsonAsync(await GetAsync(s.RequestId, s.Owner));
        Assert.Equal("STOPPED", body.GetProperty("trackingStatus").GetString());
        Assert.Equal("REQUEST_CANCELLED", body.GetProperty("stoppedReason").GetString());
    }

    [Fact]
    public async Task ApproveAndReturnDoNotStop_AndResubmitKeepsTheOriginalStartAndDue()
    {
        var s = await SubmittedAsync();
        var original = await JsonAsync(await GetAsync(s.RequestId, s.Owner));

        var returned = await SendAsync(HttpMethod.Post, $"{Requests}/{s.RequestId}/return-for-correction", s.Approver, new { reason = "Photo does not show the fault" }, s.ETag);
        Assert.Equal(HttpStatusCode.OK, returned.StatusCode);
        var afterReturn = await JsonAsync(await GetAsync(s.RequestId, s.Owner));
        Assert.Equal("ACTIVE", afterReturn.GetProperty("trackingStatus").GetString());

        var resubmitted = await SendAsync(HttpMethod.Post, $"{Requests}/{s.RequestId}/submit", s.Owner, new { duplicateContinuationReason = "Separate fault" }, returned.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.OK, resubmitted.StatusCode);

        var afterResubmit = await JsonAsync(await GetAsync(s.RequestId, s.Owner));
        Assert.Equal("ACTIVE", afterResubmit.GetProperty("trackingStatus").GetString());
        Assert.Equal(original.GetProperty("startedAt").GetString(), afterResubmit.GetProperty("startedAt").GetString());
        Assert.Equal(original.GetProperty("dueAt").GetString(), afterResubmit.GetProperty("dueAt").GetString());
        Assert.Equal(1, await _factory.WithDbAsync(db => db.SlaRecords.CountAsync(item => item.RepairRequestId == s.RequestId)));
        Assert.Equal(1, await _factory.WithDbAsync(db => db.AuditHistory.CountAsync(item => item.ActionCode == "SLA_STARTED" && item.TenantId == s.TenantId)));
    }

    [Fact]
    public async Task ApproveDoesNotStopTheSla()
    {
        var s = await SubmittedAsync();

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Post, $"{Requests}/{s.RequestId}/approve", s.Approver, null, s.ETag)).StatusCode);

        Assert.Equal("ACTIVE", (await JsonAsync(await GetAsync(s.RequestId, s.Owner))).GetProperty("trackingStatus").GetString());
    }

    // ---------------- Legacy ----------------

    [Fact]
    public async Task APreviouslySubmittedRequestWithoutARecord_IsNotTrackedLegacy_WithNullSlaFields_AndTheReadWritesNothing()
    {
        var s = await SubmittedAsync();
        await _factory.WithDbAsync(db => db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM [sla_record] WHERE [repair_request_id] = {s.RequestId}"));
        var auditsBefore = await _factory.WithDbAsync(db => db.AuditHistory.CountAsync(item => item.TenantId == s.TenantId));

        for (var read = 0; read < 3; read++)
        {
            var response = await GetAsync(s.RequestId, s.Owner);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await JsonAsync(response);
            Assert.Equal(ContractFields, body.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
            Assert.Equal("NOT_TRACKED_LEGACY", body.GetProperty("trackingStatus").GetString());
            Assert.Equal(s.RequestId, body.GetProperty("repairRequestId").GetGuid());
            foreach (var field in new[] { "slaPolicyVersion", "startedAt", "dueAt", "riskAt", "stoppedAt", "stoppedReason" })
            {
                Assert.Equal(JsonValueKind.Null, body.GetProperty(field).ValueKind);
            }
        }

        Assert.Equal(0, await _factory.WithDbAsync(db => db.SlaRecords.CountAsync(item => item.RepairRequestId == s.RequestId)));
        Assert.Equal(auditsBefore, await _factory.WithDbAsync(db => db.AuditHistory.CountAsync(item => item.TenantId == s.TenantId)));
    }

    [Fact]
    public async Task ALegacyRequest_CanStillBeRejected_ReadsLegacyAfterwards_AndGainsNoRecord()
    {
        var s = await SubmittedAsync();
        await _factory.WithDbAsync(db => db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM [sla_record] WHERE [repair_request_id] = {s.RequestId}"));

        var rejected = await SendAsync(HttpMethod.Post, $"{Requests}/{s.RequestId}/reject", s.Approver, new { reason = "Covered by the service contract" }, s.ETag);

        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);
        Assert.Equal("NOT_TRACKED_LEGACY", (await JsonAsync(await GetAsync(s.RequestId, s.Owner))).GetProperty("trackingStatus").GetString());
        Assert.Equal(0, await _factory.WithDbAsync(db => db.SlaRecords.CountAsync(item => item.RepairRequestId == s.RequestId)));
        Assert.Equal(0, await _factory.WithDbAsync(db => db.AuditHistory.CountAsync(item => item.ActionCode == "SLA_STOPPED" && item.TenantId == s.TenantId)));
    }

    // ---------------- Authorization and scope ----------------

    [Fact]
    public async Task Administrator_Is403_Anonymous_Is401_AndEveryReadRoleInScopeIs200()
    {
        var s = await SubmittedAsync();

        await AssertProblemAsync(await GetAsync(s.RequestId, s.Admin), HttpStatusCode.Forbidden, "ACCESS_DENIED");
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(s.RequestId, null)).StatusCode);

        foreach (var role in new[] { RoleCodes.Approver, RoleCodes.Coordinator, RoleCodes.TeamLead, RoleCodes.Supervisor })
        {
            var caller = await CallerAsync(s.TenantId, [s.Site], role);
            Assert.Equal(HttpStatusCode.OK, (await GetAsync(s.RequestId, caller)).StatusCode);
        }
    }

    [Fact]
    public async Task AnotherRequester_AnotherSiteUser_AndAnotherTenant_GetTheSameNonLeaking404()
    {
        var s = await SubmittedAsync();
        var otherRequester = await CallerAsync(s.TenantId, [s.Site], RoleCodes.Requester);
        var otherSite = await _factory.WithDbAsync(async db =>
        {
            var customer = new Customer(s.TenantId, "CUST-2");
            db.Customers.Add(customer);
            var created = new Site(s.TenantId, customer.Id, "SITE-B");
            db.Sites.Add(created);
            await db.SaveChangesAsync();
            return created;
        });
        var otherSiteCoordinator = await CallerAsync(s.TenantId, [otherSite], RoleCodes.Coordinator);
        var otherTenantSite = await SiteAsync(Guid.NewGuid());
        var otherTenantCoordinator = await CallerAsync(otherTenantSite.TenantId, [otherTenantSite], RoleCodes.Coordinator);
        var noScope = await CallerAsync(s.TenantId, [], RoleCodes.Coordinator);

        var bodies = new[]
        {
            await AssertNotFoundAsync(await GetAsync(s.RequestId, otherRequester)),
            await AssertNotFoundAsync(await GetAsync(s.RequestId, otherSiteCoordinator)),
            await AssertNotFoundAsync(await GetAsync(s.RequestId, otherTenantCoordinator)),
            await AssertNotFoundAsync(await GetAsync(s.RequestId, noScope)),
            await AssertNotFoundAsync(await GetAsync(Guid.NewGuid(), s.Owner)),
        };

        Assert.Single(bodies.Select(body => body.GetProperty("code").GetString()).Distinct());
        Assert.Single(bodies.Select(body => body.GetProperty("title").GetString()).Distinct());
    }

    [Fact]
    public async Task OnlyGetIsSupported_NoWriteVerbExists()
    {
        var s = await SubmittedAsync();

        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Patch, HttpMethod.Delete })
        {
            var response = await SendAsync(method, $"{SlaRecords}/{s.RequestId}", s.Admin, new { }, null);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        }

        Assert.Equal(1, await _factory.WithDbAsync(db => db.SlaRecords.CountAsync(item => item.RepairRequestId == s.RequestId)));
    }

    // ---------------- Helpers ----------------

    private async Task<Scenario> SubmittedAsync(bool withRoute = true)
    {
        var tenantId = Guid.NewGuid();
        var site = await SiteAsync(tenantId);
        var admin = await CallerAsync(tenantId, [], RoleCodes.Administrator);
        var owner = await CallerAsync(tenantId, [site], RoleCodes.Requester);
        var approver = await CallerAsync(tenantId, [site], RoleCodes.Approver);
        var coordinator = await CallerAsync(tenantId, [site], RoleCodes.Coordinator);
        if (withRoute)
        {
            Assert.Equal(HttpStatusCode.Created, (await SendAsync(HttpMethod.Post, Routes, admin, new { requestCategoryCode = "ELECTRICAL", siteId = site.Id, approverRoleCode = "APPROVER" }, null)).StatusCode);
        }

        var (id, draftETag) = await DraftAsync(owner, site);
        await _factory.WithDbAsync(async db =>
        {
            var file = new FileAsset(tenantId, "evidence.png", "image/png", 1024, ValidHash, $"{tenantId:N}/{Guid.NewGuid():N}", owner.UserId, DateTime.UtcNow);
            file.MarkClean();
            db.FileAssets.Add(file);
            db.RepairRequestAttachments.Add(new RepairRequestAttachment(id, file.Id, AttachmentFileRules.AccessScopeCode));
            await db.SaveChangesAsync();
        });

        var submitted = await SendAsync(HttpMethod.Post, $"{Requests}/{id}/submit", owner, new { }, draftETag);
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
        var status = (await JsonAsync(submitted)).GetProperty("status").GetString()!;
        return new Scenario(tenantId, site, admin, owner, approver, coordinator, id, submitted.Headers.ETag!.Tag, status);
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

    private Task<HttpResponseMessage> GetAsync(Guid repairRequestId, Caller? caller) =>
        SendAsync(HttpMethod.Get, $"{SlaRecords}/{repairRequestId}", caller, null, null);

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

    private static void AssertNotFound(HttpResponseMessage response) => Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

    private static async Task<JsonElement> AssertNotFoundAsync(HttpResponseMessage response) =>
        await AssertProblemAsync(response, HttpStatusCode.NotFound, "NOT_FOUND");

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
