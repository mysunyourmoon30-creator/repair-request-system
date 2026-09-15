using Microsoft.Extensions.Options;
using RepairRequest.Application.Attachments;

namespace RepairRequest.Api.DevelopmentScanning;

/// <summary>
/// Development/Testing-only in-process runner that scans PENDING files with the registered fake scanner
/// (DEC-PRE-S1-007-08). It reuses <see cref="FileScanService"/>, so the S1-006 transition rules, audit and
/// "only while PENDING" guard are unchanged. It is test and development infrastructure only: it does not satisfy or replace
/// the deferred production background scanning Worker (DEC-S1-006-F2), and it refuses to start outside Development/Testing.
/// </summary>
public sealed class DevelopmentOnlyPendingScanRunner : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<DevelopmentMalwareScanningOptions> _options;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<DevelopmentOnlyPendingScanRunner> _logger;

    public DevelopmentOnlyPendingScanRunner(
        IServiceScopeFactory scopeFactory,
        IOptions<DevelopmentMalwareScanningOptions> options,
        IHostEnvironment environment,
        ILogger<DevelopmentOnlyPendingScanRunner> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _environment = environment;
        _logger = logger;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        // Defense in depth: registration already refuses these conditions.
        if (!_options.Value.Enabled || !DevelopmentMalwareScanning.IsAllowedEnvironment(_environment))
        {
            throw new InvalidOperationException(DevelopmentMalwareScanning.RefusalMessage(_environment.EnvironmentName));
        }

        _logger.LogWarning(
            "Development-only FAKE malware scanning is enabled in the {EnvironmentName} environment. It provides no malware protection.",
            _environment.EnvironmentName);

        return base.StartAsync(cancellationToken);
    }

    /// <summary>Scans one bounded batch of PENDING files. Returns the number of files attempted.</summary>
    public async Task<int> ScanPendingBatchAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var pending = await scope.ServiceProvider.GetRequiredService<IPendingFileScanSource>()
            .ListPendingFileIdsAsync(_options.Value.BatchSize, cancellationToken);
        var scanService = scope.ServiceProvider.GetRequiredService<FileScanService>();

        foreach (var fileAssetId in pending)
        {
            await scanService.ScanAsync(fileAssetId, cancellationToken);
        }

        return pending.Count;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ScanPendingBatchAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // For example the database is not migrated yet; files stay PENDING and the next iteration retries.
                _logger.LogWarning(exception, "Development-only scan runner iteration failed; it will retry.");
            }

            try
            {
                await Task.Delay(_options.Value.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
