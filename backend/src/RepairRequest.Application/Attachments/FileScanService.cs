namespace RepairRequest.Application.Attachments;

/// <summary>
/// Applies a malware scan to one PENDING file (DEC-PS1-005; RR-ARCH-001 section 11 and failure table; decision F2).
/// Clean -> CLEAN and Infected -> FAILED, each audited and persisted only while the file is still PENDING. An unavailable
/// or failing scanner leaves the file PENDING (unusable) for a later retry. No production background runner exists yet
/// (decision F2 covers the S1-006 production/background scope); a Worker will invoke this service when the production
/// scanning provider and runner are introduced. In Development/Testing only, the opt-in fake scanning runner of
/// DEC-PRE-S1-007-08 invokes it as test infrastructure; that runner does not replace the deferred production Worker.
/// </summary>
public sealed class FileScanService
{
    private readonly IAttachmentStore _store;
    private readonly IFileStorage _storage;
    private readonly IFileMalwareScanner _scanner;
    private readonly TimeProvider _clock;

    public FileScanService(IAttachmentStore store, IFileStorage storage, IFileMalwareScanner scanner, TimeProvider clock)
    {
        _store = store;
        _storage = storage;
        _scanner = scanner;
        _clock = clock;
    }

    public async Task<FileScanResult> ScanAsync(Guid fileAssetId, CancellationToken cancellationToken)
    {
        var file = await _store.FindPendingFileAsync(fileAssetId, cancellationToken);
        if (file is null)
        {
            return FileScanResult.NotPending;
        }

        MalwareScanOutcome outcome;
        try
        {
            await using var content = await _storage.OpenReadAsync(file.StorageReference, cancellationToken);
            outcome = await _scanner.ScanAsync(content, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Scanner or storage failure: fail closed for use (still PENDING), retry later.
            return FileScanResult.StillPending;
        }

        if (outcome == MalwareScanOutcome.Unavailable)
        {
            return FileScanResult.StillPending;
        }

        if (outcome == MalwareScanOutcome.Clean)
        {
            file.MarkClean();
        }
        else
        {
            file.MarkFailed();
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        now = now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMillisecond));
        var completed = await _store.TryCompleteScanAsync(file, AttachmentAudit.ScanCompleted(file, now, Guid.NewGuid()), cancellationToken);

        if (!completed)
        {
            return FileScanResult.NotPending;
        }

        return outcome == MalwareScanOutcome.Clean ? FileScanResult.Clean : FileScanResult.Failed;
    }
}
