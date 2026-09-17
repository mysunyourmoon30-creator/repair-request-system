using RepairRequest.Application.Common;
using RepairRequest.Application.Security;
using RepairRequest.Domain.Auditing;
using RepairRequest.Domain.Files;
using RepairRequest.Domain.RepairRequests;

namespace RepairRequest.Application.Attachments;

/// <summary>
/// Private binary storage (RR-ARCH-001 section 11; RR-DD-001 FAS-007). Keys are opaque, server-generated and never
/// derived from client input; implementations must not expose public URLs.
/// </summary>
public interface IFileStorage
{
    /// <summary>Stores <paramref name="content"/> under a new key. Never overwrites; a failed save leaves nothing behind.</summary>
    Task SaveAsync(string storageKey, Stream content, CancellationToken cancellationToken);

    Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken);

    /// <summary>Best-effort removal used for compensation. Never throws; failures are logged by the adapter.</summary>
    Task DeleteAsync(string storageKey, CancellationToken cancellationToken);
}

public enum MalwareScanOutcome
{
    Clean,
    Infected,

    /// <summary>The scan could not be completed (no provider configured, provider outage or error). The file stays PENDING.</summary>
    Unavailable
}

/// <summary>
/// Pluggable malware scanning provider (DEC-PS1-005). Production providers replace the default without changing
/// business logic; the default reports <see cref="MalwareScanOutcome.Unavailable"/> and never claims protection.
/// </summary>
public interface IFileMalwareScanner
{
    Task<MalwareScanOutcome> ScanAsync(Stream content, CancellationToken cancellationToken);
}

/// <summary>Scoped lookup data for an authorized download.</summary>
public sealed record AttachmentDownloadInfo(
    Guid RepairRequestId,
    Guid TenantId,
    Guid FileAssetId,
    string FileName,
    string MimeType,
    long SizeBytes,
    string StorageReference,
    MalwareScanStatus MalwareScanStatus);

/// <summary>
/// Persistence port for attachment use cases. Caller-facing lookups are restricted by the Repair Request business scope
/// (<see cref="IDataScope.RepairRequests"/>), so out-of-scope and nonexistent ids are indistinguishable.
/// </summary>
public interface IAttachmentStore
{
    /// <summary>Status of a Repair Request the caller owns and can see; null otherwise.</summary>
    Task<RepairRequestStatus?> GetOwnRequestStatusAsync(CurrentUser user, Guid repairRequestId, CancellationToken cancellationToken);

    /// <summary>File attached to a Repair Request within the caller's scope; null otherwise. One query.</summary>
    Task<AttachmentDownloadInfo?> FindDownloadAsync(CurrentUser user, Guid fileAssetId, CancellationToken cancellationToken);

    /// <summary>
    /// One bounded page of attachment metadata for a Repair Request within the caller's scope, in upload order; null when the
    /// Repair Request is not in scope. Paging executes in SQL and no file content is read.
    /// </summary>
    Task<PagedResult<AttachmentDto>?> ListAsync(CurrentUser user, Guid repairRequestId, PageRequest paging, CancellationToken cancellationToken);

    /// <summary>Adds the file metadata; its identifier is assigned when added.</summary>
    void AddFileAsset(FileAsset fileAsset);

    void AddAttachment(RepairRequestAttachment attachment);

    void AddAudit(AuditHistory audit);

    /// <summary>Saves pending changes in one database transaction.</summary>
    Task SaveChangesAsync(CancellationToken cancellationToken);

    /// <summary>Untracked PENDING file for scanning; null when missing or already scanned.</summary>
    Task<FileAsset?> FindPendingFileAsync(Guid fileAssetId, CancellationToken cancellationToken);

    /// <summary>
    /// Persists the scan result only while the file is still PENDING, together with its audit record, in one transaction.
    /// Returns false when another scan already completed the file.
    /// </summary>
    Task<bool> TryCompleteScanAsync(FileAsset scannedFile, AuditHistory audit, CancellationToken cancellationToken);
}
