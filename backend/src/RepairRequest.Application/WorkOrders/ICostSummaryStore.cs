using RepairRequest.Application.Common;
using RepairRequest.Application.Security;
using RepairRequest.Domain.Auditing;
using RepairRequest.Domain.WorkOrders;

namespace RepairRequest.Application.WorkOrders;

/// <summary>
/// Persistence port for Cost Summary Prepare (CST-API-001; Team Lead only; `docs/13` §4.18). Follows the same
/// one-transaction shape as every other command in this codebase, but with a two-phase If-Match target (see
/// <see cref="SaveNewAsync"/>/<see cref="SaveUpdateAsync"/>'s own remarks) since Prepare, unlike every prior
/// command, never changes the Work Order's own status — so the Work Order's RowVersion cannot serve as a
/// meaningful concurrency guard once a Cost Summary row already exists and is being edited again.
/// </summary>
public interface ICostSummaryStore
{
    Task<CommandResult<T>> RunInTransactionAsync<T>(Func<Task<CommandResult<T>>> command, CancellationToken cancellationToken);

    /// <summary>
    /// The Work Order for Prepare (tracked), within <see cref="IDataScope.WorkOrders"/> site-wide scope (Team
    /// Lead) — the same scope model Work Summary review already uses (`docs/13` §4.15 Decision 5). Null (404)
    /// when nonexistent, outside the caller's tenant, or outside their Site scope.
    /// </summary>
    Task<WorkOrder?> LoadWorkOrderForPrepareAsync(CurrentUser user, Guid workOrderId, CancellationToken cancellationToken);

    /// <summary>The Work Order's current Cost Summary (tracked), or null when none has been prepared yet. Not scope-checked itself — the caller already holds a scoped Work Order.</summary>
    Task<CostSummary?> GetTrackedCostSummaryAsync(Guid workOrderId, CancellationToken cancellationToken);

    void Add(CostSummary costSummary);

    void AddAudit(AuditHistory audit);

    /// <summary>
    /// First-time Prepare (no existing Cost Summary row): <paramref name="expectedRowVersion"/> is checked as a
    /// true compare-and-swap against the Work Order's own RowVersion — the only ETag the caller could have
    /// obtained (from GET /work-orders/{id}), and the UNIQUE(work_order_id) index is the DB-level backstop
    /// against two concurrent first-time Prepare calls racing to create the same row.
    /// </summary>
    Task<WorkOrderSaveOutcome> SaveNewAsync(WorkOrder workOrder, byte[] expectedRowVersion, CancellationToken cancellationToken);

    /// <summary>
    /// Re-Prepare (edit) an existing, not-yet-reviewed row: <paramref name="expectedRowVersion"/> is checked as a
    /// true compare-and-swap against the Cost Summary's own RowVersion (CST-011) — the ETag returned by the
    /// previous successful Prepare response — since the Work Order's own RowVersion never advances during
    /// Prepare and so cannot detect a lost update across repeated edits.
    /// </summary>
    Task<WorkOrderSaveOutcome> SaveUpdateAsync(CostSummary costSummary, byte[] expectedRowVersion, CancellationToken cancellationToken);
}
