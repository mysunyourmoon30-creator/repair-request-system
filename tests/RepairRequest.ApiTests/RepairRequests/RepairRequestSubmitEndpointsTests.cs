using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RepairRequest.Application.Attachments;
using RepairRequest.Application.RepairRequests;
using RepairRequest.Domain.Files;
using RepairRequest.Domain.MasterData;
using RepairRequest.Domain.RepairRequests;
using RepairRequest.Domain.Security;
using RepairRequest.Infrastructure.Identity;
using RepairRequest.Infrastructure.Persistence;
using RepairRequest.Infrastructure.RepairRequests;

namespace RepairRequest.ApiTests.RepairRequests;

/// <summary>API host with its own disposable LocalDB database for the Repair Request Submit endpoint tests.</summary>
public sealed class RepairRequestSubmitApiFactory : AuthApiFactory
{
    public override string ConnectionString =>
        "Server=(localdb)\\MSSQLLocalDB;Database=RepairRequestDb_SubmitApiTest;Trusted_Connection=True;TrustServerCertificate=True";
}

/// <summary>
/// RR-API-005 Submit end to end (UC-RR-002; ST-RR-002; FR-02; BR-01/14/16; D-12; TC-RR-003/004; TC-SEC-001/002;
/// DEC-PRE-S1-007-01..12): REQUESTER owner with If-Match, deterministic validation, CLEAN-photo evidence, BR-14 duplicate
/// warning with count-only 422 and continuation reason, per-tenant Request No, SLA start marker, success-only audit,
/// concurrency and non-leaking scope.
/// </summary>
public sealed partial class RepairRequestSubmitEndpointsTests : IClassFixture<RepairRequestSubmitApiFactory>
{
    private const string Collection = "/api/v1/repair-requests";
    private const string ValidHash = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";

    private readonly RepairRequestSubmitApiFactory _factory;

    public RepairRequestSubmitEndpointsTests(RepairRequestSubmitApiFactory factory)
    {
        _factory = factory;
    }

    public static TheoryData<string> NonRequesterRoles =>
    [
        RoleCodes.Approver,
        RoleCodes.Coordinator,
        RoleCodes.Technician,
        RoleCodes.TeamLead,
        RoleCodes.Supervisor,
        RoleCodes.Administrator
    ];

    private sealed record Caller(Guid UserId, Guid TenantId, string Token);

    /// <summary>Tenant with active Site A (active EQ-A1), unassigned Site B; another tenant with Site O. Lookups are seeded for both.</summary>
    private sealed record World(Guid TenantId, Site SiteA, Equipment EquipmentA1, Site SiteB, Guid OtherTenantId, Site OtherTenantSite);

    // ---------------- Success ----------------

