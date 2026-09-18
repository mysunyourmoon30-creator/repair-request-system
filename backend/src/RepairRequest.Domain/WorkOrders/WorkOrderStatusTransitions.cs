namespace RepairRequest.Domain.WorkOrders;

/// <summary>One allowed status change, traceable to its canonical RR-STS-001 transition ID.</summary>
public sealed record WorkOrderStatusTransition(
    string TransitionId,
    WorkOrderStatus From,
    string Action,
    WorkOrderStatus To);

/// <summary>
/// Work Order transition matrix (RR-STS-001, ST-WO-001..011), mirroring <c>RepairRequestStatusTransitions</c>.
/// S2-003 introduces the first entry (Schedule); every later Work Order transition remains out of scope.
/// </summary>
public static class WorkOrderStatusTransitions
{
    public static IReadOnlyList<WorkOrderStatusTransition> All { get; } =
    [
        new("ST-WO-001", WorkOrderStatus.Open, "Schedule", WorkOrderStatus.Scheduled),
    ];

    public static bool IsAllowed(WorkOrderStatus from, WorkOrderStatus to) =>
        All.Any(transition => transition.From == from && transition.To == to);
}
