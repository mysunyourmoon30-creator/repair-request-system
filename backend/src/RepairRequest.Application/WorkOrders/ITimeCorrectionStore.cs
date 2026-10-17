using RepairRequest.Application.Common;
using RepairRequest.Application.Security;
using RepairRequest.Domain.Auditing;
using RepairRequest.Domain.WorkOrders;

namespace RepairRequest.Application.WorkOrders;

/// <summary>
/// Persistence port of Time Correction (ST-TC-001..003; UC-TIME-001; `docs/17`). Every method shares the transaction of the
/// command that calls it. The loading methods take the locks the command needs and keep them until the transaction ends:
/// the Work Order row first (compatible with Close/Cancel, which update that row), then — for Approve — the Work Session, its
/// pause rows and its Visit. Always the same order, so two Approves, or an Approve and a Pause/Resume/Check-out, serialize
/// instead of deadlocking.
/// </summary>
public interface ITimeCorrectionStore
{
    Task<CommandResult<T>> RunInTransactionAsync<T>(Func<Task<CommandResult<T>>> command, CancellationToken cancellationToken);

    /// <summary>
    /// The session a Request targets, restricted by <see cref="IDataScope.WorkSessionsForTimeCorrectionRequest"/>; null when out of
    /// scope. Locks the Work Order row and then reads the Work Order status, the session and its pause rows fresh.
    /// </summary>
    Task<TimeCorrectionRequestContext?> LoadForRequestAsync(CurrentUser user, Guid workSessionId, CancellationToken cancellationToken);

    void Add(TimeCorrection correction);

    void AddAudit(AuditHistory audit);

    /// <summary>Inserts the new Request and its audit row; a second PENDING request for the same target is reported, not thrown.</summary>
    Task<TimeCorrectionInsertOutcome> SaveNewAsync(CancellationToken cancellationToken);

    /// <summary>The Correction (tracked) restricted by <see cref="IDataScope.TimeCorrectionsForDecision"/>; null when out of scope. Reject takes no Work Order lock: it changes no time fact.</summary>
    Task<TimeCorrection?> LoadForRejectAsync(CurrentUser user, Guid timeCorrectionId, CancellationToken cancellationToken);

    /// <summary>
    /// The Correction and everything Approve applies it to, locked and read fresh: Work Order row, Work Session row, its pause rows
    /// and its Visit row (all held until the transaction ends). Null when out of scope.
    /// </summary>
    Task<TimeCorrectionApprovalContext?> LoadForApproveAsync(CurrentUser user, Guid timeCorrectionId, CancellationToken cancellationToken);

    /// <summary>Saves a Reject: the Correction only, guarded by <paramref name="expectedRowVersion"/> (the client's If-Match).</summary>
    Task<WorkOrderSaveOutcome> SaveRejectionAsync(TimeCorrection correction, byte[] expectedRowVersion, CancellationToken cancellationToken);

    /// <summary>
    /// Saves an Approve: the Correction (guarded by <paramref name="expectedRowVersion"/>), the Work Session — always touched so its
    /// row version changes and stale Pause/Resume/Check-out tokens conflict — and the changed pause row and Visit.
    /// </summary>
    Task<WorkOrderSaveOutcome> SaveApprovalAsync(
        TimeCorrection correction, WorkSession session, byte[] expectedRowVersion, CancellationToken cancellationToken);

    Task<TimeCorrectionDto?> GetAsync(CurrentUser user, Guid timeCorrectionId, CancellationToken cancellationToken);

    /// <summary>
    /// TIME-API-ADD-002: the sessions of one Work Order inside <see cref="IDataScope.WorkSessionsForTimeCorrectionRequest"/> with their pauses and the
    /// Work Order's status; null when no session of that Work Order is in the caller's request scope. Read-only, no locks.
    /// </summary>
    Task<TimeCorrectionRequestTargetsDto?> GetRequestTargetsAsync(CurrentUser user, Guid workOrderId, CancellationToken cancellationToken);

    /// <summary>The caller's Site-scoped PENDING requests (Supervisor review), oldest first.</summary>
    Task<PagedResult<TimeCorrectionDto>> ListPendingAsync(CurrentUser user, PendingTimeCorrectionsQuery query, CancellationToken cancellationToken);
}
