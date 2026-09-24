using RepairRequest.Application.Common;
using RepairRequest.Application.Security;
using RepairRequest.Application.WorkOrders;

namespace RepairRequest.Application.Tests.WorkOrders;

internal sealed class FakeWorkOrderStore : IWorkOrderStore
{
    public CurrentUser? LastListUser { get; private set; }

    public WorkOrderListQuery? LastListQuery { get; private set; }

    public PagedResult<WorkOrderDto> ListResult { get; set; } = new([], 1, 20, 0);

    public CurrentUser? LastGetUser { get; private set; }

    public Guid? LastGetId { get; private set; }

    public WorkOrderDto? GetResult { get; set; }

    public Task<PagedResult<WorkOrderDto>> ListAsync(CurrentUser user, WorkOrderListQuery query, CancellationToken cancellationToken)
    {
        LastListUser = user;
        LastListQuery = query;
        return Task.FromResult(ListResult);
    }

    public Task<WorkOrderDto?> GetAsync(CurrentUser user, Guid workOrderId, CancellationToken cancellationToken)
    {
        LastGetUser = user;
        LastGetId = workOrderId;
        return Task.FromResult(GetResult);
    }

    public Task<WorkOrderDto?> GetForTechnicianAsync(CurrentUser user, Guid workOrderId, CancellationToken cancellationToken)
    {
        LastGetUser = user;
        LastGetId = workOrderId;
        return Task.FromResult(GetResult);
    }

    public Task<WorkOrderDto?> GetForAcceptanceContactAsync(CurrentUser user, Guid workOrderId, CancellationToken cancellationToken)
    {
        LastGetUser = user;
        LastGetId = workOrderId;
        return Task.FromResult(GetResult);
    }
}
