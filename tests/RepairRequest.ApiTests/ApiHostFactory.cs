using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RepairRequest.Infrastructure.Identity;
using RepairRequest.Infrastructure.Persistence;

namespace RepairRequest.ApiTests;

/// <summary>
/// Boots the real API in an isolated "Testing" environment: developer user-secrets and
/// appsettings.Development.json are not loaded, and a random per-run signing key is supplied
/// in memory. Issuer, audience and lifetimes come from the committed appsettings.json.
/// </summary>
public class ApiHostFactory : WebApplicationFactory<Program>
{
    /// <summary>Each database-backed factory uses its own disposable database so test classes can run in parallel.</summary>
    public virtual string ConnectionString =>
        "Server=(localdb)\\MSSQLLocalDB;Database=RepairRequestDb_ApiTest;Trusted_Connection=True;TrustServerCertificate=True";

    public string SigningKey { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    protected virtual IDictionary<string, string?> TestSettings => new Dictionary<string, string?>
    {
        ["ConnectionStrings:DefaultConnection"] = ConnectionString,
        ["Authentication:Jwt:SigningKey"] = SigningKey
    };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(TestSettings));
    }
}

/// <summary>
/// API host backed by a disposable, migrated LocalDB database for authentication flows. Creating (delete, create, migrate) and dropping the
/// database run inside <see cref="LocalDbLifecycleGate"/>, so concurrently starting test classes never create or drop databases at the same time.
/// </summary>
public class AuthApiFactory : ApiHostFactory, IAsyncLifetime
{
    public const string ValidPassword = "Correct-Horse-42!";

    /// <summary>The lifecycle gate this fixture uses (the shared PR #26 gate); only the lifecycle probe tests give theirs a resource of its own.</summary>
    protected virtual string LifecycleGateResource => LocalDbLifecycleGate.Resource;

    public async Task InitializeAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<RepairRequestDbContext>();
        await using (await LocalDbLifecycleGate.EnterAsync(ConnectionString, LifecycleGateResource))
        {
            await FixtureDatabaseDropDiagnostics.RunAsync(() => context.Database.EnsureDeletedAsync(), ConnectionString, GetType().Name);
            await context.Database.MigrateAsync();
        }
    }

    /// <summary>
    /// Stops the fixture's own host FIRST, then drops its database through a standalone context inside the lifecycle gate.
    /// <para>
    /// The host's background services (for example the 200 ms <c>DevelopmentOnlyPendingScanRunner</c> of the scanning fixtures) keep opening connections to
    /// this database for as long as the host runs. Dropping the database while the host was still alive left that activity inside the drop window; the
    /// post-merge failures recorded in <c>docs/18</c> are "database currently in use" errors of exactly those fixtures. Stopping the host first removes the
    /// fixture's own activity from the window (hosted services are stopped and awaited, so nothing of this fixture can reopen a connection afterwards). EF Core's
    /// drop is "ALTER DATABASE … SET SINGLE_USER WITH ROLLBACK IMMEDIATE" followed by "DROP DATABASE", so sessions that already exist cannot block it; only a
    /// connection (re)established after that ALTER can, and with the host stopped no component of this fixture makes one. The drop needs nothing from the
    /// disposed host: the database name is the factory's own <see cref="ApiHostFactory.ConnectionString"/>. Whether this is what blocked the drop in the
    /// historical incidents is NOT proven.
    /// </para>
    /// The gate, the diagnostics and the propagation of a failed drop are unchanged; a failed drop still throws the original exception (the host is already
    /// disposed by then).
    /// </summary>
    async Task IAsyncLifetime.DisposeAsync()
    {
        var connectionString = ConnectionString;

        await DisposeAsync();

        await using (await LocalDbLifecycleGate.EnterAsync(connectionString, LifecycleGateResource))
        {
            await using var cleanup = new RepairRequestDbContext(new DbContextOptionsBuilder<RepairRequestDbContext>().UseSqlServer(connectionString).Options);
            await FixtureDatabaseDropDiagnostics.RunAsync(() => DropDatabaseAsync(cleanup), connectionString, GetType().Name);
        }
    }

    /// <summary>The database drop itself (EF Core's <c>EnsureDeletedAsync</c>); overridable only so a lifecycle test can make it fail with a real SQL error.</summary>
    protected virtual Task DropDatabaseAsync(RepairRequestDbContext cleanup) => cleanup.Database.EnsureDeletedAsync();

    public Task<ApplicationUser> CreateUserAsync(params string[] roleCodes) =>
        CreateUserInTenantAsync(Guid.NewGuid(), roleCodes);

    public async Task<ApplicationUser> CreateUserInTenantAsync(Guid tenantId, params string[] roleCodes)
    {
        await using var scope = Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var email = $"api-{Guid.NewGuid():N}@example.test";
        var user = new ApplicationUser { TenantId = tenantId, Email = email, UserName = email };

        var created = await userManager.CreateAsync(user, ValidPassword);
        Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(error => error.Code)));

        if (roleCodes.Length > 0)
        {
            Assert.True((await userManager.AddToRolesAsync(user, roleCodes)).Succeeded);
        }

        return user;
    }
}
