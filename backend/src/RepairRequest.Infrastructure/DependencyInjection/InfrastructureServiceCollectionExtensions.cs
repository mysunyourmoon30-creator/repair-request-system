using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using RepairRequest.Application.Authentication;
using RepairRequest.Application.Security;
using RepairRequest.Infrastructure.Authentication;
using RepairRequest.Infrastructure.Authorization;
using RepairRequest.Infrastructure.Identity;
using RepairRequest.Infrastructure.Persistence;

namespace RepairRequest.Infrastructure.DependencyInjection;

/// <summary>
/// Composition root entry point for the Infrastructure layer (RR-ARCH-001 section 5).
/// Wires EF Core / SQL Server and the ASP.NET Core Identity + token adapters (DEC-PS1-004).
/// Storage and notification-outbox adapters are added in later sprints.
/// </summary>
public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<RepairRequestDbContext>(options =>
        {
            // Read when the context is first created so host-level overrides (environment
            // variables, user-secrets, test hosts) are always honoured.
            var connectionString = configuration.GetConnectionString("DefaultConnection");
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException(
                    "ConnectionStrings:DefaultConnection is not configured. " +
                    "Set it via appsettings.{Environment}.json (gitignored), user-secrets, or an environment variable.");
            }

            options.UseSqlServer(connectionString);
        });

        AddAuthentication(services, configuration);

        // S1-003: caller resolution and tenant/site data scope.
        services.AddScoped<ICurrentUserStore, CurrentUserStore>();
        services.AddScoped<IDataScope, DataScope>();

        return services;
    }

    private static void AddAuthentication(IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);

        services.AddIdentityCore<ApplicationUser>(IdentityPolicy.Configure)
            .AddRoles<ApplicationRole>()
            .AddEntityFrameworkStores<RepairRequestDbContext>();

        services.AddOptions<JwtOptions>()
            .Bind(configuration.GetSection(JwtOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<JwtOptions>, JwtOptionsValidator>();

        services.AddOptions<RefreshTokenOptions>()
            .Bind(configuration.GetSection(RefreshTokenOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<RefreshTokenOptions>, RefreshTokenOptionsValidator>();

        services.AddSingleton<JwtAccessTokenIssuer>();
        services.AddSingleton<DummyPasswordHash>();
        services.AddScoped<IAuthenticationService, AuthenticationService>();
    }
}
