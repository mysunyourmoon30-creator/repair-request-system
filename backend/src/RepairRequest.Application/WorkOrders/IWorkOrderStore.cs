using RepairRequest.Application.Common;
using RepairRequest.Application.Security;

namespace RepairRequest.Application.WorkOrders;

/// <summary>
/// Focused read persistence port for the Work Order List/Detail use cases (S2-001). Every lookup is restricted
/// by <see cref="IDataScope.WorkOrders"/>, so out-of-scope and nonexistent ids are indistinguishable.
/// </summary>
public interface IWorkOrderStore
{
    Task<PagedResult<WorkOrderDto>> ListAsync(CurrentUser user, WorkOrderListQuery query, CancellationToken cancellationToken);

    Task<WorkOrderDto?> GetAsync(CurrentUser user, Guid workOrderId, CancellationToken cancellationToken);

    /// <summary>
    /// The same projection as <see cref="GetAsync"/>, but scoped for a Technician caller after Check-in
    /// (S3-001): <see cref="IDataScope.WorkOrders"/> deliberately excludes TECHNICIAN (DEC-S2-001-03), so the
    /// Coordinator-oriented read cannot be reused here. Scope is instead: the Work Order has at least one
    /// Service Visit assigned to the caller — correct immediately after a successful Check-in, since that
    /// action itself only ever succeeds on a Visit assigned to the caller. The returned <c>Visits</c> are
    /// restricted to the caller's own assigned Visits.
    /// </summary>
    Task<WorkOrderDto?> GetForTechnicianAsync(CurrentUser user, Guid workOrderId, CancellationToken cancellationToken);
}
