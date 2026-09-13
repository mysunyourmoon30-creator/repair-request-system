using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RepairRequest.Infrastructure.Persistence;

namespace RepairRequest.Infrastructure.DependencyInjection;

/// <summary>
/// Composition root entry point for the Infrastructure layer (RR-ARCH-001 section 5).
/// Wires EF Core / SQL Server. Storage, notification-outbox and identity adapters
/// are added here in later sprints once their ports are approved in the Application layer.
/// </summary>
public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException(
                "ConnectionStrings:DefaultConnection is not configured. " +
                "Set it via appsettings.{Environment}.json (gitignored), user-secrets, or an environment variable.");

        services.AddDbContext<RepairRequestDbContext>(options =>
            options.UseSqlServer(connectionString));

        return services;
    }
}
