using Microsoft.EntityFrameworkCore;
using RepairRequest.Infrastructure.Persistence;

namespace RepairRequest.IntegrationTests;

/// <summary>
/// Sprint 0 Definition of Done: "API connects to SQL and EF Core migration can
/// create baseline schema in a clean development database." Runs against a
/// disposable LocalDB database so it does not disturb the shared dev database.
/// Requires MSSQLLocalDB to be running locally (see repo Sprint 0 prerequisites).
/// </summary>
public class EfCoreLocalDbConnectivityTests : IAsyncLifetime
{
    private const string ConnectionString =
        "Server=(localdb)\\MSSQLLocalDB;Database=RepairRequestDb_IntegrationTest;Trusted_Connection=True;TrustServerCertificate=True";

    private RepairRequestDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<RepairRequestDbContext>()
            .UseSqlServer(ConnectionString)
            .Options;

        return new RepairRequestDbContext(options);
    }

    [Fact]
    public async Task Database_CanConnect_AfterMigrate()
    {
        await using var context = CreateContext();

        await context.Database.MigrateAsync();

        var canConnect = await context.Database.CanConnectAsync();

        Assert.True(canConnect);
    }

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
}
