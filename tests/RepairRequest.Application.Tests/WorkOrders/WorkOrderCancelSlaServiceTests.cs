using System.Text.Json;
using RepairRequest.Application.Common;
using RepairRequest.Application.Security;
using RepairRequest.Application.Sla;
using RepairRequest.Application.Tests.Sla;
using RepairRequest.Application.WorkOrders;
using RepairRequest.Domain.Auditing;
using RepairRequest.Domain.Security;
using RepairRequest.Domain.Sla;
using RepairRequest.Domain.WorkOrders;

namespace RepairRequest.Application.Tests.WorkOrders;

/// <summary>
/// The SLA step of Work Order Cancel (ST-WO-011; EV-SLA-005; `docs/16` D6) against an in-memory store: the stop runs once with
/// WO_CANCELLED after every guard, and nothing SLA-related happens on a failed command.
/// </summary>
public class WorkOrderCancelSlaServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 9, 15, 0, 321, TimeSpan.Zero);

    private readonly FakeSlaRecordStore _sla = new();
    private readonly FakeWorkOrderCancelStore _store = new();
    private readonly WorkOrderCancelService _service;
    private readonly CommandContext _context = new(new CurrentUser(Guid.NewGuid(), Guid.NewGuid(), [RoleCodes.Supervisor]), Guid.NewGuid());

    public WorkOrderCancelSlaServiceTests()
    {
        _service = new WorkOrderCancelService(_store, _sla, new CancelFixedClock(Now));
    }

    private static WorkOrder OpenWorkOrder()
    {
        var workOrder = WorkOrder.Create(Guid.NewGuid(), Guid.NewGuid(), "WO-1", DateTime.UtcNow);
        workOrder.GetType().GetProperty(nameof(WorkOrder.Id))!.SetValue(workOrder, Guid.NewGuid());
        return workOrder;
    }

    private Task<CommandResult<Guid>> CancelAsync(WorkOrder workOrder, string? reason = "Customer withdrew the request", byte[]? rowVersion = null) =>
        _service.CancelAsync(_context, workOrder.Id, rowVersion ?? workOrder.RowVersion, reason, CancellationToken.None);

    [Fact]
    public async Task Cancel_StopsTheOriginatingRequestsSlaOnce_WithWoCancelled_AndAuditsSlaStoppedAsSystem()
    {
        var workOrder = OpenWorkOrder();
        _store.WorkOrder = workOrder;
        var slaRecordId = Guid.NewGuid();
        _sla.StopResult = slaRecordId;

        var result = await CancelAsync(workOrder);

        Assert.True(result.Succeeded);
        Assert.Equal(WorkOrderStatus.Cancelled, workOrder.Status);
        var stop = Assert.Single(_sla.Stops);
        Assert.Equal(workOrder.TenantId, stop.TenantId);
        Assert.Equal(workOrder.RepairRequestId, stop.RepairRequestId);
        Assert.Equal(SlaStopReason.WoCancelled, stop.Reason);
        Assert.Equal(Now.UtcDateTime, stop.StoppedAt);

        Assert.Equal(2, _store.Audits.Count);
        Assert.Equal("WORK_ORDER_CANCELLED", _store.Audits[0].ActionCode);
        Assert.Equal(_store.Audits[0].OccurredAt, stop.StoppedAt);
        var sla = _store.Audits[1];
        Assert.Equal(SlaAudit.StoppedAction, sla.ActionCode);
        Assert.Equal(SlaAudit.EntityType, sla.EntityType);
        Assert.Equal(slaRecordId, sla.EntityId);
        Assert.Equal(SystemActors.SlaTracking, sla.ActorId);
        Assert.Equal(_context.CorrelationId, sla.CorrelationId);
        var values = JsonDocument.Parse(sla.NewValueJson!).RootElement;
        Assert.Equal(workOrder.RepairRequestId, values.GetProperty("repairRequestId").GetGuid());
        Assert.Equal("WO_CANCELLED", values.GetProperty("stoppedReason").GetString());
        Assert.Equal(1, _store.SaveCalls);
    }

    [Fact]
    public async Task Cancel_ForALegacyRequestWithoutAnSlaRecord_IsASuccessfulNoOp_WithoutSlaAudit()
    {
        var workOrder = OpenWorkOrder();
        _store.WorkOrder = workOrder;
        _sla.StopResult = null;

        Assert.True((await CancelAsync(workOrder)).Succeeded);

        Assert.Single(_sla.Stops);
        Assert.Equal("WORK_ORDER_CANCELLED", Assert.Single(_store.Audits).ActionCode);
    }

    [Fact]
    public async Task Cancel_WhenAnyGuardFails_NeverTouchesTheSla()
    {
        _sla.StopResult = Guid.NewGuid();
        var workOrder = OpenWorkOrder();

        _store.WorkOrder = null;
        Assert.Equal(CommandFailure.NotFound, (await CancelAsync(workOrder)).Error?.Failure);

        _store.WorkOrder = workOrder;
        Assert.Equal(CommandFailure.ConcurrencyConflict, (await CancelAsync(workOrder, rowVersion: [9, 9, 9, 9, 9, 9, 9, 9])).Error?.Failure);
        Assert.Equal(CommandFailure.ValidationFailed, (await CancelAsync(workOrder, reason: " ")).Error?.Failure);

        Assert.Empty(_sla.Stops);
        Assert.Empty(_store.Audits);
        Assert.Equal(0, _store.SaveCalls);
        Assert.Equal(WorkOrderStatus.Open, workOrder.Status);
    }
}

internal sealed class FakeWorkOrderCancelStore : IWorkOrderCancelStore
{
    public WorkOrder? WorkOrder { get; set; }

    public List<AuditHistory> Audits { get; } = [];

    public int SaveCalls { get; private set; }

    public Task<CommandResult<T>> RunInTransactionAsync<T>(Func<Task<CommandResult<T>>> command, CancellationToken cancellationToken) => command();

    public Task<WorkOrder?> LoadInScopeAsync(CurrentUser user, Guid workOrderId, CancellationToken cancellationToken) => Task.FromResult(WorkOrder);

    public Task<IReadOnlyList<ServiceVisit>> GetServiceVisitsAsync(Guid workOrderId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ServiceVisit>>([]);

    public void AddAudit(AuditHistory audit) => Audits.Add(audit);

    public Task<WorkOrderSaveOutcome> SaveChangesAsync(WorkOrder workOrder, byte[] expectedRowVersion, CancellationToken cancellationToken)
    {
        SaveCalls++;
        return Task.FromResult(WorkOrderSaveOutcome.Saved);
    }
}

internal sealed class CancelFixedClock(DateTimeOffset utcNow) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => utcNow;
}