    [Fact]
    public async Task Submit_ValidDraft_Returns200Submitted_WithRequestNo_SlaStartMarker_ETag_AndAudit()
    {
        var world = await WorldAsync();
        var requester = await CallerAsync(world.TenantId, [world.SiteA], RoleCodes.Requester);
        var (id, etag) = await CompleteDraftAsync(requester, world);
        await AttachAsync(requester, id, "image/png", MalwareScanStatus.Clean);
        var correlationId = Guid.NewGuid();
        var before = DateTime.UtcNow.AddSeconds(-1);

        var response = await SubmitAsync(requester, id, etag, correlationId: correlationId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal("SUBMITTED", body.GetProperty("status").GetString());
        var requestNo = body.GetProperty("requestNo").GetString()!;
        Assert.Equal($"RR-{DateTime.UtcNow.Year:D4}-000001", requestNo);
        Assert.Matches(RequestNoPattern(), requestNo);
        Assert.Equal("ELECTRICAL", body.GetProperty("requestCategoryCode").GetString());
        Assert.Equal("HIGH", body.GetProperty("priorityCode").GetString());
        Assert.Equal(requester.UserId, body.GetProperty("requestContactId").GetGuid());
        Assert.Equal($"\"{body.GetProperty("rowVersion").GetString()}\"", response.Headers.ETag!.Tag);
        Assert.NotEqual(etag, response.Headers.ETag.Tag);

        var stored = await WithDbAsync(db => db.RepairRequests.AsNoTracking().SingleAsync(request => request.Id == id));
        Assert.Equal(RepairRequestStatus.Submitted, stored.Status);
        Assert.Equal(requestNo, stored.RequestNo);
        Assert.Equal(requester.UserId, stored.SubmittedBy);
        Assert.InRange(stored.SubmittedAt!.Value, before, DateTime.UtcNow.AddSeconds(1));
        Assert.Null(stored.DuplicateContinuationReason);

        var audit = Assert.Single(await AuditsAsync(id, RepairRequestAudit.SubmittedAction));
        Assert.Equal("DRAFT", audit.FromState);
        Assert.Equal("SUBMITTED", audit.ToState);
        Assert.Equal(requester.UserId, audit.ActorId);
        Assert.Equal(correlationId, audit.CorrelationId);
        Assert.Contains(requestNo, audit.NewValueJson);
    }

    [Fact]
    public async Task Submit_NumbersAreSequentialPerTenant_AndAnotherTenantStartsAtOne()
    {
        var world = await WorldAsync();
        var requester = await CallerAsync(world.TenantId, [world.SiteA], RoleCodes.Requester);
        var otherTenantRequester = await CallerAsync(world.OtherTenantId, [world.OtherTenantSite], RoleCodes.Requester);
        var year = DateTime.UtcNow.Year;

        var first = await SubmittedRequestNoAsync(requester, world, world.SiteA, "ELECTRICAL");
        var second = await SubmittedRequestNoAsync(requester, world, world.SiteA, "PLUMBING");
        var otherTenant = await SubmittedRequestNoAsync(otherTenantRequester, world, world.OtherTenantSite, "ELECTRICAL");

        Assert.Equal($"RR-{year:D4}-000001", first);
        Assert.Equal($"RR-{year:D4}-000002", second);
        Assert.Equal($"RR-{year:D4}-000001", otherTenant);
    }

    [Fact]
    public async Task Submit_WithoutBody_IsAccepted()
    {
        var world = await WorldAsync();
        var requester = await CallerAsync(world.TenantId, [world.SiteA], RoleCodes.Requester);
        var (id, etag) = await CompleteDraftAsync(requester, world);
        await AttachAsync(requester, id, "image/jpeg", MalwareScanStatus.Clean);

        var response = await SendAsync(HttpMethod.Post, $"{Collection}/{id}/submit", requester, body: null, ifMatch: etag);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---------------- Authorization / scope ----------------

    [Fact]
    public async Task Submit_WithoutToken_Returns401()
    {
        var response = await SendAsync(HttpMethod.Post, $"{Collection}/{Guid.NewGuid()}/submit", null, new { }, "\"AAAAAAAAB9E=\"");

        await AssertProblemAsync(response, HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
    }

    [Theory]
    [MemberData(nameof(NonRequesterRoles))]
    public async Task Submit_ByNonRequesterRole_Returns403(string role)
    {
        var world = await WorldAsync();
        var owner = await CallerAsync(world.TenantId, [world.SiteA], RoleCodes.Requester);
        var (id, etag) = await CompleteDraftAsync(owner, world);
        var caller = await CallerAsync(world.TenantId, [world.SiteA], role);

        var response = await SubmitAsync(caller, id, etag);

        await AssertProblemAsync(response, HttpStatusCode.Forbidden, "ACCESS_DENIED");
        await AssertNotSubmittedAsync(id);
    }

    [Fact]
    public async Task Submit_OfAnotherUsersOrTenantsDraft_OrAfterSiteAssignmentRemoved_Returns404IdenticalToUnknown()
    {
        var world = await WorldAsync();
        var owner = await CallerAsync(world.TenantId, [world.SiteA], RoleCodes.Requester);
        var sameSiteRequester = await CallerAsync(world.TenantId, [world.SiteA], RoleCodes.Requester);
        var approverRequester = await CallerAsync(world.TenantId, [world.SiteA], RoleCodes.Approver, RoleCodes.Requester);
        var otherTenantRequester = await CallerAsync(world.OtherTenantId, [world.OtherTenantSite], RoleCodes.Requester);
        var (id, etag) = await CompleteDraftAsync(owner, world);
        await AttachAsync(owner, id, "image/png", MalwareScanStatus.Clean);

        var expected = await NotFoundShapeAsync(await SubmitAsync(owner, Guid.NewGuid(), etag));
        Assert.Equal(expected, await NotFoundShapeAsync(await SubmitAsync(sameSiteRequester, id, etag)));
        Assert.Equal(expected, await NotFoundShapeAsync(await SubmitAsync(approverRequester, id, etag)));
        Assert.Equal(expected, await NotFoundShapeAsync(await SubmitAsync(otherTenantRequester, id, etag)));

        await WithDbAsync(db => db.UserSiteScopes.Where(scope => scope.UserId == owner.UserId).ExecuteDeleteAsync());
        Assert.Equal(expected, await NotFoundShapeAsync(await SubmitAsync(owner, id, etag)));
        await AssertNotSubmittedAsync(id);
    }

    [Fact]
    public async Task AdministratorRequester_SubmitsOwnDraftOnAssignedSite_ButAdministratorAloneIsDenied()
    {
        var world = await WorldAsync();
        var adminRequester = await CallerAsync(world.TenantId, [world.SiteA], RoleCodes.Administrator, RoleCodes.Requester);
        var administrator = await CallerAsync(world.TenantId, [world.SiteA], RoleCodes.Administrator);
        var (id, etag) = await CompleteDraftAsync(adminRequester, world);
        await AttachAsync(adminRequester, id, "image/png", MalwareScanStatus.Clean);

        await AssertProblemAsync(await SubmitAsync(administrator, id, etag), HttpStatusCode.Forbidden, "ACCESS_DENIED");
        Assert.Equal(HttpStatusCode.OK, (await SubmitAsync(adminRequester, id, etag)).StatusCode);
    }

    // ---------------- Concurrency / state ----------------

    [Fact]
    public async Task Submit_WithoutIfMatch_Returns400_AndWithStaleETag_Returns409()
    {
        var world = await WorldAsync();
        var requester = await CallerAsync(world.TenantId, [world.SiteA], RoleCodes.Requester);
        var (id, etag) = await CompleteDraftAsync(requester, world);
        await AttachAsync(requester, id, "image/png", MalwareScanStatus.Clean);
        var edited = await SendAsync(HttpMethod.Patch, $"{Collection}/{id}", requester, CompleteBody(requester, world, description: "Edited"), etag);
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);

        await AssertProblemAsync(await SubmitAsync(requester, id, null), HttpStatusCode.BadRequest, "BAD_REQUEST");
        await AssertProblemAsync(await SubmitAsync(requester, id, etag), HttpStatusCode.Conflict, "CONCURRENCY_CONFLICT");
        await AssertNotSubmittedAsync(id);
    }

    [Fact]
    public async Task Submit_AlreadySubmitted_Returns409StateConflict_AndKeepsTheOriginalNumber()
    {
        var world = await WorldAsync();
        var requester = await CallerAsync(world.TenantId, [world.SiteA], RoleCodes.Requester);
        var (id, etag) = await CompleteDraftAsync(requester, world);
        await AttachAsync(requester, id, "image/png", MalwareScanStatus.Clean);

        var first = await SubmitAsync(requester, id, etag);
        var number = (await JsonAsync(first)).GetProperty("requestNo").GetString();
        var again = await SubmitAsync(requester, id, first.Headers.ETag!.Tag);

        await AssertProblemAsync(again, HttpStatusCode.Conflict, "STATE_CONFLICT");
        Assert.Equal(number, await WithDbAsync(db => db.RepairRequests.Where(request => request.Id == id).Select(request => request.RequestNo).SingleAsync()));
        Assert.Single(await AuditsAsync(id, RepairRequestAudit.SubmittedAction));
    }

    [Fact]
    public async Task Submit_TwoConcurrentRequestsWithTheSameETag_ExactlyOneSucceeds_WithOneNumberAndOneAudit()
    {
        var world = await WorldAsync();
        var requester = await CallerAsync(world.TenantId, [world.SiteA], RoleCodes.Requester);
        var (id, etag) = await CompleteDraftAsync(requester, world);
        await AttachAsync(requester, id, "image/png", MalwareScanStatus.Clean);

        var responses = await Task.WhenAll(
            Task.Run(() => SubmitAsync(requester, id, etag)),
            Task.Run(() => SubmitAsync(requester, id, etag)));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        Assert.Single(await AuditsAsync(id, RepairRequestAudit.SubmittedAction));
        Assert.Equal(1, await WithDbAsync(db => db.RequestNumberCounters.Where(counter => counter.TenantId == world.TenantId).SumAsync(counter => counter.LastValue)));
        Assert.Single(await WithDbAsync(db => db.RepairRequests.Where(request => request.TenantId == world.TenantId && request.RequestNo != null).ToListAsync()));
    }

    // ---------------- Validation ----------------

    [Fact]
    public async Task Submit_IncompleteDraft_Returns422WithEveryMissingField_AndLeavesNoTrace()
    {
        var world = await WorldAsync();
        var requester = await CallerAsync(world.TenantId, [world.SiteA], RoleCodes.Requester);
        var created = await SendAsync(HttpMethod.Post, Collection, requester, new { description = "Only text" });
        var id = (await JsonAsync(created)).GetProperty("id").GetGuid();

        var response = await SubmitAsync(requester, id, created.Headers.ETag!.Tag);

        var body = await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
        var keys = body.GetProperty("errors").EnumerateObject().Select(property => property.Name).Order().ToArray();
        Assert.Equal(new[] { "attachments", "preferredEndAt", "preferredStartAt", "priorityCode", "requestCategoryCode", "requestContactId", "siteId" }, keys);
        Assert.False(body.TryGetProperty("duplicateCount", out _));
        await AssertNotSubmittedAsync(id);
        Assert.False(await WithDbAsync(db => db.RequestNumberCounters.AnyAsync(counter => counter.TenantId == world.TenantId)));
    }

    [Fact]
    public async Task Submit_WhenLookupsBecameInactive_OrContactLostSiteAssignment_Returns422()
    {
        var world = await WorldAsync();
        var requester = await CallerAsync(world.TenantId, [world.SiteA], RoleCodes.Requester);
        var technician = await CallerAsync(world.TenantId, [world.SiteA], RoleCodes.Technician);
        var (id, etag) = await CompleteDraftAsync(requester, world, contactId: technician.UserId);
        await AttachAsync(requester, id, "image/png", MalwareScanStatus.Clean);
        await WithDbAsync(async db =>
        {
            await db.RequestCategories.Where(category => category.TenantId == world.TenantId && category.Code == "ELECTRICAL")
                .ExecuteUpdateAsync(setters => setters.SetProperty(category => category.Status, MasterDataStatus.Inactive));
            await db.RequestPriorities.Where(priority => priority.TenantId == world.TenantId && priority.Code == "HIGH")
                .ExecuteUpdateAsync(setters => setters.SetProperty(priority => priority.Status, MasterDataStatus.Inactive));
            await db.UserSiteScopes.Where(scope => scope.UserId == technician.UserId).ExecuteDeleteAsync();
        });

        var body = await AssertProblemAsync(await SubmitAsync(requester, id, etag), HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");

        var errors = body.GetProperty("errors");
        Assert.True(errors.TryGetProperty("requestCategoryCode", out _));
        Assert.True(errors.TryGetProperty("priorityCode", out _));
        Assert.True(errors.TryGetProperty("requestContactId", out _));
        Assert.False(errors.TryGetProperty("attachments", out _));
        await AssertNotSubmittedAsync(id);
    }

    public static TheoryData<string, string[]> MissingPhotoCases => new()
    {
        { "no attachment", [] },
        { "PENDING only", ["image/png:PENDING"] },
        { "FAILED only", ["image/jpeg:FAILED"] },
        { "CLEAN PDF only", ["application/pdf:CLEAN"] },
        { "PENDING and FAILED", ["image/png:PENDING", "image/jpeg:FAILED"] }
    };

    [Theory]
    [MemberData(nameof(MissingPhotoCases))]
    public async Task Submit_WithoutCleanPhoto_Returns422OnAttachments_AndStaysDraft(string scenario, string[] files)
    {
        _ = scenario;
        var world = await WorldAsync();
        var requester = await CallerAsync(world.TenantId, [world.SiteA], RoleCodes.Requester);
        var (id, etag) = await CompleteDraftAsync(requester, world);
        foreach (var file in files)
        {
            var parts = file.Split(':');
            await AttachAsync(requester, id, parts[0], Enum.Parse<MalwareScanStatus>(parts[1], ignoreCase: true));
        }

        var body = await AssertProblemAsync(await SubmitAsync(requester, id, etag), HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");

        Assert.Equal(new[] { "attachments" }, body.GetProperty("errors").EnumerateObject().Select(property => property.Name));
        await AssertNotSubmittedAsync(id);
    }

    [Theory]
    [InlineData("PENDING")]
    [InlineData("FAILED")]
    public async Task Submit_CleanPhotoWithAnAdditionalPendingOrFailedAttachment_Succeeds(string otherStatus)
    {
        var world = await WorldAsync();
        var requester = await CallerAsync(world.TenantId, [world.SiteA], RoleCodes.Requester);
        var (id, etag) = await CompleteDraftAsync(requester, world);
        await AttachAsync(requester, id, "image/jpeg", MalwareScanStatus.Clean);
        await AttachAsync(requester, id, "image/png", Enum.Parse<MalwareScanStatus>(otherStatus, ignoreCase: true));

        var response = await SubmitAsync(requester, id, etag);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---------------- Duplicate warning ----------------

    [Fact]
    public async Task Submit_Duplicate_WithoutReason_Returns422CountOnly_ThenWithReasonSucceeds_AndAuditsTheReason()
    {
        var world = await WorldAsync();
        var firstRequester = await CallerAsync(world.TenantId, [world.SiteA], RoleCodes.Requester);
        var secondRequester = await CallerAsync(world.TenantId, [world.SiteA], RoleCodes.Requester);
        var existingNo = await SubmittedRequestNoAsync(firstRequester, world, world.SiteA, "ELECTRICAL");
        var (id, etag) = await CompleteDraftAsync(secondRequester, world);
        await AttachAsync(secondRequester, id, "image/png", MalwareScanStatus.Clean);

        var warned = await SubmitAsync(secondRequester, id, etag, reason: "   ");

        var raw = await warned.Content.ReadAsStringAsync();
        var body = await AssertProblemAsync(warned, HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
        Assert.Equal(1, body.GetProperty("duplicateCount").GetInt32());
        Assert.Equal(new[] { "duplicateContinuationReason" }, body.GetProperty("errors").EnumerateObject().Select(property => property.Name));
        Assert.DoesNotContain(existingNo, raw);
        await AssertNotSubmittedAsync(id);

        var continued = await SubmitAsync(secondRequester, id, etag, reason: "  Different fault on the same pump  ");

        Assert.Equal(HttpStatusCode.OK, continued.StatusCode);
        var stored = await WithDbAsync(db => db.RepairRequests.AsNoTracking().SingleAsync(request => request.Id == id));
        Assert.Equal("Different fault on the same pump", stored.DuplicateContinuationReason);
        var audit = Assert.Single(await AuditsAsync(id, RepairRequestAudit.SubmittedAction));
        Assert.Equal("Different fault on the same pump", audit.Reason);
        Assert.Contains("\"duplicateCount\":1", audit.NewValueJson);
    }

    [Fact]
    public async Task Submit_EmptyEquipmentMatchesOnlyEmptyEquipment_AndNullIsNeverAWildcard()
    {
        var world = await WorldAsync();
        var requester = await CallerAsync(world.TenantId, [world.SiteA], RoleCodes.Requester);
        await SubmittedRequestNoAsync(requester, world, world.SiteA, "ELECTRICAL", world.EquipmentA1.Id);

        var (withoutEquipment, withoutEtag) = await CompleteDraftAsync(requester, world);
        await AttachAsync(requester, withoutEquipment, "image/png", MalwareScanStatus.Clean);
        Assert.Equal(HttpStatusCode.OK, (await SubmitAsync(requester, withoutEquipment, withoutEtag)).StatusCode);

        var (withEquipment, withEtag) = await CompleteDraftAsync(requester, world, world.EquipmentA1.Id);
        await AttachAsync(requester, withEquipment, "image/png", MalwareScanStatus.Clean);
        var body = await AssertProblemAsync(await SubmitAsync(requester, withEquipment, withEtag), HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
        Assert.Equal(1, body.GetProperty("duplicateCount").GetInt32());
    }

    [Fact]
    public async Task Submit_RejectedOrCancelledRequestsAndOtherTenantsDoNotCount_ButUnderReviewDoes()
    {
        var world = await WorldAsync();
        var requester = await CallerAsync(world.TenantId, [world.SiteA], RoleCodes.Requester);
        var otherTenantRequester = await CallerAsync(world.OtherTenantId, [world.OtherTenantSite], RoleCodes.Requester);
        await SubmittedRequestNoAsync(otherTenantRequester, world, world.OtherTenantSite, "ELECTRICAL");
        var rejectedNo = await SubmittedRequestNoAsync(requester, world, world.SiteA, "ELECTRICAL");
        await WithDbAsync(db => db.RepairRequests.Where(request => request.RequestNo == rejectedNo && request.TenantId == world.TenantId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(request => request.Status, RepairRequestStatus.Rejected)));
        var (cancelledId, cancelledEtag) = await CompleteDraftAsync(requester, world);
        await AttachAsync(requester, cancelledId, "image/png", MalwareScanStatus.Clean);
        Assert.Equal(HttpStatusCode.OK, (await SubmitAsync(requester, cancelledId, cancelledEtag)).StatusCode);
        await WithDbAsync(db => db.RepairRequests.Where(request => request.Id == cancelledId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(request => request.Status, RepairRequestStatus.Cancelled)));

        var (id, etag) = await CompleteDraftAsync(requester, world);
        await AttachAsync(requester, id, "image/png", MalwareScanStatus.Clean);
        Assert.Equal(HttpStatusCode.OK, (await SubmitAsync(requester, id, etag, reason: "Reason sent without a duplicate")).StatusCode);
        Assert.Null(await WithDbAsync(db => db.RepairRequests.Where(request => request.Id == id).Select(request => request.DuplicateContinuationReason).SingleAsync()));

        await WithDbAsync(db => db.RepairRequests.Where(request => request.Id == id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(request => request.Status, RepairRequestStatus.UnderReview)));
        var (next, nextEtag) = await CompleteDraftAsync(requester, world);
        await AttachAsync(requester, next, "image/png", MalwareScanStatus.Clean);
        var body = await AssertProblemAsync(await SubmitAsync(requester, next, nextEtag), HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
        Assert.Equal(1, body.GetProperty("duplicateCount").GetInt32());
    }

    // ---------------- Draft fields used by Submit ----------------

    [Fact]
    public async Task Draft_AcceptsLookupsAndContact_StoresCanonicalCodes_AndRejectsInvalidSelections()
    {
        var world = await WorldAsync();
        var requester = await CallerAsync(world.TenantId, [world.SiteA], RoleCodes.Requester);
        var administratorOnly = await CallerAsync(world.TenantId, [world.SiteA], RoleCodes.Administrator);
        var unassigned = await CallerAsync(world.TenantId, [world.SiteB], RoleCodes.Requester);

        var created = await SendAsync(HttpMethod.Post, Collection, requester, new
        {
            siteId = world.SiteA.Id,
            requestCategoryCode = "electrical",
            priorityCode = " high ",
            requestContactId = requester.UserId
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await JsonAsync(created);
        Assert.Equal("ELECTRICAL", body.GetProperty("requestCategoryCode").GetString());
        Assert.Equal("HIGH", body.GetProperty("priorityCode").GetString());

        async Task AssertRejected(object draft, string field)
        {
            var response = await SendAsync(HttpMethod.Post, Collection, requester, draft);
            var problem = await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
            Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _), field);
        }

        await AssertRejected(new { requestCategoryCode = "NOT-A-CATEGORY" }, "requestCategoryCode");
        await AssertRejected(new { priorityCode = "P1" }, "priorityCode");
        await AssertRejected(new { requestContactId = requester.UserId }, "requestContactId");
        await AssertRejected(new { siteId = world.SiteA.Id, requestContactId = administratorOnly.UserId }, "requestContactId");
        await AssertRejected(new { siteId = world.SiteA.Id, requestContactId = unassigned.UserId }, "requestContactId");
        await AssertRejected(new { siteId = world.SiteA.Id, requestContactId = Guid.NewGuid() }, "requestContactId");
    }

    [Fact]
    public async Task Draft_ContactFromAnotherTenant_IsRejected_IdenticallyToUnknown_AndNothingIsCreated()
    {
        // DEC-PRE-S1-007-04: existence + tenant + Site scope stay enforced while only the ACTIVE check is deferred (REQ-FU-USR-001).
        var world = await WorldAsync();
        var requester = await CallerAsync(world.TenantId, [world.SiteA], RoleCodes.Requester);
        var otherTenantUser = await CallerAsync(world.OtherTenantId, [world.OtherTenantSite], RoleCodes.Requester);

        async Task<JsonElement> ContactErrorAsync(Guid contactId)
        {
            var response = await SendAsync(HttpMethod.Post, Collection, requester, CompleteBody(requester, world, contactId: contactId));
            var problem = await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
            var errors = problem.GetProperty("errors");
            Assert.Equal(new[] { "requestContactId" }, errors.EnumerateObject().Select(property => property.Name));
            return errors.GetProperty("requestContactId");
        }

        var crossTenant = await ContactErrorAsync(otherTenantUser.UserId);
        var unknown = await ContactErrorAsync(Guid.NewGuid());

        Assert.Equal(unknown.GetRawText(), crossTenant.GetRawText());
        Assert.False(await WithDbAsync(db => db.RepairRequests.AnyAsync(request => request.CreatedBy == requester.UserId)));
    }

    // ---------------- Helpers ----------------

    [GeneratedRegex(@"^RR-\d{4}-\d{6}$")]
    private static partial Regex RequestNoPattern();

    private HttpClient CreateClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

    private async Task<World> WorldAsync()
    {
        var tenantId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();

        var world = await WithDbAsync(async db =>
        {
            var customer = new Customer(tenantId, "CUST-1");
            var otherCustomer = new Customer(otherTenantId, "CUST-1");
            db.Customers.AddRange(customer, otherCustomer);

            var siteA = new Site(tenantId, customer.Id, "SITE-A");
            var siteB = new Site(tenantId, customer.Id, "SITE-B");
            var otherTenantSite = new Site(otherTenantId, otherCustomer.Id, "SITE-A");
            db.Sites.AddRange(siteA, siteB, otherTenantSite);

            var equipmentA1 = new Equipment(tenantId, siteA.Id, "EQ-A1");
            db.Equipment.Add(equipmentA1);

            await db.SaveChangesAsync();
            return new World(tenantId, siteA, equipmentA1, siteB, otherTenantId, otherTenantSite);
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
            await WithDbAsync(db =>
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

    private static object CompleteBody(
        Caller owner,
        World world,
        Site? site = null,
        Guid? equipmentId = null,
        string category = "ELECTRICAL",
        Guid? contactId = null,
        string description = "Pump leaking") =>
        new
        {
            siteId = (site ?? world.SiteA).Id,
            equipmentId,
            requestCategoryCode = category,
            priorityCode = "HIGH",
            requestContactId = contactId ?? owner.UserId,
            description,
            preferredStartAt = "2026-09-20T08:00:00Z",
            preferredEndAt = "2026-09-20T10:00:00Z"
        };

    private async Task<(Guid Id, string ETag)> CompleteDraftAsync(
        Caller owner,
        World world,
        Guid? equipmentId = null,
        string category = "ELECTRICAL",
        Guid? contactId = null,
        Site? site = null)
    {
        var response = await SendAsync(HttpMethod.Post, Collection, owner, CompleteBody(owner, world, site, equipmentId, category, contactId));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return ((await JsonAsync(response)).GetProperty("id").GetGuid(), response.Headers.ETag!.Tag);
    }

    /// <summary>Creates, evidences and submits a request through the API; returns its Request No.</summary>
    private async Task<string> SubmittedRequestNoAsync(Caller owner, World world, Site site, string category, Guid? equipmentId = null)
    {
        var (id, etag) = await CompleteDraftAsync(owner, world, equipmentId, category, site: site);
        await AttachAsync(owner, id, "image/png", MalwareScanStatus.Clean);
        var response = await SubmitAsync(owner, id, etag);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await JsonAsync(response)).GetProperty("requestNo").GetString()!;
    }

    private Task AttachAsync(Caller owner, Guid requestId, string mimeType, MalwareScanStatus status) =>
        WithDbAsync(async db =>
        {
            var file = new FileAsset(owner.TenantId, "evidence.bin", mimeType, 1024, ValidHash, $"{owner.TenantId:N}/{Guid.NewGuid():N}", owner.UserId, DateTime.UtcNow);
            if (status == MalwareScanStatus.Clean)
            {
                file.MarkClean();
            }
            else if (status == MalwareScanStatus.Failed)
            {
                file.MarkFailed();
            }

            db.FileAssets.Add(file);
            db.RepairRequestAttachments.Add(new RepairRequestAttachment(requestId, file.Id, AttachmentFileRules.AccessScopeCode));
            await db.SaveChangesAsync();
        });

    private Task<HttpResponseMessage> SubmitAsync(Caller caller, Guid id, string? etag, string? reason = null, Guid? correlationId = null) =>
        SendAsync(HttpMethod.Post, $"{Collection}/{id}/submit", caller, new { duplicateContinuationReason = reason }, etag, correlationId);

    private async Task AssertNotSubmittedAsync(Guid id)
    {
        var stored = await WithDbAsync(db => db.RepairRequests.AsNoTracking().SingleAsync(request => request.Id == id));
        Assert.Equal(RepairRequestStatus.Draft, stored.Status);
        Assert.Null(stored.RequestNo);
        Assert.Null(stored.SubmittedAt);
        Assert.Null(stored.SubmittedBy);
        Assert.Empty(await AuditsAsync(id, RepairRequestAudit.SubmittedAction));
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, Caller? caller, object? body = null, string? ifMatch = null, Guid? correlationId = null)
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

        if (correlationId is not null)
        {
            request.Headers.Add("X-Correlation-ID", correlationId.Value.ToString());
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

    private static async Task<string> NotFoundShapeAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var node = System.Text.Json.Nodes.JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        Assert.Equal("NOT_FOUND", (string?)node["code"]);
        node.Remove("correlationId");
        node.Remove("traceId");
        return node.ToJsonString();
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

    private Task<List<RepairRequest.Domain.Auditing.AuditHistory>> AuditsAsync(Guid entityId, string actionCode) =>
        WithDbAsync(db => db.AuditHistory.AsNoTracking().Where(audit => audit.EntityId == entityId && audit.ActionCode == actionCode).ToListAsync());
}
