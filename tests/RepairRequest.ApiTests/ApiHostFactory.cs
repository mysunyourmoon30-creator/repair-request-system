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

    public async Task InitializeAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<RepairRequestDbContext>();
        await using (await LocalDbLifecycleGate.EnterAsync(ConnectionString))
        {
            await FixtureDatabaseDropDiagnostics.RunAsync(() => context.Database.EnsureDeletedAsync(), ConnectionString, GetType().Name);
            await context.Database.MigrateAsync();
        }
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await using (var scope = Services.CreateAsyncScope())
        {
            await using (await LocalDbLifecycleGate.EnterAsync(ConnectionString))
            {
                var context = scope.ServiceProvider.GetRequiredService<RepairRequestDbContext>();
                await FixtureDatabaseDropDiagnostics.RunAsync(() => context.Database.EnsureDeletedAsync(), ConnectionString, GetType().Name);
            }
        }

        await DisposeAsync();
    }

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
