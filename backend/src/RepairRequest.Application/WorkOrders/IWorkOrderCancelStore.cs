using RepairRequest.Application.Common;
using RepairRequest.Application.Security;
using RepairRequest.Domain.Auditing;
using RepairRequest.Domain.WorkOrders;

namespace RepairRequest.Application.WorkOrders;

/// <summary>
/// Persistence port for Work Order Cancel (ST-WO-011; WO-API-011; UC-WO-026; BR-09/BR-12; `docs/13` §4.25).
/// One transaction, RowVersion-guarded on the Work Order's own token (Cancel changes its status).
/// </summary>
public interface IWorkOrderCancelStore
{
    Task<CommandResult<T>> RunInTransactionAsync<T>(Func<Task<CommandResult<T>>> command, CancellationToken cancellationToken);

    /// <summary>The Work Order for Cancel (tracked), or null (404) when it is outside the caller's tenant/Site scope — <see cref="IDataScope.WorkOrders"/>, unchanged.</summary>
    Task<WorkOrder?> LoadInScopeAsync(CurrentUser user, Guid workOrderId, CancellationToken cancellationToken);

    /// <summary>
    /// Every Service Visit of this Work Order (tracked), any status. Used to deny Cancel (409) when any Visit is
    /// currently IN_PROGRESS (`docs/13` §4.25 Decision — Cancel must not force-terminate an active Work Session),
    /// and, on success, to cascade-cancel every Visit still SCHEDULED. COMPLETED/MISSED/CANCELLED Visits are read
    /// but never mutated — their history is preserved unchanged.
    /// </summary>
    Task<IReadOnlyList<ServiceVisit>> GetServiceVisitsAsync(Guid workOrderId, CancellationToken cancellationToken);

    void AddAudit(AuditHistory audit);

    /// <summary>Saves the Work Order and any cascaded Service Visit changes, guarded by <paramref name="expectedRowVersion"/> (the client's If-Match, the Work Order's own token).</summary>
    Task<WorkOrderSaveOutcome> SaveChangesAsync(WorkOrder workOrder, byte[] expectedRowVersion, CancellationToken cancellationToken);
}
