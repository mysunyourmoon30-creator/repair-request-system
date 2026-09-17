using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RepairRequest.Application.Security;
using RepairRequest.Domain.Security;
using RepairRequest.Infrastructure.Persistence;
using RepairRequest.IntegrationTests.Authentication;
using RepairRequest.IntegrationTests.Persistence;

namespace RepairRequest.IntegrationTests.Authorization;

/// <summary>
/// Tenant/site data scope against LocalDB (S1-003 decisions 1, 2, 4; RR-REQ-001 sections 3/9;
/// TC-SEC-001 cross-tenant/site IDOR). Every test builds its own two-tenant world.
/// </summary>
[Collection(PersistenceDatabaseCollection.Name)]
public sealed class DataScopeTests : IAsyncLifetime
{
    private readonly AuthenticationTestHost _host = new();

    public DataScopeTests(PersistenceDatabaseFixture database)
    {
        _ = database;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private async Task<T> QueryAsync<T>(Func<IDataScope, Task<T>> query)
    {
        await using var scope = _host.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<IDataScope>());
    }

    private async Task<T> WithDbAsync<T>(Func<RepairRequestDbContext, Task<T>> action)
    {
        await using var scope = _host.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<RepairRequestDbContext>());
    }

    private Task<ScopeWorld> NewWorldAsync() => WithDbAsync(ScopeWorld.CreateAsync);

    private async Task<CurrentUser> NewUserAsync(ScopeWorld world, Domain.MasterData.Site[] assignedSites, params string[] roles)
    {
        var user = await _host.CreateUserInTenantAsync(world.TenantId, roles);
        await WithDbAsync(async db =>
        {
            await ScopeWorld.AssignSitesAsync(db, world.TenantId, user.Id, assignedSites);
            return true;
        });
        return new CurrentUser(user.Id, world.TenantId, roles);
    }

    private Task<Guid> AddRequestAsync(Guid tenantId, Guid createdBy, Domain.MasterData.Site? site) =>
        WithDbAsync(db => ScopeWorld.AddRequestAsync(db, tenantId, createdBy, site));

    private static Guid[] Sorted(IEnumerable<Guid> ids) => ids.Order().ToArray();

    // ---------------- Site / Equipment / Customer ----------------

    [Fact]
    public async Task Sites_AreLimitedToAssignedSitesOfOwnTenant()
    {
        var world = await NewWorldAsync();
        var user = await NewUserAsync(world, [world.SiteA1, world.SiteB1], RoleCodes.Approver);

        var visible = await QueryAsync(scope => scope.Sites(user).Select(site => site.Id).ToListAsync());

        Assert.Equal(Sorted([world.SiteA1.Id, world.SiteB1.Id]), Sorted(visible));
    }

    [Fact]
    public async Task UserWithoutSiteAssignment_SeesNoSiteDataAtAll()
    {
        var world = await NewWorldAsync();
        var creator = await NewUserAsync(world, [], RoleCodes.Requester);
        await AddRequestAsync(world.TenantId, creator.UserId, world.SiteA1);
        var user = await NewUserAsync(world, [], RoleCodes.Supervisor);

        Assert.Empty(await QueryAsync(scope => scope.Sites(user).ToListAsync()));
        Assert.Empty(await QueryAsync(scope => scope.Equipment(user).ToListAsync()));
        Assert.Empty(await QueryAsync(scope => scope.Customers(user).ToListAsync()));
        Assert.Empty(await QueryAsync(scope => scope.RepairRequests(user).ToListAsync()));
    }

    [Fact]
    public async Task DeactivatedAssignedSite_RemainsVisibleForHistory()
    {
        var world = await NewWorldAsync();
        var user = await NewUserAsync(world, [world.SiteA1], RoleCodes.Coordinator);
        await WithDbAsync(db => ScopeWorld.DeactivateSiteAsync(db, world.SiteA1.Id).ContinueWith(_ => true));

        var visible = await QueryAsync(scope => scope.Sites(user).Select(site => site.Id).ToListAsync());

        Assert.Equal([world.SiteA1.Id], visible);
    }

    [Fact]
    public async Task Equipment_FollowsSiteScope()
    {
        var world = await NewWorldAsync();
        var user = await NewUserAsync(world, [world.SiteA1], RoleCodes.Technician);

        var visible = await QueryAsync(scope => scope.Equipment(user).Select(item => item.Id).ToListAsync());

        Assert.Equal([world.EquipmentA1.Id], visible);
    }

    [Fact]
    public async Task Customers_AreVisibleOnlyThroughAssignedSites()
    {
        var world = await NewWorldAsync();
        var user = await NewUserAsync(world, [world.SiteA1, world.SiteA2], RoleCodes.Approver);

        var visible = await QueryAsync(scope => scope.Customers(user).Select(customer => customer.Id).ToListAsync());

        Assert.Equal([world.CustomerA.Id], visible);
    }

    [Fact]
    public async Task CrossTenantRecords_AreNotReturnedEvenWhenLookedUpById()
    {
        var world = await NewWorldAsync();
        var user = await NewUserAsync(world, [world.SiteA1], RoleCodes.Approver);

        var site = await QueryAsync(scope => scope.Sites(user).Where(s => s.Id == world.OtherTenantSite.Id).Select(s => (Guid?)s.Id).SingleOrDefaultAsync());
        var equipment = await QueryAsync(scope => scope.Equipment(user).Where(e => e.Id == world.OtherTenantEquipment.Id).Select(e => (Guid?)e.Id).SingleOrDefaultAsync());

        Assert.Null(site);
        Assert.Null(equipment);
    }

    // ---------------- Administrator ----------------

    [Fact]
    public async Task Administrator_HasTenantWideMasterData_ButNoRepairRequests()
    {
        var world = await NewWorldAsync();
        var creator = await NewUserAsync(world, [world.SiteA1], RoleCodes.Requester);
        await AddRequestAsync(world.TenantId, creator.UserId, world.SiteA1);
        var admin = await NewUserAsync(world, [], RoleCodes.Administrator);

        var sites = await QueryAsync(scope => scope.Sites(admin).Select(site => site.Id).ToListAsync());
        var customers = await QueryAsync(scope => scope.Customers(admin).Select(customer => customer.Id).ToListAsync());
        var equipment = await QueryAsync(scope => scope.Equipment(admin).Select(item => item.Id).ToListAsync());
        var requests = await QueryAsync(scope => scope.RepairRequests(admin).ToListAsync());

        Assert.Equal(Sorted([world.SiteA1.Id, world.SiteA2.Id, world.SiteB1.Id]), Sorted(sites));
        Assert.Equal(Sorted([world.CustomerA.Id, world.CustomerB.Id]), Sorted(customers));
        Assert.Equal(Sorted([world.EquipmentA1.Id, world.EquipmentB1.Id]), Sorted(equipment));
        Assert.Empty(requests);
    }

    [Fact]
    public async Task AdministratorWithBusinessRole_SeesRequestsOnlyAtAssignedSites()
    {
        var world = await NewWorldAsync();
        var creator = await NewUserAsync(world, [world.SiteA1, world.SiteB1], RoleCodes.Requester);
        var atA1 = await AddRequestAsync(world.TenantId, creator.UserId, world.SiteA1);
        await AddRequestAsync(world.TenantId, creator.UserId, world.SiteB1);
        var user = await NewUserAsync(world, [world.SiteA1], RoleCodes.Administrator, RoleCodes.Approver);

        var requests = await QueryAsync(scope => scope.RepairRequests(user).Select(request => request.Id).ToListAsync());

        Assert.Equal([atA1], requests);
    }

    // ---------------- Repair Request ----------------

    [Fact]
    public async Task RequesterOnly_SeesOwnRequestsWithinScope_AndOwnSiteLessDrafts()
    {
        var world = await NewWorldAsync();
        var requester = await NewUserAsync(world, [world.SiteA1], RoleCodes.Requester);
        var colleague = await NewUserAsync(world, [world.SiteA1], RoleCodes.Requester);

        var ownAtA1 = await AddRequestAsync(world.TenantId, requester.UserId, world.SiteA1);
        var ownDraft = await AddRequestAsync(world.TenantId, requester.UserId, site: null);
        await AddRequestAsync(world.TenantId, requester.UserId, world.SiteB1);
        await AddRequestAsync(world.TenantId, colleague.UserId, world.SiteA1);
        await AddRequestAsync(world.TenantId, colleague.UserId, site: null);

        var visible = await QueryAsync(scope => scope.RepairRequests(requester).Select(request => request.Id).ToListAsync());

        Assert.Equal(Sorted([ownAtA1, ownDraft]), Sorted(visible));
    }

    [Fact]
    public async Task Approver_SeesAllRequestsAtAssignedSites_ButNotOthersSiteLessDrafts()
    {
        var world = await NewWorldAsync();
        var requester = await NewUserAsync(world, [world.SiteA1, world.SiteB1], RoleCodes.Requester);
        var approver = await NewUserAsync(world, [world.SiteA1], RoleCodes.Approver);

        var atA1 = await AddRequestAsync(world.TenantId, requester.UserId, world.SiteA1);
        await AddRequestAsync(world.TenantId, requester.UserId, world.SiteB1);
        await AddRequestAsync(world.TenantId, requester.UserId, site: null);

        var visible = await QueryAsync(scope => scope.RepairRequests(approver).Select(request => request.Id).ToListAsync());

        Assert.Equal([atA1], visible);
    }

    [Fact]
    public async Task RequesterAndApprover_GetTheCombinedScope()
    {
        var world = await NewWorldAsync();
        var colleague = await NewUserAsync(world, [world.SiteA1], RoleCodes.Requester);
        var user = await NewUserAsync(world, [world.SiteA1], RoleCodes.Requester, RoleCodes.Approver);

        var colleagueAtA1 = await AddRequestAsync(world.TenantId, colleague.UserId, world.SiteA1);
        var ownDraft = await AddRequestAsync(world.TenantId, user.UserId, site: null);
        await AddRequestAsync(world.TenantId, colleague.UserId, site: null);

        var visible = await QueryAsync(scope => scope.RepairRequests(user).Select(request => request.Id).ToListAsync());

        Assert.Equal(Sorted([colleagueAtA1, ownDraft]), Sorted(visible));
    }

    [Fact]
    public async Task RepairRequestsOfAnotherTenant_AreNeverVisible()
    {
        var world = await NewWorldAsync();
        var otherTenantUser = await _host.CreateUserInTenantAsync(world.OtherTenantId, RoleCodes.Requester);
        var foreign = await AddRequestAsync(world.OtherTenantId, otherTenantUser.Id, world.OtherTenantSite);
        var approver = await NewUserAsync(world, [world.SiteA1, world.SiteA2, world.SiteB1], RoleCodes.Approver);

        var found = await QueryAsync(scope => scope.RepairRequests(approver).AnyAsync(request => request.Id == foreign));

        Assert.False(found);
    }

    // ---------------- Client-supplied ids / query shape ----------------

    [Fact]
    public async Task IsSiteInScope_RejectsUnassignedOtherTenantAndUnknownSites()
    {
        var world = await NewWorldAsync();
        var user = await NewUserAsync(world, [world.SiteA1], RoleCodes.Requester);

        Assert.True(await QueryAsync(scope => scope.IsSiteInScopeAsync(user, world.SiteA1.Id, CancellationToken.None)));
        Assert.False(await QueryAsync(scope => scope.IsSiteInScopeAsync(user, world.SiteA2.Id, CancellationToken.None)));
        Assert.False(await QueryAsync(scope => scope.IsSiteInScopeAsync(user, world.OtherTenantSite.Id, CancellationToken.None)));
        Assert.False(await QueryAsync(scope => scope.IsSiteInScopeAsync(user, Guid.NewGuid(), CancellationToken.None)));
    }

    [Fact]
    public async Task BusinessSites_AreAssignedSitesOnly_AndConfigurationScopeNeverWidensThem()
    {
        var world = await NewWorldAsync();
        var requester = await NewUserAsync(world, [world.SiteA1], RoleCodes.Requester);
        var adminRequester = await NewUserAsync(world, [world.SiteA1], RoleCodes.Administrator, RoleCodes.Requester);
        var administrator = await NewUserAsync(world, [world.SiteA1], RoleCodes.Administrator);
        var spoofedTenant = new CurrentUser(adminRequester.UserId, world.OtherTenantId, [RoleCodes.Administrator, RoleCodes.Requester]);

        Assert.Equal(new[] { world.SiteA1.Id }, await QueryAsync(scope => scope.BusinessSites(requester).Select(site => site.Id).ToListAsync()));
        Assert.Equal(new[] { world.SiteA1.Id }, await QueryAsync(scope => scope.BusinessSites(adminRequester).Select(site => site.Id).ToListAsync()));
        Assert.Empty(await QueryAsync(scope => scope.BusinessSites(administrator).ToListAsync()));
        Assert.Empty(await QueryAsync(scope => scope.BusinessSites(spoofedTenant).ToListAsync()));

        // Configuration scope is unchanged: ADMINISTRATOR still reads every Site of its own tenant.
        Assert.Equal(
            Sorted([world.SiteA1.Id, world.SiteA2.Id, world.SiteB1.Id]),
            Sorted(await QueryAsync(scope => scope.Sites(adminRequester).Select(site => site.Id).ToListAsync())));
    }

    [Fact]
    public async Task IsSiteInScope_UsesBusinessSiteScope_ForUsersHoldingAdministrator()
    {
        var world = await NewWorldAsync();
        var adminRequester = await NewUserAsync(world, [world.SiteA1], RoleCodes.Administrator, RoleCodes.Requester);
        var administrator = await NewUserAsync(world, [], RoleCodes.Administrator);

        Assert.True(await QueryAsync(scope => scope.IsSiteInScopeAsync(adminRequester, world.SiteA1.Id, CancellationToken.None)));
        Assert.False(await QueryAsync(scope => scope.IsSiteInScopeAsync(adminRequester, world.SiteA2.Id, CancellationToken.None)));
        Assert.False(await QueryAsync(scope => scope.IsSiteInScopeAsync(adminRequester, world.OtherTenantSite.Id, CancellationToken.None)));
        Assert.False(await QueryAsync(scope => scope.IsSiteInScopeAsync(administrator, world.SiteA1.Id, CancellationToken.None)));
    }

    [Fact]
    public async Task ForgedTenantInCurrentUser_CannotReachRecordsOfThatTenantWithoutAssignment()
    {
        var world = await NewWorldAsync();
        var user = await NewUserAsync(world, [world.SiteA1], RoleCodes.Approver);
        var spoofed = new CurrentUser(user.UserId, world.OtherTenantId, [RoleCodes.Approver]);

        Assert.Empty(await QueryAsync(scope => scope.Sites(spoofed).ToListAsync()));
        Assert.False(await QueryAsync(scope => scope.IsSiteInScopeAsync(spoofed, world.OtherTenantSite.Id, CancellationToken.None)));
    }

    [Fact]
    public async Task ScopedLookupAndList_EachExecuteASingleSqlCommand()
    {
        var world = await NewWorldAsync();
        var requester = await NewUserAsync(world, [world.SiteA1], RoleCodes.Requester, RoleCodes.Approver);
        var requestId = await AddRequestAsync(world.TenantId, requester.UserId, world.SiteA1);

        await using var scope = _host.CreateScope();
        var dataScope = scope.ServiceProvider.GetRequiredService<IDataScope>();

        _host.Logs.Clear();
        await dataScope.RepairRequests(requester).AsNoTracking().Where(request => request.Id == requestId).Select(request => request.Id).SingleOrDefaultAsync();
        Assert.Equal(1, _host.Logs.ExecutedDbCommandCount);

        _host.Logs.Clear();
        await dataScope.Customers(requester).AsNoTracking().Select(customer => customer.Id).ToListAsync();
        Assert.Equal(1, _host.Logs.ExecutedDbCommandCount);
    }
}
