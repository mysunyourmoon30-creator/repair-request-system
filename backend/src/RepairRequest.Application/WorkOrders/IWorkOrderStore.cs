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
}
