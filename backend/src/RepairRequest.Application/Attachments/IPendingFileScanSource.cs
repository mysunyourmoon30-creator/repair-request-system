namespace RepairRequest.Application.Attachments;

/// <summary>
/// Source of PENDING files for the Development/Testing-only scan runner (DEC-PRE-S1-007-08). It is test and development
/// infrastructure: it is registered only when that runner is enabled, and it does not implement the deferred production
/// background scanning Worker (DEC-S1-006-F2).
/// </summary>
public interface IPendingFileScanSource
{
    /// <summary>Up to <paramref name="maxCount"/> PENDING file ids, oldest upload first. Never loads file content.</summary>
    Task<IReadOnlyList<Guid>> ListPendingFileIdsAsync(int maxCount, CancellationToken cancellationToken);
}
