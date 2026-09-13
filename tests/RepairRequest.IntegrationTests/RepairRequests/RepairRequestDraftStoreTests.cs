using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using RepairRequest.Application.Common;
using RepairRequest.Application.RepairRequests;
using RepairRequest.Application.Security;
using RepairRequest.Domain.MasterData;
using RepairRequest.Domain.Security;
using RepairRequest.Infrastructure.Persistence;
using RepairRequest.IntegrationTests.Authentication;
using RepairRequest.IntegrationTests.Authorization;
using RepairRequest.IntegrationTests.Persistence;

namespace RepairRequest.IntegrationTests.RepairRequests;

/// <summary>
/// Repair Request Draft persistence against LocalDB: scoped single-query selection and detail, owner-only tracked load,
/// row-version protection and audit atomicity (UC-RR-001; RR-ARCH-001 section 8; TC-SEC-001/002).
/// </summary>
[Collection(PersistenceDatabaseCollection.Name)]
public sealed class RepairRequestDraftStoreTests : IAsyncLifetime
{
    private readonly AuthenticationTestHost _host = new();

    public RepairRequestDraftStoreTests(PersistenceDatabaseFixture database)
    {
        _ = database;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _host.DisposeAsync();

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

    private async Task<CurrentUser> UserAsync(ScopeWorld world, Site[] sites, params string[] roles)
    {
        var user = await _host.CreateUserInTenantAsync(world.TenantId, roles);
        await WithDbAsync(db => ScopeWorld.AssignSitesAsync(db, world.TenantId, user.Id, sites));
        return new CurrentUser(user.Id, world.TenantId, roles);
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
            .CreateAsync(new CommandContext(owner, Guid.NewGuid()), fields, CancellationToken.None);
        Assert.True(result.Succeeded);
        return result.Value!;
    }

    [Fact]
    public async Task SiteSelection_IsOneScopedQuery_AndTiesEquipmentToTheSite()
    {
        var world = await WithDbAsync(ScopeWorld.CreateAsync);
        var requester = await UserAsync(world, [world.SiteA1], RoleCodes.Requester);

        var valid = await MeasureAsync(store => store.GetSiteSelectionAsync(requester, world.SiteA1.Id, world.EquipmentA1.Id, CancellationToken.None));
        var siteOnly = await MeasureAsync(store => store.GetSiteSelectionAsync(requester, world.SiteA1.Id, null, CancellationToken.None));
        var foreignEquipment = await MeasureAsync(store => store.GetSiteSelectionAsync(requester, world.SiteA1.Id, world.EquipmentB1.Id, CancellationToken.None));
        var otherTenantEquipment = await MeasureAsync(store => store.GetSiteSelectionAsync(requester, world.SiteA1.Id, world.OtherTenantEquipment.Id, CancellationToken.None));
        var unassignedSite = await MeasureAsync(store => store.GetSiteSelectionAsync(requester, world.SiteA2.Id, null, CancellationToken.None));
        var otherTenantSite = await MeasureAsync(store => store.GetSiteSelectionAsync(requester, world.OtherTenantSite.Id, null, CancellationToken.None));

        Assert.Equal(new DraftSiteSelection(MasterDataStatus.Active, MasterDataStatus.Active), valid.Result);
        Assert.Equal(1, valid.Commands);
        Assert.Equal(new DraftSiteSelection(MasterDataStatus.Active, null), siteOnly.Result);
        Assert.Null(foreignEquipment.Result!.EquipmentStatus);
        Assert.Null(otherTenantEquipment.Result!.EquipmentStatus);
        Assert.Null(unassignedSite.Result);
        Assert.Null(otherTenantSite.Result);
    }

    [Fact]
    public async Task SiteSelection_ForAdministratorRequester_IsLimitedToAssignedSites()
    {
        var world = await WithDbAsync(ScopeWorld.CreateAsync);
        var adminRequester = await UserAsync(world, [world.SiteA1], RoleCodes.Administrator, RoleCodes.Requester);

        await using var scope = _host.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IRepairRequestDraftStore>();
        var none = CancellationToken.None;

        Assert.Equal(
            new DraftSiteSelection(MasterDataStatus.Active, MasterDataStatus.Active),
            await store.GetSiteSelectionAsync(adminRequester, world.SiteA1.Id, world.EquipmentA1.Id, none));
        Assert.Null(await store.GetSiteSelectionAsync(adminRequester, world.SiteA2.Id, null, none));
        Assert.Null(await store.GetSiteSelectionAsync(adminRequester, world.SiteB1.Id, world.EquipmentB1.Id, none));
        Assert.Null(await store.GetSiteSelectionAsync(adminRequester, world.OtherTenantSite.Id, null, none));
    }

    [Fact]
    public async Task Detail_IsOneQuery_AndOwnedLoadExcludesEveryoneButTheOwner()
    {
        var world = await WithDbAsync(ScopeWorld.CreateAsync);
        var owner = await UserAsync(world, [world.SiteA1], RoleCodes.Requester);
        var otherRequester = await UserAsync(world, [world.SiteA1], RoleCodes.Requester);
        var approverRequester = await UserAsync(world, [world.SiteA1], RoleCodes.Approver, RoleCodes.Requester);
        var draft = await CreateDraftAsync(owner, new RepairRequestDraftFields(world.SiteA1.Id, null, "Leak", null, null));

        var ownerDetail = await MeasureAsync(store => store.GetAsync(owner, draft.Id, CancellationToken.None));

        await using var scope = _host.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IRepairRequestDraftStore>();

        Assert.Equal("Leak", ownerDetail.Result!.Description);
        Assert.Equal(1, ownerDetail.Commands);
        Assert.NotNull(await store.FindOwnAsync(owner, draft.Id, CancellationToken.None));
        Assert.Null(await store.GetAsync(otherRequester, draft.Id, CancellationToken.None));
        Assert.Null(await store.FindOwnAsync(otherRequester, draft.Id, CancellationToken.None));

        // Decision E3: a site-wide role may view the Draft but is never its owner.
        Assert.NotNull(await store.GetAsync(approverRequester, draft.Id, CancellationToken.None));
        Assert.Null(await store.FindOwnAsync(approverRequester, draft.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Update_WithTokenFromBeforeAConcurrentChange_WritesNothing()
    {
        var world = await WithDbAsync(ScopeWorld.CreateAsync);
        var owner = await UserAsync(world, [world.SiteA1], RoleCodes.Requester);
        var draft = await CreateDraftAsync(owner, new RepairRequestDraftFields(null, null, "Original", null, null));

        await WithDbAsync(db => db.RepairRequests
            .Where(request => request.Id == draft.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(request => request.Description, "Other writer")));

        await using (var scope = _host.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<RepairRequestDraftService>();
            var result = await service.UpdateAsync(
                new CommandContext(owner, Guid.NewGuid()), draft.Id, draft.RowVersion, new RepairRequestDraftFields(null, null, "Mine", null, null), CancellationToken.None);

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
        var draft = await CreateDraftAsync(owner, new RepairRequestDraftFields(null, null, "Original", null, null));

        await using var scope = _host.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IRepairRequestDraftStore>();
        var tracked = await store.FindOwnAsync(owner, draft.Id, CancellationToken.None);

        await WithDbAsync(db => db.RepairRequests
            .Where(request => request.Id == draft.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(request => request.Description, "Other writer")));

        tracked!.EditDraft(null, null, "Mine", null, null);

        Assert.Equal(RepairRequestSaveOutcome.ConcurrencyConflict, await store.SaveChangesAsync(tracked, draft.RowVersion, CancellationToken.None));
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
        var existing = await CreateDraftAsync(owner, new RepairRequestDraftFields(null, null, "Original", null, null));

        await using var failingHost = new AuthenticationTestHost(services =>
            services.ConfigureDbContext<RepairRequestDbContext>(options => options.AddInterceptors(new FailAuditInsertInterceptor())));

        await using (var scope = failingHost.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<RepairRequestDraftService>();
            var context = new CommandContext(owner, Guid.NewGuid());

            await Assert.ThrowsAnyAsync<Exception>(() =>
                service.CreateAsync(context, new RepairRequestDraftFields(null, null, "Never saved", null, null), CancellationToken.None));
            await Assert.ThrowsAnyAsync<Exception>(() =>
                service.UpdateAsync(context, existing.Id, existing.RowVersion, new RepairRequestDraftFields(null, null, "Never saved", null, null), CancellationToken.None));
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
