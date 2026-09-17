using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RepairRequest.Domain.MasterData;
using RepairRequest.Domain.RepairRequests;
using RepairRequest.Domain.Security;
using RepairRequest.Infrastructure.Identity;
using RepairRequest.Infrastructure.Persistence;

namespace RepairRequest.ApiTests.RepairRequests;

/// <summary>API host with its own disposable LocalDB database for the Repair Request Draft endpoint tests.</summary>
public sealed class RepairRequestApiFactory : AuthApiFactory
{
    public override string ConnectionString =>
        "Server=(localdb)\\MSSQLLocalDB;Database=RepairRequestDb_RepairRequestApiTest;Trusted_Connection=True;TrustServerCertificate=True";
}

/// <summary>
/// Repair Request Draft endpoints end to end (UC-RR-001; ST-RR-001; RR-API-001/002/004; TC-RR-001; TC-SEC-001/002):
/// REQUESTER-only create, owner-only DRAFT edit with If-Match, S1-003 scope for detail (decision E3), Site scope and
/// active masters, Equipment belongs to Site, whole-Draft revalidation (decision E2) and create/edit audit.
/// </summary>
public sealed class RepairRequestDraftEndpointsTests : IClassFixture<RepairRequestApiFactory>
{
    private const string Collection = "/api/v1/repair-requests";

    private readonly RepairRequestApiFactory _factory;

    public RepairRequestDraftEndpointsTests(RepairRequestApiFactory factory)
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

    /// <summary>
    /// Tenant with Customer C: active Site A (active EQ-A1, inactive EQ-A2), active Site B (EQ-B1, not assigned), inactive
    /// Site X; plus another tenant's Site O. Requesters are assigned to Sites A and X.
    /// </summary>
    private sealed record World(Guid TenantId, Site SiteA, Equipment EquipmentA1, Equipment InactiveEquipmentA2, Site SiteB, Equipment EquipmentB1, Site InactiveSiteX, Site OtherTenantSite);

    // ---------------- Create ----------------

    [Fact]
    public async Task Create_EmptyDraft_Returns201OwnedByCaller_WithoutRequestNo_AndIgnoresSpoofedFields()
    {
        var world = await WorldAsync();
        var requester = await CallerAsync(world.TenantId, [world.SiteA], RoleCodes.Requester);
        var correlationId = Guid.NewGuid();

        var response = await SendAsync(HttpMethod.Post, Collection, requester, new
        {
            tenantId = Guid.NewGuid(),
            createdBy = Guid.NewGuid(),
            status = "APPROVED",
            requestNo = "RR-SPOOF",
            submittedAt = "2026-09-14T10:00:00Z",
            locationId = Guid.NewGuid()
        }, correlationId: correlationId);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await JsonAsync(response);
        var id = body.GetProperty("id").GetGuid();
        Assert.Equal("DRAFT", body.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("requestNo").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("siteId").ValueKind);
        Assert.Equal(requester.UserId, body.GetProperty("createdBy").GetGuid());
        Assert.Equal($"{Collection}/{id}", response.Headers.Location!.OriginalString);
        Assert.Equal($"\"{body.GetProperty("rowVersion").GetString()}\"", response.Headers.ETag!.Tag);

        var stored = await WithDbAsync(db => db.RepairRequests.AsNoTracking().SingleAsync(request => request.Id == id));
        Assert.Equal(world.TenantId, stored.TenantId);
        Assert.Equal(requester.UserId, stored.CreatedBy);
        Assert.Equal(RepairRequestStatus.Draft, stored.Status);
        Assert.Null(stored.RequestNo);
        Assert.Null(stored.RequestCategoryCode);
        Assert.Null(stored.PriorityCode);
        Assert.Null(stored.LocationId);
        Assert.Null(stored.SubmittedAt);

        var audit = Assert.Single(await AuditsAsync(id));
        Assert.Equal("REPAIR_REQUEST", audit.EntityType);
        Assert.Equal("REPAIR_REQUEST_DRAFT_CREATED", audit.ActionCode);
        Assert.Equal("DRAFT", audit.ToState);
        Assert.Equal(requester.UserId, audit.ActorId);
        Assert.Equal(correlationId, audit.CorrelationId);
    }

