using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using RepairRequest.Domain.MasterData;
using RepairRequest.Infrastructure.Identity;
using RepairRequest.Infrastructure.Persistence;
using RepairRequest.Infrastructure.RepairRequests;

namespace RepairRequest.IntegrationTests.Persistence;

/// <summary>
/// Upgrade paths of data-bearing migrations:
/// <list type="bullet">
/// <item>DEC-PRE-S1-007-03: the AddRepairRequestSubmit migration seeds the placeholder Category/Priority lookups for every
/// tenant that already has users, exactly once per tenant, when upgrading a database that existed before S1-007.</item>
/// <item>DEC-PRE-S1-010-01: the AddApprovalCycle migration places every existing approval row in approval cycle 1.</item>
/// </list>
/// </summary>
public sealed class MigrationSeedTests : IAsyncLifetime
{
    private const string ConnectionString =
        "Server=(localdb)\\MSSQLLocalDB;Database=RepairRequestDb_MigrationSeedTest;Trusted_Connection=True;TrustServerCertificate=True";

    private const string LastMigrationBeforeS1007 = "20260913144322_AddRefreshTokenAndRoleSeed";
    private const string LastMigrationBeforeS1010 = "20260915161441_AddApprovalRouting";

    private static RepairRequestDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<RepairRequestDbContext>().UseSqlServer(ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var context = CreateContext();
        await context.Database.EnsureDeletedAsync();
    }

    public async Task DisposeAsync()
    {
        await using var context = CreateContext();
        await context.Database.EnsureDeletedAsync();
    }

    private static ApplicationUser NewUser(Guid tenantId)
    {
        var userName = $"user-{Guid.NewGuid():N}";
        return new ApplicationUser
        {
            TenantId = tenantId,
            UserName = userName,
            NormalizedUserName = userName.ToUpperInvariant(),
            SecurityStamp = Guid.NewGuid().ToString()
        };
    }

    [Fact]
    public async Task UpgradingAnExistingDatabase_SeedsLookupsOncePerTenantWithUsers()
    {
        var tenantWithTwoUsers = Guid.NewGuid();
        var tenantWithOneUser = Guid.NewGuid();
        var tenantWithoutUsers = Guid.NewGuid();

        await using (var context = CreateContext())
        {
            await context.GetService<IMigrator>().MigrateAsync(LastMigrationBeforeS1007);

            context.Users.AddRange(NewUser(tenantWithTwoUsers), NewUser(tenantWithTwoUsers), NewUser(tenantWithOneUser));
            context.Customers.Add(new Customer(tenantWithoutUsers, "CUST-NO-USERS"));
            await context.SaveChangesAsync();
        }

        await using (var context = CreateContext())
        {
            await context.GetService<IMigrator>().MigrateAsync();
            Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        }

        await using var verify = CreateContext();
        var categories = await verify.RequestCategories.AsNoTracking().ToListAsync();
        var priorities = await verify.RequestPriorities.AsNoTracking().ToListAsync();

        foreach (var tenant in new[] { tenantWithTwoUsers, tenantWithOneUser })
        {
            Assert.Equal(
                RequestLookupSeeder.CategorySeed.Select(seed => seed.Code).Order(),
                categories.Where(category => category.TenantId == tenant).Select(category => category.Code).Order());
            Assert.Equal(
                RequestLookupSeeder.PrioritySeed.Select(seed => seed.Code).Order(),
                priorities.Where(priority => priority.TenantId == tenant).Select(priority => priority.Code).Order());
        }

        Assert.All(categories, category => Assert.Equal(MasterDataStatus.Active, category.Status));
        Assert.All(priorities, priority => Assert.Equal(MasterDataStatus.Active, priority.Status));
        Assert.DoesNotContain(categories, category => category.TenantId == tenantWithoutUsers);
        Assert.Equal(2 * RequestLookupSeeder.CategorySeed.Count, categories.Count);
        Assert.Equal(2 * RequestLookupSeeder.PrioritySeed.Count, priorities.Count);
    }

    [Fact]
    public async Task UpgradingAnExistingDatabase_PlacesExistingApprovalRowsInTheFirstCycle()
    {
        var approvalId = Guid.NewGuid();

        await using (var context = CreateContext())
        {
            await context.GetService<IMigrator>().MigrateAsync(LastMigrationBeforeS1010);

            // Only the approval row's own shape matters here; its foreign keys are not what this upgrade changes.
            await context.Database.ExecuteSqlAsync($"""
                ALTER TABLE [repair_request_approval] NOCHECK CONSTRAINT ALL;
                INSERT INTO [repair_request_approval]
                    ([approval_id], [tenant_id], [repair_request_id], [approval_route_id], [approval_step_no], [assigned_approver_id], [status], [routed_at])
                VALUES ({approvalId}, {Guid.NewGuid()}, {Guid.NewGuid()}, {Guid.NewGuid()}, 1, {Guid.NewGuid()}, 'PENDING', SYSUTCDATETIME());
                """);
        }

        await using (var context = CreateContext())
        {
            // The added CHECK ([approval_cycle_no] >= 1) validates the existing row, so a wrong default would fail here.
            await context.GetService<IMigrator>().MigrateAsync();
            Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        }

        await using var verify = CreateContext();
        var cycles = await verify.Database
            .SqlQuery<short>($"SELECT [approval_cycle_no] AS [Value] FROM [repair_request_approval] WHERE [approval_id] = {approvalId}")
            .ToListAsync();
        Assert.Equal((short)1, Assert.Single(cycles));
    }
}
