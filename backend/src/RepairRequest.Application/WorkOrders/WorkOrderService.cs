using RepairRequest.Application.Common;
using RepairRequest.Application.Security;

namespace RepairRequest.Application.WorkOrders;

/// <summary>
/// S2-001 Work Order List/Detail use cases. Role capability (WorkOrder.Read) is enforced by the API policy
/// before these run; every lookup here is tenant/site scoped through <see cref="IWorkOrderStore"/>.
/// </summary>
public sealed class WorkOrderService
{
    private readonly IWorkOrderStore _store;

    public WorkOrderService(IWorkOrderStore store)
    {
        _store = store;
    }

    public Task<PagedResult<WorkOrderDto>> ListAsync(CurrentUser user, WorkOrderListQuery query, CancellationToken cancellationToken) =>
        _store.ListAsync(user, query, cancellationToken);

    public Task<WorkOrderDto?> GetAsync(CurrentUser user, Guid workOrderId, CancellationToken cancellationToken) =>
        _store.GetAsync(user, workOrderId, cancellationToken);

    public Task<WorkOrderDto?> GetForTechnicianAsync(CurrentUser user, Guid workOrderId, CancellationToken cancellationToken) =>
        _store.GetForTechnicianAsync(user, workOrderId, cancellationToken);
}
