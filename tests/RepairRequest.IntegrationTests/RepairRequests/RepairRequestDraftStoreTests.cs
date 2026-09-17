using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using RepairRequest.Application.Common;
using RepairRequest.Application.RepairRequests;
using RepairRequest.Application.Security;
using RepairRequest.Domain.MasterData;
using RepairRequest.Domain.RepairRequests;
using RepairRequest.Domain.Security;
using RepairRequest.Infrastructure.Persistence;
using RepairRequest.Infrastructure.RepairRequests;
using RepairRequest.IntegrationTests.Authentication;
using RepairRequest.IntegrationTests.Authorization;
using RepairRequest.IntegrationTests.Persistence;

namespace RepairRequest.IntegrationTests.RepairRequests;

/// <summary>
/// Repair Request Draft persistence against LocalDB: scoped single-query selection (Site, Equipment, tenant lookups, contact
/// eligibility) and detail, owner-only tracked load, row-version protection and audit atomicity (UC-RR-001;
/// RR-ARCH-001 section 8; TC-SEC-001/002; DEC-PRE-S1-007-01/02/04).
/// </summary>
[Collection(PersistenceDatabaseCollection.Name)]
public sealed class RepairRequestDraftStoreTests : IAsyncLifetime
{
    private static readonly CancellationToken None = CancellationToken.None;

    private readonly AuthenticationTestHost _host = new();

    public RepairRequestDraftStoreTests(PersistenceDatabaseFixture database)
    {
        _ = database;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private static RepairRequestDraftFields Fields(Guid? siteId = null, string? description = null) =>
        new(siteId, null, null, null, null, description, null, null);

    private static DraftSelectionQuery Query(
        Guid? siteId = null,
        Guid? equipmentId = null,
        string? category = null,
        string? priority = null,
        Guid? contactId = null) =>
        new(siteId, equipmentId, category, priority, contactId);

    private async Task<T> WithDbAsync<T>(Func<RepairRequestDbContext, Task<T>> action)
    {
        await using var scope = _host.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<RepairRequestDbContext>());
    }

    private async Task WithDbAsync(Func<RepairRequestDbContext, Task> action)
    {
        await using var scope = _host.CreateScope();
        await action(scope.ServiceProvider.GetRequiredService<RepairRequestDbContext>());
    }

    private async Task<CurrentUser> UserAsync(ScopeWorld world, Site[] sites, params string[] roles) =>
        await UserInTenantAsync(world.TenantId, sites, roles);

    private async Task<CurrentUser> UserInTenantAsync(Guid tenantId, Site[] sites, params string[] roles)
    {
        var user = await _host.CreateUserInTenantAsync(tenantId, roles);
        await WithDbAsync(db => ScopeWorld.AssignSitesAsync(db, tenantId, user.Id, sites));
        return new CurrentUser(user.Id, tenantId, roles);
    }

    private async Task SeedLookupsAsync(Guid tenantId)
    {
        await using var scope = _host.CreateScope();
        await scope.ServiceProvider.GetRequiredService<RequestLookupSeeder>().SeedTenantAsync(tenantId, None);
    }

    private async Task<(T Result, int Commands)> MeasureAsync<T>(Func<IRepairRequestDraftStore, Task<T>> query)
    {
        await using var scope = _host.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IRepairRequestDraftStore>();

        _host.Logs.Clear();
        var result = await query(store);
        return (result, _host.Logs.ExecutedDbCommandCount);
    }

    private async Task<RepairRequestDraftDto> CreateDraftAsync(CurrentUser owner, RepairRequestDraftFields fields)
    {
        await using var scope = _host.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<RepairRequestDraftService>()
            .CreateAsync(new CommandContext(owner, Guid.NewGuid()), fields, None);
        Assert.True(result.Succeeded);
        return result.Value!;
    }

