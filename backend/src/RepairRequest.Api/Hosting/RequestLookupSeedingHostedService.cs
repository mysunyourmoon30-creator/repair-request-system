using RepairRequest.Infrastructure.RepairRequests;

namespace RepairRequest.Api.Hosting;

/// <summary>
/// Seeds missing Category/Priority lookups for every tenant with users when the API starts (DEC-PRE-S1-007-03). Seeding is
/// idempotent and set-based. A failure (for example the database is not migrated or unreachable yet) is logged and never
/// stops the host; affected tenants simply have no lookups until the next successful run.
/// </summary>
internal sealed class RequestLookupSeedingHostedService : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RequestLookupSeedingHostedService> _logger;

    public RequestLookupSeedingHostedService(IServiceScopeFactory scopeFactory, ILogger<RequestLookupSeedingHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var inserted = await scope.ServiceProvider.GetRequiredService<RequestLookupSeeder>().SeedAllTenantsAsync(cancellationToken);
            _logger.LogInformation("Request lookup seeding completed. Inserted rows: {InsertedRows}", inserted);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "Request lookup seeding failed at startup; the API continues without it.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
