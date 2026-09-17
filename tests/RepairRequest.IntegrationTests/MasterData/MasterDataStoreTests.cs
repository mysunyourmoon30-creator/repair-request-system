using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using RepairRequest.Application.Common;
using RepairRequest.Application.MasterData;
using RepairRequest.Application.Security;
using RepairRequest.Domain.MasterData;
using RepairRequest.Domain.Security;
using RepairRequest.Infrastructure.Persistence;
using RepairRequest.IntegrationTests.Authentication;
using RepairRequest.IntegrationTests.Authorization;
using RepairRequest.IntegrationTests.Persistence;

namespace RepairRequest.IntegrationTests.MasterData;

/// <summary>
/// Master-data persistence against LocalDB: server-side scope and paging, bounded SQL command counts,
/// scoped unique-code translation, row-version protection and audit atomicity (RR-ARCH-001 section 8).
/// </summary>
[Collection(PersistenceDatabaseCollection.Name)]
public sealed class MasterDataStoreTests : IAsyncLifetime
{
    private readonly AuthenticationTestHost _host = new();

    public MasterDataStoreTests(PersistenceDatabaseFixture database)
    {
        _ = database;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private static CurrentUser Administrator(Guid tenantId) => new(Guid.NewGuid(), tenantId, [RoleCodes.Administrator]);

    private static MasterDataListQuery Page(int page, int pageSize, MasterDataStatus? status = null) =>
        new(new PageRequest(page, pageSize), status);

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

    private Task<ScopeWorld> NewWorldAsync() => WithDbAsync(ScopeWorld.CreateAsync);

    private async Task<(T Result, int Commands)> MeasureAsync<T>(Func<IMasterDataStore, Task<T>> query)
    {
        await using var scope = _host.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IMasterDataStore>();

        _host.Logs.Clear();
        var result = await query(store);
        return (result, _host.Logs.ExecutedDbCommandCount);
    }

    [Fact]
    public async Task ListAndDetail_UseAConstantNumberOfSqlCommands_RegardlessOfRowCount()
    {
        var world = await NewWorldAsync();
        var admin = Administrator(world.TenantId);

        var small = await MeasureAsync(store => store.ListSitesAsync(admin, world.CustomerA.Id, Page(1, 100), CancellationToken.None));

        await WithDbAsync(async db =>
        {
            for (var index = 0; index < 15; index++)
            {
                db.Sites.Add(new Site(world.TenantId, world.CustomerA.Id, $"BULK-{index:00}"));
            }

            await db.SaveChangesAsync();
        });

        var large = await MeasureAsync(store => store.ListSitesAsync(admin, world.CustomerA.Id, Page(1, 100), CancellationToken.None));
        var detail = await MeasureAsync(store => store.GetSiteAsync(admin, world.SiteA1.Id, CancellationToken.None));
        var customers = await MeasureAsync(store => store.ListCustomersAsync(admin, Page(1, 100), CancellationToken.None));

        Assert.Equal(17, large.Result!.TotalCount);
        Assert.Equal(17, large.Result.Items.Count);
        Assert.Equal(small.Commands, large.Commands);
        Assert.Equal(3, large.Commands);
        Assert.NotNull(detail.Result);
        Assert.Equal(1, detail.Commands);
        Assert.Equal(2, customers.Commands);
    }

    [Fact]
    public async Task ScopedLookups_ReturnNothingOutsideTenantOrAssignedSites()
    {
        var world = await NewWorldAsync();
        var user = await _host.CreateUserInTenantAsync(world.TenantId, RoleCodes.Technician);
        await WithDbAsync(db => ScopeWorld.AssignSitesAsync(db, world.TenantId, user.Id, world.SiteA1));
        var technician = new CurrentUser(user.Id, world.TenantId, [RoleCodes.Technician]);
        var admin = Administrator(world.TenantId);

        await using var scope = _host.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IMasterDataStore>();
        var none = CancellationToken.None;

        Assert.NotNull(await store.GetSiteAsync(technician, world.SiteA1.Id, none));
        Assert.Null(await store.GetSiteAsync(technician, world.SiteA2.Id, none));
        Assert.Null(await store.GetSiteAsync(technician, world.OtherTenantSite.Id, none));
        Assert.NotNull(await store.GetEquipmentAsync(technician, world.EquipmentA1.Id, none));
        Assert.Null(await store.FindEquipmentAsync(technician, world.EquipmentB1.Id, none));
        Assert.Null(await store.ListEquipmentAsync(technician, world.SiteB1.Id, Page(1, 10), none));
        Assert.Null(await store.ListSitesAsync(technician, world.CustomerB.Id, Page(1, 10), none));

        var sites = await store.ListSitesAsync(technician, world.CustomerA.Id, Page(1, 10), none);
        Assert.Equal(world.SiteA1.Id, Assert.Single(sites!.Items).Id);

        Assert.Null(await store.FindSiteAsync(admin, world.OtherTenantSite.Id, none));
        Assert.Null(await store.GetCustomerStatusAsync(admin, world.OtherTenantSite.CustomerId, none));
        Assert.Null(await store.GetSiteStatusAsync(admin, world.OtherTenantSite.Id, none));
        Assert.Null(await store.FindEquipmentAsync(admin, world.OtherTenantEquipment.Id, none));
    }

    [Fact]
    public async Task List_AppliesStatusFilterSortAndPagingInTheDatabase()
    {
        var tenantId = Guid.NewGuid();
        var customer = new Customer(tenantId, "CUST-PAGE");
        await WithDbAsync(async db =>
        {
            db.Customers.Add(customer);
            foreach (var code in new[] { "S-05", "S-04", "S-03", "S-02", "S-01" })
            {
                var site = new Site(tenantId, customer.Id, code);
                if (code is "S-02" or "S-04")
                {
                    site.Deactivate("Closed");
                }

                db.Sites.Add(site);
            }

            await db.SaveChangesAsync();
        });

        var admin = Administrator(tenantId);
        await using var scope = _host.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IMasterDataStore>();

        var first = await store.ListSitesAsync(admin, customer.Id, Page(1, 2, MasterDataStatus.Active), CancellationToken.None);
        var second = await store.ListSitesAsync(admin, customer.Id, Page(2, 2, MasterDataStatus.Active), CancellationToken.None);
        var beyond = await store.ListSitesAsync(admin, customer.Id, Page(int.MaxValue, 100, MasterDataStatus.Active), CancellationToken.None);
        var inactive = await store.ListSitesAsync(admin, customer.Id, Page(1, 10, MasterDataStatus.Inactive), CancellationToken.None);

        Assert.Equal(3, first!.TotalCount);
        Assert.Equal(new[] { "S-01", "S-03" }, first.Items.Select(site => site.SiteCode));
        Assert.Equal(new[] { "S-05" }, second!.Items.Select(site => site.SiteCode));
        Assert.Empty(beyond!.Items);
        Assert.Equal(3, beyond.TotalCount);
        Assert.Equal(new[] { "S-02", "S-04" }, inactive!.Items.Select(site => site.SiteCode));
    }

    [Fact]
    public async Task SaveChanges_ScopedUniqueViolation_IsReportedAsDuplicateCode_CaseInsensitively()
    {
        var tenantId = Guid.NewGuid();

        await using var scope = _host.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IMasterDataStore>();
        store.Add(new Customer(tenantId, "RACE-1"));
        store.Add(new Customer(tenantId, "race-1"));

        Assert.Equal(MasterDataSaveOutcome.DuplicateCode, await store.SaveChangesAsync(null, null, CancellationToken.None));
        Assert.False(await WithDbAsync(db => db.Customers.AnyAsync(customer => customer.TenantId == tenantId)));
    }

    [Fact]
    public async Task SaveChanges_WithStaleRowVersion_WritesNothing()
    {
        var world = await NewWorldAsync();
        var admin = Administrator(world.TenantId);

        await using var scope = _host.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IMasterDataStore>();
        var site = await store.FindSiteAsync(admin, world.SiteA1.Id, CancellationToken.None);
        var tokenReadByClient = site!.RowVersion.ToArray();

        // Another request changes the row after this client read it.
        await WithDbAsync(db => db.Sites
            .Where(candidate => candidate.Id == world.SiteA1.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(candidate => candidate.SiteCode, "OTHER-WRITER")));

        site.ChangeCode("MINE");
        var outcome = await store.SaveChangesAsync(site, tokenReadByClient, CancellationToken.None);

        Assert.Equal(MasterDataSaveOutcome.ConcurrencyConflict, outcome);
        Assert.Equal("OTHER-WRITER", await WithDbAsync(db => db.Sites
            .Where(candidate => candidate.Id == world.SiteA1.Id)
            .Select(candidate => candidate.SiteCode)
            .SingleAsync()));
    }

    [Fact]
    public async Task ServiceUpdate_WithTokenFromBeforeAConcurrentChange_Returns409()
    {
        var world = await NewWorldAsync();
        var context = new CommandContext(Administrator(world.TenantId), Guid.NewGuid());
        var originalToken = world.SiteA1.RowVersion.ToArray();

        await using (var scope = _host.CreateScope())
        {
            var first = await scope.ServiceProvider.GetRequiredService<SiteService>()
                .UpdateAsync(context, world.SiteA1.Id, originalToken, "FIRST-WRITER", CancellationToken.None);
            Assert.True(first.Succeeded);
        }

        await using (var scope = _host.CreateScope())
        {
            var second = await scope.ServiceProvider.GetRequiredService<SiteService>()
                .UpdateAsync(context, world.SiteA1.Id, originalToken, "SECOND-WRITER", CancellationToken.None);
            Assert.Equal(CommandFailure.ConcurrencyConflict, second.Error?.Failure);
        }

        Assert.Equal("FIRST-WRITER", await WithDbAsync(db => db.Sites
            .Where(site => site.Id == world.SiteA1.Id)
            .Select(site => site.SiteCode)
            .SingleAsync()));
    }

    [Fact]
    public async Task ActiveChildCounts_CountOnlyActiveChildrenOfTheParent()
    {
        var world = await NewWorldAsync();

        await using var scope = _host.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IMasterDataStore>();

        Assert.Equal(2, await store.CountActiveSitesAsync(world.TenantId, world.CustomerA.Id, CancellationToken.None));
        Assert.Equal(1, await store.CountActiveEquipmentAsync(world.TenantId, world.SiteA1.Id, CancellationToken.None));

        await WithDbAsync(db => ScopeWorld.DeactivateSiteAsync(db, world.SiteA2.Id));

        Assert.Equal(1, await store.CountActiveSitesAsync(world.TenantId, world.CustomerA.Id, CancellationToken.None));
        Assert.Equal(0, await store.CountActiveSitesAsync(world.OtherTenantId, world.CustomerA.Id, CancellationToken.None));
    }

    [Fact]
    public async Task AuditWriteFailure_RollsBackTheMasterChange()
    {
        var world = await NewWorldAsync();
        var context = new CommandContext(Administrator(world.TenantId), Guid.NewGuid());

        await using var failingHost = new AuthenticationTestHost(services =>
            services.ConfigureDbContext<RepairRequestDbContext>(options => options.AddInterceptors(new FailAuditInsertInterceptor())));

        await using (var scope = failingHost.CreateScope())
        {
            var customers = scope.ServiceProvider.GetRequiredService<CustomerService>();
            await Assert.ThrowsAnyAsync<Exception>(() => customers.CreateAsync(context, "ATOMIC-1", CancellationToken.None));
        }

        await using (var scope = failingHost.CreateScope())
        {
            var sites = scope.ServiceProvider.GetRequiredService<SiteService>();
            await Assert.ThrowsAnyAsync<Exception>(() =>
                sites.DeactivateAsync(context, world.SiteA2.Id, world.SiteA2.RowVersion, "Closed", CancellationToken.None));
        }

        Assert.False(await WithDbAsync(db => db.Customers.AnyAsync(customer =>
            customer.TenantId == world.TenantId && customer.CustomerCode == "ATOMIC-1")));
        Assert.Equal(MasterDataStatus.Active, await WithDbAsync(db => db.Sites
            .Where(site => site.Id == world.SiteA2.Id)
            .Select(site => site.Status)
            .SingleAsync()));
        Assert.False(await WithDbAsync(db => db.AuditHistory.AnyAsync(audit => audit.TenantId == world.TenantId)));
    }

    [Fact]
    public async Task SuccessfulCommand_WritesChangeAndAuditTogether()
    {
        var world = await NewWorldAsync();
        var context = new CommandContext(Administrator(world.TenantId), Guid.NewGuid());

        await using (var scope = _host.CreateScope())
        {
            var sites = scope.ServiceProvider.GetRequiredService<SiteService>();
            var result = await sites.DeactivateAsync(context, world.SiteA2.Id, world.SiteA2.RowVersion, "Closed", CancellationToken.None);
            Assert.True(result.Succeeded);
            Assert.False(result.Value!.RowVersion.AsSpan().SequenceEqual(world.SiteA2.RowVersion));
        }

        var audit = await WithDbAsync(db => db.AuditHistory.AsNoTracking().SingleAsync(item => item.EntityId == world.SiteA2.Id));
        Assert.Equal("SITE_DEACTIVATED", audit.ActionCode);
        Assert.Equal(context.CorrelationId, audit.CorrelationId);
        Assert.Equal("Closed", audit.Reason);
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