    [Fact]
    public async Task DraftSelection_IsOneScopedQuery_AndTiesEquipmentToTheSite()
    {
        var world = await WithDbAsync(ScopeWorld.CreateAsync);
        var requester = await UserAsync(world, [world.SiteA1], RoleCodes.Requester);

        var valid = await MeasureAsync(store => store.GetDraftSelectionAsync(requester, Query(world.SiteA1.Id, world.EquipmentA1.Id), None));
        var siteOnly = await MeasureAsync(store => store.GetDraftSelectionAsync(requester, Query(world.SiteA1.Id), None));
        var foreignEquipment = await MeasureAsync(store => store.GetDraftSelectionAsync(requester, Query(world.SiteA1.Id, world.EquipmentB1.Id), None));
        var otherTenantEquipment = await MeasureAsync(store => store.GetDraftSelectionAsync(requester, Query(world.SiteA1.Id, world.OtherTenantEquipment.Id), None));
        var unassignedSite = await MeasureAsync(store => store.GetDraftSelectionAsync(requester, Query(world.SiteA2.Id), None));
        var otherTenantSite = await MeasureAsync(store => store.GetDraftSelectionAsync(requester, Query(world.OtherTenantSite.Id), None));

        Assert.Equal(MasterDataStatus.Active, valid.Result.SiteStatus);
        Assert.Equal(MasterDataStatus.Active, valid.Result.EquipmentStatus);
        Assert.Equal(1, valid.Commands);
        Assert.Equal(MasterDataStatus.Active, siteOnly.Result.SiteStatus);
        Assert.Null(siteOnly.Result.EquipmentStatus);
        Assert.Null(foreignEquipment.Result.EquipmentStatus);
        Assert.Null(otherTenantEquipment.Result.EquipmentStatus);
        Assert.Null(unassignedSite.Result.SiteStatus);
        Assert.Null(otherTenantSite.Result.SiteStatus);
    }

    [Fact]
    public async Task DraftSelection_ForAdministratorRequester_IsLimitedToAssignedSites()
    {
        var world = await WithDbAsync(ScopeWorld.CreateAsync);
        var adminRequester = await UserAsync(world, [world.SiteA1], RoleCodes.Administrator, RoleCodes.Requester);

        await using var scope = _host.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IRepairRequestDraftStore>();

        var assigned = await store.GetDraftSelectionAsync(adminRequester, Query(world.SiteA1.Id, world.EquipmentA1.Id), None);
        Assert.Equal(MasterDataStatus.Active, assigned.SiteStatus);
        Assert.Equal(MasterDataStatus.Active, assigned.EquipmentStatus);
        Assert.Null((await store.GetDraftSelectionAsync(adminRequester, Query(world.SiteA2.Id), None)).SiteStatus);
        Assert.Null((await store.GetDraftSelectionAsync(adminRequester, Query(world.SiteB1.Id, world.EquipmentB1.Id), None)).SiteStatus);
        Assert.Null((await store.GetDraftSelectionAsync(adminRequester, Query(world.OtherTenantSite.Id), None)).SiteStatus);
    }

    [Fact]
    public async Task DraftSelection_ResolvesTenantLookupsToCanonicalCodes_ReportsInactive_AndIgnoresOtherTenants()
    {
        var world = await WithDbAsync(ScopeWorld.CreateAsync);
        var requester = await UserAsync(world, [world.SiteA1], RoleCodes.Requester);
        await SeedLookupsAsync(world.TenantId);
        await WithDbAsync(async db =>
        {
            await db.RequestPriorities
                .Where(priority => priority.TenantId == world.TenantId && priority.Code == "LOW")
                .ExecuteUpdateAsync(setters => setters.SetProperty(priority => priority.Status, MasterDataStatus.Inactive));
            db.RequestCategories.Add(new RequestCategory(world.OtherTenantId, "FOREIGN", "Other tenant only"));
            await db.SaveChangesAsync();
        });

        var selection = await MeasureAsync(store => store.GetDraftSelectionAsync(requester, Query(category: "electrical", priority: "LOW"), None));
        var unknown = await MeasureAsync(store => store.GetDraftSelectionAsync(requester, Query(category: "FOREIGN", priority: "NOPE"), None));

        Assert.Equal(new LookupSelection("ELECTRICAL", MasterDataStatus.Active), selection.Result.Category);
        Assert.Equal(new LookupSelection("LOW", MasterDataStatus.Inactive), selection.Result.Priority);
        Assert.Equal(1, selection.Commands);
        Assert.Null(unknown.Result.Category);
        Assert.Null(unknown.Result.Priority);
    }

