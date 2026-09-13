using Microsoft.EntityFrameworkCore;
using RepairRequest.Infrastructure.Persistence;

namespace RepairRequest.IntegrationTests.Persistence;

/// <summary>
/// Creates a disposable LocalDB database from the EF Core migrations once per test run
/// (RR-ARCH-001 section 19: mappings and constraints verified against real SQL Server).
/// Tests isolate their data by using fresh tenant identifiers rather than resetting the database.
/// </summary>
public sealed class PersistenceDatabaseFixture : IAsyncLifetime
{
    private const string ConnectionString =
        "Server=(localdb)\\MSSQLLocalDB;Database=RepairRequestDb_PersistenceTest;Trusted_Connection=True;TrustServerCertificate=True";

    public RepairRequestDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<RepairRequestDbContext>()
            .UseSqlServer(ConnectionString)
            .Options;

        return new RepairRequestDbContext(options);
    }

    public async Task InitializeAsync()
    {
        await using var context = CreateContext();
        await context.Database.EnsureDeletedAsync();
        await context.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await using var context = CreateContext();
        await context.Database.EnsureDeletedAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class PersistenceDatabaseCollection : ICollectionFixture<PersistenceDatabaseFixture>
{
    public const string Name = "Persistence database";
}
