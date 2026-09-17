using RepairRequest.Application.Common;
using RepairRequest.Application.Security;
using RepairRequest.Application.WorkOrders;
using RepairRequest.Domain.Security;
using RepairRequest.Domain.WorkOrders;

namespace RepairRequest.Application.Tests.WorkOrders;

/// <summary>S2-001: WorkOrderService is a thin, scope-preserving pass-through to IWorkOrderStore.</summary>
public class WorkOrderServiceTests
{
    private readonly FakeWorkOrderStore _store = new();
    private readonly WorkOrderService _service;
    private readonly CurrentUser _user = new(Guid.NewGuid(), Guid.NewGuid(), [RoleCodes.Coordinator]);

    public WorkOrderServiceTests()
    {
        _service = new WorkOrderService(_store);
    }

    [Fact]
    public async Task ListAsync_PassesTheCallerAndQueryThrough_Unchanged()
    {
        var query = new WorkOrderListQuery(new PageRequest(2, 10), WorkOrderStatus.Open);
        var expected = new PagedResult<WorkOrderDto>([], 2, 10, 0);
        _store.ListResult = expected;

        var result = await _service.ListAsync(_user, query, CancellationToken.None);

        Assert.Same(_user, _store.LastListUser);
        Assert.Same(query, _store.LastListQuery);
        Assert.Same(expected, result);
    }

    [Fact]
    public async Task GetAsync_PassesTheCallerAndIdThrough_Unchanged()
    {
        var workOrderId = Guid.NewGuid();
        var expected = new WorkOrderDto(workOrderId, "WO-1", WorkOrderStatus.Open, Guid.NewGuid(), null, null, null, null, [1]);
        _store.GetResult = expected;

        var result = await _service.GetAsync(_user, workOrderId, CancellationToken.None);

        Assert.Same(_user, _store.LastGetUser);
        Assert.Equal(workOrderId, _store.LastGetId);
        Assert.Same(expected, result);
    }

    [Fact]
    public async Task GetAsync_WhenOutOfScopeOrMissing_ReturnsNull()
    {
        _store.GetResult = null;

        var result = await _service.GetAsync(_user, Guid.NewGuid(), CancellationToken.None);

        Assert.Null(result);
    }
}