    [Fact]
    public async Task DraftSelection_ContactMustHoldABusinessRoleAndBeAssignedToTheSelectedSite()
    {
        var world = await WithDbAsync(ScopeWorld.CreateAsync);
        var requester = await UserAsync(world, [world.SiteA1], RoleCodes.Requester);
        var technician = await UserAsync(world, [world.SiteA1], RoleCodes.Technician);
        var administratorRequester = await UserAsync(world, [world.SiteA1], RoleCodes.Administrator, RoleCodes.Requester);
        var administratorOnly = await UserAsync(world, [world.SiteA1], RoleCodes.Administrator);
        var withoutRole = await UserAsync(world, [world.SiteA1]);
        var assignedElsewhere = await UserAsync(world, [world.SiteA2], RoleCodes.Requester);
        var otherTenant = await UserInTenantAsync(world.OtherTenantId, [world.OtherTenantSite], RoleCodes.Requester);

        await using var scope = _host.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IRepairRequestDraftStore>();

        async Task<bool> Eligible(Guid contactId) =>
            (await store.GetDraftSelectionAsync(requester, Query(world.SiteA1.Id, contactId: contactId), None)).ContactEligible;

        Assert.True(await Eligible(requester.UserId));
        Assert.True(await Eligible(technician.UserId));
        Assert.True(await Eligible(administratorRequester.UserId));
        Assert.False(await Eligible(administratorOnly.UserId));
        Assert.False(await Eligible(withoutRole.UserId));
        Assert.False(await Eligible(assignedElsewhere.UserId));
        Assert.False(await Eligible(otherTenant.UserId));
        Assert.False(await Eligible(Guid.NewGuid()));
        Assert.False((await store.GetDraftSelectionAsync(requester, Query(contactId: technician.UserId), None)).ContactEligible);
    }

    [Fact]
    public async Task Detail_IsOneQuery_AndOwnedLoadExcludesEveryoneButTheOwner()
    {
        var world = await WithDbAsync(ScopeWorld.CreateAsync);
        var owner = await UserAsync(world, [world.SiteA1], RoleCodes.Requester);
        var otherRequester = await UserAsync(world, [world.SiteA1], RoleCodes.Requester);
        var approverRequester = await UserAsync(world, [world.SiteA1], RoleCodes.Approver, RoleCodes.Requester);
        var draft = await CreateDraftAsync(owner, Fields(world.SiteA1.Id, "Leak"));

        var ownerDetail = await MeasureAsync(store => store.GetAsync(owner, draft.Id, None));

        await using var scope = _host.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IRepairRequestDraftStore>();

        Assert.Equal("Leak", ownerDetail.Result!.Description);
        Assert.Equal(1, ownerDetail.Commands);
        Assert.NotNull(await store.FindOwnAsync(owner, draft.Id, None));
        Assert.Null(await store.GetAsync(otherRequester, draft.Id, None));
        Assert.Null(await store.FindOwnAsync(otherRequester, draft.Id, None));

        // Decision E3: a site-wide role may view the Draft but is never its owner.
        Assert.NotNull(await store.GetAsync(approverRequester, draft.Id, None));
        Assert.Null(await store.FindOwnAsync(approverRequester, draft.Id, None));
    }

