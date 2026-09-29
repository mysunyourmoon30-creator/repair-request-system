using RepairRequest.Application.Common;
using RepairRequest.Application.Security;
using RepairRequest.Domain.Auditing;
using RepairRequest.Domain.WorkOrders;

namespace RepairRequest.Application.WorkOrders;

/// <summary>
/// The facts about a Work Order that Close's guard (BR-08; `docs/13` §4.20 Q2) reads, gathered from existing data
/// only — no review flag exists for the Work Summary, so nothing here is a new column.
/// </summary>
/// <param name="HasWorkSummary">A <c>work_summary</c> row exists for the Work Order (any revision; only revision 1 is created by the current implementation).</param>
/// <param name="LatestAcceptanceDecision">
/// The decision of the <c>customer_acceptance</c> row with the highest round number, or null when there is none.
/// Deliberately the latest round, never "any ACCEPT row in history".
/// </param>
/// <param name="CostSummaryId">The Cost Summary's id, or null when none has been prepared.</param>
/// <param name="CostSummaryReviewedAt">The Cost Summary's <c>reviewed_at</c>; null means prepared-but-unreviewed (or no Cost Summary).</param>
public sealed record WorkOrderCloseEvidence(
    bool HasWorkSummary,
    AcceptanceDecision? LatestAcceptanceDecision,
    Guid? CostSummaryId,
    DateTime? CostSummaryReviewedAt);

/// <summary>
/// Persistence port for Work Order Close (ST-WO-006; WO-API-010; BR-08; `docs/13` §4.20). One transaction,
/// RowVersion-guarded on the Work Order's own token (Close changes the Work Order's status, so unlike Cost
/// Summary Prepare/Review its own RowVersion is the correct compare-and-swap target).
/// </summary>
public interface IWorkOrderCloseStore
{
    Task<CommandResult<T>> RunInTransactionAsync<T>(Func<Task<CommandResult<T>>> command, CancellationToken cancellationToken);

    /// <summary>The Work Order for Close (tracked), or null (404) when it is outside the caller's tenant/Site scope — <see cref="IDataScope.WorkOrders"/>, unchanged.</summary>
    Task<WorkOrder?> LoadInScopeAsync(CurrentUser user, Guid workOrderId, CancellationToken cancellationToken);

    /// <summary>Reads the Close guard's evidence for one Work Order (no tracking).</summary>
    Task<WorkOrderCloseEvidence> GetCloseEvidenceAsync(Guid workOrderId, CancellationToken cancellationToken);

    void AddAudit(AuditHistory audit);

    /// <summary>Saves the Work Order, guarded by <paramref name="expectedRowVersion"/> (the client's If-Match, the Work Order's own token).</summary>
    Task<WorkOrderSaveOutcome> SaveChangesAsync(WorkOrder workOrder, byte[] expectedRowVersion, CancellationToken cancellationToken);
}
