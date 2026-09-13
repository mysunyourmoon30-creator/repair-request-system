using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using RepairRequest.Infrastructure.Authentication;
using RepairRequest.Infrastructure.DependencyInjection;
using RepairRequest.Infrastructure.Identity;
using RepairRequest.IntegrationTests.Persistence;

namespace RepairRequest.IntegrationTests.Authentication;

/// <summary>
/// Builds the real Infrastructure composition (Identity, token services, EF Core) against the
/// persistence test database with a controllable clock, a per-run random signing key and
/// captured log output. No credentials or keys are stored in source.
/// </summary>
internal sealed class AuthenticationTestHost : IAsyncDisposable
{
    public const string ValidPassword = "Correct-Horse-42!";
    public const string Issuer = "RepairRequest.IntegrationTests";
    public const string Audience = "RepairRequest.IntegrationTests.Web";

    private readonly ServiceProvider _provider;

    public AuthenticationTestHost()
    {
        SigningKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        Clock = new TestClock(DateTimeOffset.UtcNow);
        _provider = BuildProvider(CreateSettings(SigningKey), Clock, Logs);
    }

    public string SigningKey { get; }

    public TestClock Clock { get; }

    public CapturingLoggerProvider Logs { get; } = new();

    public TokenValidationParameters ValidationParameters =>
        JwtTokenValidation.CreateParameters(_provider.GetRequiredService<IOptions<JwtOptions>>().Value);

    public static Dictionary<string, string?> CreateSettings(string? signingKey) => new()
    {
        ["ConnectionStrings:DefaultConnection"] = PersistenceDatabaseFixture.ConnectionString,
        ["Authentication:Jwt:Issuer"] = Issuer,
        ["Authentication:Jwt:Audience"] = Audience,
        ["Authentication:Jwt:AccessTokenLifetime"] = "00:15:00",
        ["Authentication:Jwt:SigningKey"] = signingKey,
        ["Authentication:RefreshToken:Lifetime"] = "7.00:00:00"
    };

    public static ServiceProvider BuildProvider(
        IDictionary<string, string?> settings,
        TimeProvider? clock = null,
        ILoggerProvider? logs = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();

        if (clock is not null)
        {
            services.AddSingleton(clock);
        }

        services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Trace);
            if (logs is not null)
            {
                logging.AddProvider(logs);
            }
        });

        services.AddInfrastructure(configuration);

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    public AsyncServiceScope CreateScope() => _provider.CreateAsyncScope();

    public Task<ApplicationUser> CreateUserAsync(params string[] roleCodes) =>
        CreateUserInTenantAsync(Guid.NewGuid(), roleCodes);

    public async Task<ApplicationUser> CreateUserInTenantAsync(Guid tenantId, params string[] roleCodes)
    {
        await using var scope = CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var email = $"user-{Guid.NewGuid():N}@example.test";
        var user = new ApplicationUser { TenantId = tenantId, Email = email, UserName = email };

        var created = await userManager.CreateAsync(user, ValidPassword);
        Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(error => error.Code)));

        if (roleCodes.Length > 0)
        {
            var added = await userManager.AddToRolesAsync(user, roleCodes);
            Assert.True(added.Succeeded, string.Join("; ", added.Errors.Select(error => error.Code)));
        }

        return user;
    }

    public ValueTask DisposeAsync() => _provider.DisposeAsync();
}

internal sealed class TestClock(DateTimeOffset utcNow) : TimeProvider
{
    public DateTimeOffset UtcNow { get; private set; } = utcNow;

    public override DateTimeOffset GetUtcNow() => UtcNow;

    public void Advance(TimeSpan duration) => UtcNow += duration;
}

/// <summary>Records every log message and its structured values so tests can prove secrets never reach logs.</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _entries = new();

    public IReadOnlyCollection<string> Entries => _entries;

    public void Clear() => _entries.Clear();

    /// <summary>Number of SQL commands EF Core executed since the last <see cref="Clear"/>.</summary>
    public int ExecutedDbCommandCount => _entries.Count(entry => entry.StartsWith("Executed DbCommand", StringComparison.Ordinal));

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(_entries);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(ConcurrentQueue<string> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var values = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? string.Join("|", pairs.Select(pair => $"{pair.Key}={pair.Value}"))
                : string.Empty;

            entries.Enqueue($"{formatter(state, exception)} {values} {exception}");
        }
    }
}