    [Fact]
    public async Task Update_WithTokenFromBeforeAConcurrentChange_WritesNothing()
    {
        var world = await WithDbAsync(ScopeWorld.CreateAsync);
        var owner = await UserAsync(world, [world.SiteA1], RoleCodes.Requester);
        var draft = await CreateDraftAsync(owner, Fields(description: "Original"));

        await WithDbAsync(db => db.RepairRequests
            .Where(request => request.Id == draft.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(request => request.Description, "Other writer")));

        await using (var scope = _host.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<RepairRequestDraftService>();
            var result = await service.UpdateAsync(
                new CommandContext(owner, Guid.NewGuid()), draft.Id, draft.RowVersion, Fields(description: "Mine"), None);

            Assert.Equal(CommandFailure.ConcurrencyConflict, result.Error?.Failure);
        }

        Assert.Equal("Other writer", await WithDbAsync(db => db.RepairRequests
            .Where(request => request.Id == draft.Id)
            .Select(request => request.Description)
            .SingleAsync()));
    }

    [Fact]
    public async Task SaveChanges_WithStaleTokenAtWriteTime_IsRejectedByTheDatabase()
    {
        var world = await WithDbAsync(ScopeWorld.CreateAsync);
        var owner = await UserAsync(world, [world.SiteA1], RoleCodes.Requester);
        var draft = await CreateDraftAsync(owner, Fields(description: "Original"));

        await using var scope = _host.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IRepairRequestDraftStore>();
        var tracked = await store.FindOwnAsync(owner, draft.Id, None);

        await WithDbAsync(db => db.RepairRequests
            .Where(request => request.Id == draft.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(request => request.Description, "Other writer")));

        tracked!.EditDraft(null, null, null, null, null, "Mine", null, null);

        Assert.Equal(RepairRequestSaveOutcome.ConcurrencyConflict, await store.SaveChangesAsync(tracked, draft.RowVersion, None));
        Assert.Equal("Other writer", await WithDbAsync(db => db.RepairRequests
            .Where(request => request.Id == draft.Id)
            .Select(request => request.Description)
            .SingleAsync()));
    }

    [Fact]
    public async Task AuditWriteFailure_RollsBackDraftCreateAndEdit()
    {
        var world = await WithDbAsync(ScopeWorld.CreateAsync);
        var owner = await UserAsync(world, [world.SiteA1], RoleCodes.Requester);
        var existing = await CreateDraftAsync(owner, Fields(description: "Original"));

        await using var failingHost = new AuthenticationTestHost(services =>
            services.ConfigureDbContext<RepairRequestDbContext>(options => options.AddInterceptors(new FailAuditInsertInterceptor())));

        await using (var scope = failingHost.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<RepairRequestDraftService>();
            var context = new CommandContext(owner, Guid.NewGuid());

            await Assert.ThrowsAnyAsync<Exception>(() => service.CreateAsync(context, Fields(description: "Never saved"), None));
            await Assert.ThrowsAnyAsync<Exception>(() =>
                service.UpdateAsync(context, existing.Id, existing.RowVersion, Fields(description: "Never saved"), None));
        }

        Assert.Equal(new[] { "Original" }, await WithDbAsync(db => db.RepairRequests
            .Where(request => request.TenantId == world.TenantId)
            .Select(request => request.Description)
            .ToListAsync()));
        Assert.Single(await WithDbAsync(db => db.AuditHistory
            .Where(audit => audit.TenantId == world.TenantId && audit.EntityType == RepairRequestAudit.EntityType)
            .ToListAsync()));
    }

    private sealed class FailAuditInsertInterceptor : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            FailOnAuditWrite(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            FailOnAuditWrite(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        private static void FailOnAuditWrite(DbCommand command)
        {
            if (command.CommandText.Contains("INSERT INTO [audit_history]", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Simulated audit write failure.");
            }
        }
    }
}
