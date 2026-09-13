using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using RepairRequest.Domain.Security;
using RepairRequest.Infrastructure.Identity;
using RepairRequest.IntegrationTests.Persistence;

namespace RepairRequest.IntegrationTests.Authentication;

/// <summary>Migration-based role seed: exactly the approved catalog, idempotent, no users or credentials.</summary>
[Collection(PersistenceDatabaseCollection.Name)]
public class RoleSeedTests
{
    private readonly PersistenceDatabaseFixture _database;

    public RoleSeedTests(PersistenceDatabaseFixture database)
    {
        _database = database;
    }

    [Fact]
    public async Task SeededRoles_MatchApprovedCatalogExactly()
    {
        await using var context = _database.CreateContext();

        var roles = await context.Roles.AsNoTracking().ToListAsync();

        Assert.Equal(RoleCodes.All.Order(), roles.Select(role => role.Name!).Order());
        Assert.All(roles, role => Assert.Equal(role.Name, role.NormalizedName));
    }

    [Fact]
    public async Task ReapplyingMigrations_DoesNotDuplicateOrChangeRoles()
    {
        await using var context = _database.CreateContext();
        var before = await context.Roles.AsNoTracking().OrderBy(role => role.Id).Select(role => new { role.Id, role.Name, role.ConcurrencyStamp }).ToListAsync();

        await context.Database.MigrateAsync();

        var after = await context.Roles.AsNoTracking().OrderBy(role => role.Id).Select(role => new { role.Id, role.Name, role.ConcurrencyStamp }).ToListAsync();
        Assert.Equal(RoleCodes.All.Count, after.Count);
        Assert.Equal(before, after);
    }

    [Fact]
    public void Seed_ContainsNoUsersRoleAssignmentsClaimsOrTokens()
    {
        using var context = _database.CreateContext();
        var model = context.GetService<IDesignTimeModel>().Model;

        Type[] identityTypes =
        [
            typeof(ApplicationUser),
            typeof(IdentityUserRole<Guid>),
            typeof(IdentityUserClaim<Guid>),
            typeof(IdentityUserToken<Guid>),
            typeof(IdentityUserLogin<Guid>),
            typeof(IdentityRoleClaim<Guid>),
            typeof(RefreshToken)
        ];

        Assert.All(identityTypes, type => Assert.Empty(model.FindEntityType(type)!.GetSeedData()));
    }
}
