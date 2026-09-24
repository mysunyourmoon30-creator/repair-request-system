using RepairRequest.Application.Common;
using RepairRequest.Application.Security;
using RepairRequest.Domain.Auditing;
using RepairRequest.Domain.WorkOrders;

namespace RepairRequest.Application.WorkOrders;

/// <summary>
/// Persistence port for Customer Accept (ST-WO-005; UC-WO-021; `docs/13` §4.16). Deliberately separate from
/// <see cref="IWorkSummaryStore"/> — UC-WO-021 is its own baseline use case, distinct from UC-WO-020 (Work
/// Summary Submit/Review, which owns ST-WO-003/004). Follows the same one-transaction, RowVersion-guarded shape
/// as every other Work Order command in this codebase; the Work Order is the resource named in the URL, so it
/// carries the client's If-Match token.
/// </summary>
public interface IWorkOrderAcceptanceStore
{
    Task<CommandResult<T>> RunInTransactionAsync<T>(Func<Task<CommandResult<T>>> command, CancellationToken cancellationToken);

    /// <summary>
    /// The Work Order for Accept (tracked); null (404) unless ALL of: it exists in the caller's tenant, the
    /// caller is exactly <see cref="WorkOrder.AcceptanceContactId"/> (a resource-specific identity check, not a
    /// role-scoped query — `docs/13` §4.16 Decision), and the caller currently holds Site scope on the Work
    /// Order's Site (defense-in-depth re-check, the same convention every Technician-scoped query in this
    /// codebase already uses — a revoked Site scope hides the Work Order even from the exact designated contact).
    /// </summary>
    Task<WorkOrder?> LoadForAcceptAsync(CurrentUser user, Guid workOrderId, CancellationToken cancellationToken);

    void AddAudit(AuditHistory audit);

    /// <summary>Saves the Work Order, guarded by <paramref name="expectedRowVersion"/> (the client's If-Match, the Work Order's own token).</summary>
    Task<WorkOrderSaveOutcome> SaveChangesAsync(WorkOrder workOrder, byte[] expectedRowVersion, CancellationToken cancellationToken);
}