    [Fact]
    public async Task Create_WithSiteEquipmentDescriptionAndWindow_ReturnsUtcValues()
    {
        var world = await WorldAsync();
        var requester = await CallerAsync(world.TenantId, [world.SiteA], RoleCodes.Requester);

        var response = await SendAsync(HttpMethod.Post, Collection, requester, new
        {
            siteId = world.SiteA.Id,
            equipmentId = world.EquipmentA1.Id,
            description = "  Pump leaking  ",
            preferredStartAt = "2026-09-20T15:00:00+07:00",
            preferredEndAt = "2026-09-20T10:00:00Z"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal(world.SiteA.Id, body.GetProperty("siteId").GetGuid());
        Assert.Equal(world.EquipmentA1.Id, body.GetProperty("equipmentId").GetGuid());
        Assert.Equal("Pump leaking", body.GetProperty("description").GetString());
        Assert.Equal("2026-09-20T08:00:00Z", body.GetProperty("preferredStartAt").GetString());
        Assert.Equal("2026-09-20T10:00:00Z", body.GetProperty("preferredEndAt").GetString());
    }

    [Fact]
    public async Task Create_WithoutToken_Returns401()
    {
        var response = await SendAsync(HttpMethod.Post, Collection, null, new { });

        await AssertProblemAsync(response, HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
    }

    [Theory]
    [MemberData(nameof(NonRequesterRoles))]
    public async Task NonRequesterRoles_CannotCreateOrEditDrafts_Returns403(string role)
    {
        var world = await WorldAsync();
        var caller = await CallerAsync(world.TenantId, [world.SiteA], role);

        var create = await SendAsync(HttpMethod.Post, Collection, caller, new { siteId = world.SiteA.Id });
        var edit = await SendAsync(HttpMethod.Patch, $"{Collection}/{Guid.NewGuid()}", caller, new { }, "\"AAAAAAAAB9E=\"");

        await AssertProblemAsync(create, HttpStatusCode.Forbidden, "ACCESS_DENIED");
        await AssertProblemAsync(edit, HttpStatusCode.Forbidden, "ACCESS_DENIED");
        Assert.False(await WithDbAsync(db => db.RepairRequests.AnyAsync(request => request.TenantId == world.TenantId)));
    }

    [Fact]
    public async Task Create_WithSiteOutsideScopeOrTenant_Returns404IdenticalToUnknownSite()
    {
        var world = await WorldAsync();
        var requester = await CallerAsync(world.TenantId, [world.SiteA], RoleCodes.Requester);

        var unknown = await SendAsync(HttpMethod.Post, Collection, requester, new { siteId = Guid.NewGuid() });
        var unassigned = await SendAsync(HttpMethod.Post, Collection, requester, new { siteId = world.SiteB.Id });
        var otherTenant = await SendAsync(HttpMethod.Post, Collection, requester, new { siteId = world.OtherTenantSite.Id });

        var expected = await NotFoundShapeAsync(unknown);
        Assert.Equal(expected, await NotFoundShapeAsync(unassigned));
        Assert.Equal(expected, await NotFoundShapeAsync(otherTenant));
        Assert.False(await WithDbAsync(db => db.RepairRequests.AnyAsync(request => request.TenantId == world.TenantId)));
    }

    [Fact]
    public async Task Create_WithInactiveOrMismatchedMasters_Returns422()
    {
        var world = await WorldAsync();
        var requester = await CallerAsync(world.TenantId, [world.SiteA, world.InactiveSiteX], RoleCodes.Requester);

        var inactiveSite = await SendAsync(HttpMethod.Post, Collection, requester, new { siteId = world.InactiveSiteX.Id });
        var equipmentOfOtherSite = await SendAsync(HttpMethod.Post, Collection, requester, new { siteId = world.SiteA.Id, equipmentId = world.EquipmentB1.Id });
        var equipmentWithoutSite = await SendAsync(HttpMethod.Post, Collection, requester, new { equipmentId = world.EquipmentA1.Id });
        var inactiveEquipment = await SendAsync(HttpMethod.Post, Collection, requester, new { siteId = world.SiteA.Id, equipmentId = world.InactiveEquipmentA2.Id });
        var backwardsWindow = await SendAsync(HttpMethod.Post, Collection, requester, new { preferredStartAt = "2026-09-20T10:00:00Z", preferredEndAt = "2026-09-20T09:00:00Z" });

        Assert.True((await AssertProblemAsync(inactiveSite, HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED")).GetProperty("errors").TryGetProperty("siteId", out _));
        Assert.True((await AssertProblemAsync(equipmentOfOtherSite, HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED")).GetProperty("errors").TryGetProperty("equipmentId", out _));
        Assert.True((await AssertProblemAsync(equipmentWithoutSite, HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED")).GetProperty("errors").TryGetProperty("equipmentId", out _));
        Assert.True((await AssertProblemAsync(inactiveEquipment, HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED")).GetProperty("errors").TryGetProperty("equipmentId", out _));
        Assert.True((await AssertProblemAsync(backwardsWindow, HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED")).GetProperty("errors").TryGetProperty("preferredEndAt", out _));
        Assert.False(await WithDbAsync(db => db.RepairRequests.AnyAsync(request => request.TenantId == world.TenantId)));
    }

    [Fact]
    public async Task Create_WithTimestampWithoutOffset_Returns400()
    {
        var world = await WorldAsync();
        var requester = await CallerAsync(world.TenantId, [world.SiteA], RoleCodes.Requester);

        var response = await SendRawJsonAsync(HttpMethod.Post, Collection, requester, "{\"preferredStartAt\":\"2026-09-20T08:00:00\"}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---------------- Edit ----------------

    [Fact]
    public async Task Edit_ByOwnerWithCurrentETag_UpdatesDraft_ReturnsNewETag_AndAuditsChanges()
    {
        var world = await WorldAsync();
        var requester = await CallerAsync(world.TenantId, [world.SiteA], RoleCodes.Requester);
        var (id, etag) = await CreateDraftAsync(requester, new { siteId = world.SiteA.Id, description = "Before" });

        var response = await SendAsync(HttpMethod.Patch, $"{Collection}/{id}", requester, new
        {
            siteId = world.SiteA.Id,
            equipmentId = world.EquipmentA1.Id,
            description = "After"
        }, etag);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal("After", body.GetProperty("description").GetString());
        Assert.Equal(world.EquipmentA1.Id, body.GetProperty("equipmentId").GetGuid());
        Assert.Equal("DRAFT", body.GetProperty("status").GetString());
        Assert.NotEqual(etag, response.Headers.ETag!.Tag);

        var audits = await AuditsAsync(id);
        Assert.Equal(new[] { "REPAIR_REQUEST_DRAFT_CREATED", "REPAIR_REQUEST_DRAFT_UPDATED" }, audits.Select(audit => audit.ActionCode));
        Assert.Contains("Before", audits[1].OldValueJson);
        Assert.Contains("After", audits[1].NewValueJson);
        Assert.Contains(world.EquipmentA1.Id.ToString(), audits[1].NewValueJson);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("*")]
    public async Task Edit_WithoutUsableIfMatch_Returns400(string? ifMatch)
    {
        var world = await WorldAsync();
        var requester = await CallerAsync(world.TenantId, [world.SiteA], RoleCodes.Requester);
        var (id, _) = await CreateDraftAsync(requester, new { description = "Before" });

        var response = await SendAsync(HttpMethod.Patch, $"{Collection}/{id}", requester, new { description = "After" }, ifMatch);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "BAD_REQUEST");
    }

    [Fact]
    public async Task Edit_WithStaleETag_Returns409_AndFirstWriteWins()
    {
        var world = await WorldAsync();
        var requester = await CallerAsync(world.TenantId, [world.SiteA], RoleCodes.Requester);
        var (id, etag) = await CreateDraftAsync(requester, new { description = "Before" });

        var first = await SendAsync(HttpMethod.Patch, $"{Collection}/{id}", requester, new { description = "First" }, etag);
        var second = await SendAsync(HttpMethod.Patch, $"{Collection}/{id}", requester, new { description = "Second" }, etag);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        await AssertProblemAsync(second, HttpStatusCode.Conflict, "CONCURRENCY_CONFLICT");
        Assert.Equal("First", await DescriptionAsync(id));
    }

    [Fact]
    public async Task Edit_WhenNoLongerDraft_Returns409StateConflict()
    {
        var world = await WorldAsync();
        var requester = await CallerAsync(world.TenantId, [world.SiteA], RoleCodes.Requester);
        var (id, _) = await CreateDraftAsync(requester, new { siteId = world.SiteA.Id, description = "Before" });
        await WithDbAsync(db => db.RepairRequests.Where(request => request.Id == id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(request => request.Status, RepairRequestStatus.Submitted)));
        var etag = await ETagAsync(id, requester);

        var response = await SendAsync(HttpMethod.Patch, $"{Collection}/{id}", requester, new { siteId = world.SiteA.Id, description = "After" }, etag);

        await AssertProblemAsync(response, HttpStatusCode.Conflict, "STATE_CONFLICT");
        Assert.Equal("Before", await DescriptionAsync(id));
    }

    [Fact]
    public async Task Edit_OfAnotherUsersDraft_Returns404_EvenForAViewerWhoCanSeeIt()
    {
        var world = await WorldAsync();
        var owner = await CallerAsync(world.TenantId, [world.SiteA], RoleCodes.Requester);
        var otherRequester = await CallerAsync(world.TenantId, [world.SiteA], RoleCodes.Requester);
        var approverRequester = await CallerAsync(world.TenantId, [world.SiteA], RoleCodes.Approver, RoleCodes.Requester);
        var otherTenantRequester = await CallerAsync(world.OtherTenantSite.TenantId, [world.OtherTenantSite], RoleCodes.Requester);
        var (id, etag) = await CreateDraftAsync(owner, new { siteId = world.SiteA.Id, description = "Mine" });
        var random = await SendAsync(HttpMethod.Patch, $"{Collection}/{Guid.NewGuid()}", owner, new { description = "x" }, etag);

        var sameSite = await SendAsync(HttpMethod.Patch, $"{Collection}/{id}", otherRequester, new { description = "Hijack" }, etag);
        var viewer = await SendAsync(HttpMethod.Patch, $"{Collection}/{id}", approverRequester, new { description = "Hijack" }, etag);
        var otherTenant = await SendAsync(HttpMethod.Patch, $"{Collection}/{id}", otherTenantRequester, new { description = "Hijack" }, etag);

        var expected = await NotFoundShapeAsync(random);
        Assert.Equal(expected, await NotFoundShapeAsync(sameSite));
        Assert.Equal(expected, await NotFoundShapeAsync(viewer));
        Assert.Equal(expected, await NotFoundShapeAsync(otherTenant));
        Assert.Equal("Mine", await DescriptionAsync(id));
        Assert.Single(await AuditsAsync(id));
    }

    [Fact]
    public async Task Edit_ChangingSite_RequiresEquipmentOfTheNewSite()
    {
        var world = await WorldAsync();
        var siteC = await WithDbAsync(async db =>
        {
            var customerId = await db.Sites.Where(site => site.Id == world.SiteA.Id).Select(site => site.CustomerId).SingleAsync();
            var site = new Site(world.TenantId, customerId, "SITE-C");
            db.Sites.Add(site);
            await db.SaveChangesAsync();
            return site;
        });
        var equipmentC1 = await WithDbAsync(async db =>
        {
            var equipment = new Equipment(world.TenantId, siteC.Id, "EQ-C1");
            db.Equipment.Add(equipment);
            await db.SaveChangesAsync();
            return equipment;
        });
        var requester = await CallerAsync(world.TenantId, [world.SiteA, siteC], RoleCodes.Requester);
        var (id, etag) = await CreateDraftAsync(requester, new { siteId = world.SiteA.Id, equipmentId = world.EquipmentA1.Id });

        var keptOldEquipment = await SendAsync(HttpMethod.Patch, $"{Collection}/{id}", requester, new { siteId = siteC.Id, equipmentId = world.EquipmentA1.Id }, etag);
        var matchingEquipment = await SendAsync(HttpMethod.Patch, $"{Collection}/{id}", requester, new { siteId = siteC.Id, equipmentId = equipmentC1.Id }, etag);

        Assert.True((await AssertProblemAsync(keptOldEquipment, HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED")).GetProperty("errors").TryGetProperty("equipmentId", out _));
        Assert.Equal(HttpStatusCode.OK, matchingEquipment.StatusCode);
        var body = await JsonAsync(matchingEquipment);
        Assert.Equal(siteC.Id, body.GetProperty("siteId").GetGuid());
        Assert.Equal(equipmentC1.Id, body.GetProperty("equipmentId").GetGuid());
    }

    [Fact]
    public async Task Edit_WithoutChanges_Returns200_AndWritesNoAudit()
    {
        var world = await WorldAsync();
        var requester = await CallerAsync(world.TenantId, [world.SiteA], RoleCodes.Requester);
        var (id, etag) = await CreateDraftAsync(requester, new { siteId = world.SiteA.Id, description = "Same" });

        var response = await SendAsync(HttpMethod.Patch, $"{Collection}/{id}", requester, new { siteId = world.SiteA.Id, description = "Same" }, etag);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(etag, response.Headers.ETag!.Tag);
        Assert.Single(await AuditsAsync(id));
    }

    [Fact]
    public async Task Edit_WhenPreviouslySelectedSiteBecameInactive_Returns422_Decision_E2()
    {
        var world = await WorldAsync();
        var requester = await CallerAsync(world.TenantId, [world.SiteA], RoleCodes.Requester);
        var (id, etag) = await CreateDraftAsync(requester, new { siteId = world.SiteA.Id, description = "Before" });
        await WithDbAsync(db => db.Sites.Where(site => site.Id == world.SiteA.Id).ExecuteUpdateAsync(setters => setters
            .SetProperty(site => site.Status, MasterDataStatus.Inactive)
            .SetProperty(site => site.DeactivateReason, "Closed")));

        var response = await SendAsync(HttpMethod.Patch, $"{Collection}/{id}", requester, new { siteId = world.SiteA.Id, description = "Only text" }, etag);

        Assert.True((await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED")).GetProperty("errors").TryGetProperty("siteId", out _));
        Assert.Equal("Before", await DescriptionAsync(id));
    }

    [Fact]
    public async Task Edit_AfterSiteAssignmentWasRemoved_Returns404()
    {
        var world = await WorldAsync();
        var requester = await CallerAsync(world.TenantId, [world.SiteA], RoleCodes.Requester);
        var (id, etag) = await CreateDraftAsync(requester, new { siteId = world.SiteA.Id, description = "Before" });
        await WithDbAsync(db => db.UserSiteScopes.Where(scope => scope.UserId == requester.UserId).ExecuteDeleteAsync());

        var edit = await SendAsync(HttpMethod.Patch, $"{Collection}/{id}", requester, new { siteId = world.SiteA.Id, description = "After" }, etag);
        var detail = await SendAsync(HttpMethod.Get, $"{Collection}/{id}", requester);

        Assert.Equal(HttpStatusCode.NotFound, edit.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);
        Assert.Equal("Before", await DescriptionAsync(id));
    }

    // ---------------- ADMINISTRATOR + REQUESTER (code review M1) ----------------

    [Fact]
    public async Task AdministratorRequester_CreatesDraftsOnlyOnAssignedSites()
    {
        var world = await WorldAsync();
        var adminRequester = await CallerAsync(world.TenantId, [world.SiteA], RoleCodes.Administrator, RoleCodes.Requester);

        var assigned = await SendAsync(HttpMethod.Post, Collection, adminRequester, new { siteId = world.SiteA.Id, equipmentId = world.EquipmentA1.Id });
        var unknown = await SendAsync(HttpMethod.Post, Collection, adminRequester, new { siteId = Guid.NewGuid() });
        var unassigned = await SendAsync(HttpMethod.Post, Collection, adminRequester, new { siteId = world.SiteB.Id, equipmentId = world.EquipmentB1.Id });
        var otherTenant = await SendAsync(HttpMethod.Post, Collection, adminRequester, new { siteId = world.OtherTenantSite.Id });

        Assert.Equal(HttpStatusCode.Created, assigned.StatusCode);
        var id = (await JsonAsync(assigned)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, $"{Collection}/{id}", adminRequester)).StatusCode);

        var expected = await NotFoundShapeAsync(unknown);
        Assert.Equal(expected, await NotFoundShapeAsync(unassigned));
        Assert.Equal(expected, await NotFoundShapeAsync(otherTenant));
        Assert.Equal(new[] { id }, await WithDbAsync(db => db.RepairRequests
            .Where(request => request.TenantId == world.TenantId)
            .Select(request => request.Id)
            .ToListAsync()));
    }

    [Fact]
    public async Task AdministratorRequester_EditsOnlyWithinAssignedSites()
    {
        var world = await WorldAsync();
        var adminRequester = await CallerAsync(world.TenantId, [world.SiteA], RoleCodes.Administrator, RoleCodes.Requester);
        var (id, etag) = await CreateDraftAsync(adminRequester, new { siteId = world.SiteA.Id, description = "Before" });

        var moveToUnassigned = await SendAsync(
            HttpMethod.Patch, $"{Collection}/{id}", adminRequester, new { siteId = world.SiteB.Id, equipmentId = world.EquipmentB1.Id, description = "Moved" }, etag);

        Assert.Equal(HttpStatusCode.NotFound, moveToUnassigned.StatusCode);
        Assert.Equal("Before", await DescriptionAsync(id));

        var stayOnAssigned = await SendAsync(HttpMethod.Patch, $"{Collection}/{id}", adminRequester, new { siteId = world.SiteA.Id, description = "After" }, etag);

        Assert.Equal(HttpStatusCode.OK, stayOnAssigned.StatusCode);
        Assert.Equal(world.SiteA.Id, (await JsonAsync(stayOnAssigned)).GetProperty("siteId").GetGuid());
        Assert.Equal("After", await DescriptionAsync(id));
        Assert.Equal(
            new[] { "REPAIR_REQUEST_DRAFT_CREATED", "REPAIR_REQUEST_DRAFT_UPDATED" },
            (await AuditsAsync(id)).Select(audit => audit.ActionCode));
    }

    // ---------------- Detail ----------------

    [Fact]
    public async Task Detail_FollowsApprovedScope_Decision_E3()
    {
        var world = await WorldAsync();
        var owner = await CallerAsync(world.TenantId, [world.SiteA], RoleCodes.Requester);
        var approver = await CallerAsync(world.TenantId, [world.SiteA], RoleCodes.Approver);
        var approverElsewhere = await CallerAsync(world.TenantId, [world.SiteB], RoleCodes.Approver);
        var otherRequester = await CallerAsync(world.TenantId, [world.SiteA], RoleCodes.Requester);
        var otherTenantApprover = await CallerAsync(world.OtherTenantSite.TenantId, [world.OtherTenantSite], RoleCodes.Approver);
        var administrator = await CallerAsync(world.TenantId, [], RoleCodes.Administrator);
        var (id, etag) = await CreateDraftAsync(owner, new { siteId = world.SiteA.Id, description = "Leak" });
        var url = $"{Collection}/{id}";

        var ownerView = await SendAsync(HttpMethod.Get, url, owner);
        var approverView = await SendAsync(HttpMethod.Get, url, approver);
        var random = await SendAsync(HttpMethod.Get, $"{Collection}/{Guid.NewGuid()}", owner);

        Assert.Equal(HttpStatusCode.OK, ownerView.StatusCode);
        Assert.Equal(etag, ownerView.Headers.ETag!.Tag);
        Assert.Equal("Leak", (await JsonAsync(ownerView)).GetProperty("description").GetString());
        Assert.Equal(HttpStatusCode.OK, approverView.StatusCode);

        var expected = await NotFoundShapeAsync(random);
        Assert.Equal(expected, await NotFoundShapeAsync(await SendAsync(HttpMethod.Get, url, approverElsewhere)));
        Assert.Equal(expected, await NotFoundShapeAsync(await SendAsync(HttpMethod.Get, url, otherRequester)));
        Assert.Equal(expected, await NotFoundShapeAsync(await SendAsync(HttpMethod.Get, url, otherTenantApprover)));
        await AssertProblemAsync(await SendAsync(HttpMethod.Get, url, administrator), HttpStatusCode.Forbidden, "ACCESS_DENIED");
    }

    // ---------------- Helpers ----------------

    private HttpClient CreateClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

    private async Task<World> WorldAsync()
    {
        var tenantId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();

        return await WithDbAsync(async db =>
        {
            var customer = new Customer(tenantId, "CUST-1");
            var otherCustomer = new Customer(otherTenantId, "CUST-1");
            db.Customers.AddRange(customer, otherCustomer);

            var siteA = new Site(tenantId, customer.Id, "SITE-A");
            var siteB = new Site(tenantId, customer.Id, "SITE-B");
            var siteX = new Site(tenantId, customer.Id, "SITE-X");
            siteX.Deactivate("Closed");
            var otherTenantSite = new Site(otherTenantId, otherCustomer.Id, "SITE-A");
            db.Sites.AddRange(siteA, siteB, siteX, otherTenantSite);

            var equipmentA1 = new Equipment(tenantId, siteA.Id, "EQ-A1");
            var equipmentA2 = new Equipment(tenantId, siteA.Id, "EQ-A2");
            equipmentA2.Deactivate("Broken");
            var equipmentB1 = new Equipment(tenantId, siteB.Id, "EQ-B1");
            db.Equipment.AddRange(equipmentA1, equipmentA2, equipmentB1);

            await db.SaveChangesAsync();
            return new World(tenantId, siteA, equipmentA1, equipmentA2, siteB, equipmentB1, siteX, otherTenantSite);
        });
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

    private async Task<(Guid Id, string ETag)> CreateDraftAsync(Caller owner, object body)
    {
        var response = await SendAsync(HttpMethod.Post, Collection, owner, body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return ((await JsonAsync(response)).GetProperty("id").GetGuid(), response.Headers.ETag!.Tag);
    }

    private async Task<string> ETagAsync(Guid id, Caller caller)
    {
        var response = await SendAsync(HttpMethod.Get, $"{Collection}/{id}", caller);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return response.Headers.ETag!.Tag;
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, Caller? caller, object? body = null, string? ifMatch = null, Guid? correlationId = null) =>
        SendCoreAsync(method, url, caller, body is null ? null : JsonContent.Create(body), ifMatch, correlationId);

    private Task<HttpResponseMessage> SendRawJsonAsync(HttpMethod method, string url, Caller caller, string json) =>
        SendCoreAsync(method, url, caller, new StringContent(json, Encoding.UTF8, "application/json"), null, null);

    private async Task<HttpResponseMessage> SendCoreAsync(HttpMethod method, string url, Caller? caller, HttpContent? content, string? ifMatch, Guid? correlationId)
    {
        var request = new HttpRequestMessage(method, url) { Content = content };

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
        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
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

    private Task<string?> DescriptionAsync(Guid id) =>
        WithDbAsync(db => db.RepairRequests.Where(request => request.Id == id).Select(request => request.Description).SingleAsync());

    private Task<List<RepairRequest.Domain.Auditing.AuditHistory>> AuditsAsync(Guid entityId) =>
        WithDbAsync(db => db.AuditHistory.AsNoTracking().Where(audit => audit.EntityId == entityId).OrderBy(audit => audit.OccurredAt).ToListAsync());
}
