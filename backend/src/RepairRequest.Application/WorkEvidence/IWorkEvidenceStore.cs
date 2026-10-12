using RepairRequest.Application.Common;
using RepairRequest.Application.Security;
using RepairRequest.Domain.Auditing;
using RepairRequest.Domain.Files;
using RepairRequest.Domain.WorkEvidence;

namespace RepairRequest.Application.WorkEvidence;

/// <summary>
/// Persistence port of Work Order evidence (`docs/13` §4.28). It shares the unit of work and the transaction of the command that calls
/// it: the upload command's own transaction, and the Work Summary Submit transaction for locking and binding. Caller-facing reads
/// derive the caller's <see cref="ReportAudience"/> on the server and never return evidence of a Work Order the caller cannot see.
/// </summary>
public interface IWorkEvidenceStore
{
    Task<CommandResult<T>> RunInTransactionAsync<T>(Func<Task<CommandResult<T>>> command, CancellationToken cancellationToken);

    /// <summary>
    /// The upload target of a Technician: the Work Order in the caller's tenant on which the caller has at least one own Work Session,
    /// with the Visit of their latest session. Null (404) otherwise — also for every non-Technician.
    /// </summary>
    Task<WorkEvidenceUploadTarget?> FindUploadTargetAsync(CurrentUser user, Guid workOrderId, CancellationToken cancellationToken);

    /// <summary>
    /// Takes the exclusive, transaction-owned evidence lock of one Work Order inside the caller's open transaction. Upload and Work
    /// Summary Submit both take it, so they are serialized per Work Order. False means the bounded wait elapsed or the session was a
    /// deadlock victim; the caller then ends the command as a concurrency conflict without writing anything.
    /// </summary>
    Task<bool> TryLockWorkOrderAsync(Guid workOrderId, CancellationToken cancellationToken);

    /// <summary>
    /// Resolves client-supplied evidence ids for one Submit: only uploads that belong to exactly this tenant, Work Order and Visit are
    /// returned, each with its file's persisted scan status and whether it is already bound. An id that is unknown or lives anywhere else is
    /// simply absent, so the caller cannot tell the difference. At most ten ids are ever passed.
    /// </summary>
    Task<IReadOnlyList<WorkEvidenceSelectionRow>> LoadSelectionAsync(
        Guid tenantId, Guid workOrderId, Guid serviceVisitId, IReadOnlyCollection<Guid> evidenceIds, CancellationToken cancellationToken);

    void AddFileAsset(FileAsset fileAsset);

    void AddUpload(WorkEvidenceUpload upload);

    void AddBinding(WorkSummaryEvidence binding);

    void AddAudit(AuditHistory audit);

    Task SaveChangesAsync(CancellationToken cancellationToken);

    Task<ReportAudience> ResolveAudienceAsync(CurrentUser user, Guid workOrderId, CancellationToken cancellationToken);

    /// <summary>
    /// Evidence metadata visible to the caller, oldest first, with its audience; null when the caller has no access to the Work Order.
    /// A Technician sees the uploads of their own Visits (prepared and bound), a Team Lead or Supervisor those of the Work Order, and
    /// the Acceptance Contact only items already bound to a submitted Work Summary and CLEAN.
    /// </summary>
    Task<(ReportAudience Audience, IReadOnlyList<WorkEvidenceItemDto> Items)?> ListAsync(CurrentUser user, Guid workOrderId, CancellationToken cancellationToken);

    /// <summary>One evidence item for download within the caller's scope; null when missing, out of scope or not visible to the caller.</summary>
    Task<WorkEvidenceDownloadInfo?> FindDownloadAsync(CurrentUser user, Guid workOrderId, Guid evidenceId, CancellationToken cancellationToken);
}
