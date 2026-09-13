using Microsoft.Extensions.DependencyInjection;

namespace RepairRequest.Application.DependencyInjection;

/// <summary>
/// Composition root entry point for the Application layer.
/// Sprint 0: no use-case handlers exist yet; business command/query
/// registrations are added here as they are approved and implemented.
/// </summary>
public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        return services;
    }
}
