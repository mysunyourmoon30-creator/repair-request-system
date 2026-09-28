using RepairRequest.Application.Common;
using RepairRequest.Application.Security;
using RepairRequest.Domain.Auditing;
using RepairRequest.Domain.WorkOrders;

namespace RepairRequest.Application.WorkOrders;

/// <summary>
/// Persistence port for Cost Summary Prepare (CST-API-001), Read and Review (CST-API-002; Supervisor only;
/// `docs/13` §4.18/§4.19). Follows the same one-transaction shape as every other command in this codebase, but
/// with a two-phase If-Match target for Prepare (see <see cref="SaveNewAsync"/>/<see cref="SaveUpdateAsync"/>'s
/// own remarks) since Prepare never changes the Work Order's own status; Review always targets the Cost
/// Summary's own RowVersion via <see cref="SaveUpdateAsync"/>, the same as Prepare's own edit path.
/// </summary>
public interface ICostSummaryStore
{
    Task<CommandResult<T>> RunInTransactionAsync<T>(Func<Task<CommandResult<T>>> command, CancellationToken cancellationToken);

    /// <summary>
    /// The Work Order (tracked), within <see cref="IDataScope.WorkOrders"/> site-wide scope — Team Lead for
    /// Prepare, Team Lead or Supervisor for Read, Supervisor for Review; the outer authorization policy gates
    /// which role can reach each action, so this loader is shared unchanged across all three. Null (404) when
    /// nonexistent, outside the caller's tenant, or outside their Site scope.
    /// </summary>
    Task<WorkOrder?> LoadWorkOrderInScopeAsync(CurrentUser user, Guid workOrderId, CancellationToken cancellationToken);

    /// <summary>The Work Order's current Cost Summary (tracked), or null when none has been prepared yet.</summary>
    Task<CostSummary?> GetTrackedCostSummaryAsync(Guid workOrderId, CancellationToken cancellationToken);

    /// <summary>
    /// The Work Order's current Cost Summary, projected read-only (no tracking), scoped to the caller via <see
    /// cref="IDataScope.WorkOrders"/> in the same query (mirrors <c>WorkSummaryStore.GetWorkSummaryAsync</c>'s
    /// combined scope+read shape) — Team Lead or Supervisor, gated by the outer authorization policy. Null when
    /// none exists yet or the caller is out of scope (identical, non-leaking response either way).
    /// </summary>
    Task<CostSummaryDto?> GetCostSummaryDtoAsync(CurrentUser user, Guid workOrderId, CancellationToken cancellationToken);

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
    /// Re-Prepare (edit) an existing, not-yet-reviewed row, or Review of an existing row: <paramref
    /// name="expectedRowVersion"/> is checked as a true compare-and-swap against the Cost Summary's own
    /// RowVersion (CST-011) — the ETag returned by the previous Prepare/read response — since the Work Order's
    /// own RowVersion never advances during either action and so cannot detect a lost update.
    /// </summary>
    Task<WorkOrderSaveOutcome> SaveUpdateAsync(CostSummary costSummary, byte[] expectedRowVersion, CancellationToken cancellationToken);

    /// <summary>
    /// Every Work Order with an unreviewed Cost Summary, within the Supervisor's <see cref="IDataScope.WorkOrders"/>
    /// site-wide scope (`docs/13` §4.19 — a technical addition, not baseline-documented): Status COMPLETED, a
    /// Cost Summary exists, its ReviewedAt is null. Newest-prepared-first, with a stable id tie-breaker for full
    /// determinism, mirroring the same paging shape as every other list in this codebase.
    /// </summary>
    Task<PagedResult<PendingCostSummaryReviewDto>> ListPendingReviewAsync(CurrentUser user, PendingCostSummaryReviewQuery query, CancellationToken cancellationToken);
}
