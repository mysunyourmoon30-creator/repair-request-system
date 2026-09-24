namespace RepairRequest.Domain.WorkOrders;

/// <summary>One allowed status change, traceable to its canonical RR-STS-001 transition ID.</summary>
public sealed record WorkOrderStatusTransition(
    string TransitionId,
    WorkOrderStatus From,
    string Action,
    WorkOrderStatus To);

/// <summary>
/// Work Order transition matrix (RR-STS-001, ST-WO-001..011), mirroring <c>RepairRequestStatusTransitions</c>.
/// S2-003 introduces the first entry (Schedule); S3-001 adds Check-in. ST-WO-003/004 are added for Work Summary
/// Submit/Review, with actors corrected from the baseline per the Portfolio Project Owner directives recorded in
/// `docs/13` §4.15 (ST-WO-003 actor is the Technician, not the baseline's "Team Lead"; ST-WO-004 actor is Team
/// Lead OR Supervisor, not the baseline's "Supervisor" alone — actor enforcement itself lives in the API
/// authorization policy, not here). Every later Work Order transition (ST-WO-005..011) remains out of scope.
/// </summary>
public static class WorkOrderStatusTransitions
{
    public static IReadOnlyList<WorkOrderStatusTransition> All { get; } =
    [
        new("ST-WO-001", WorkOrderStatus.Open, "Schedule", WorkOrderStatus.Scheduled),
        new("ST-WO-002", WorkOrderStatus.Scheduled, "CheckIn", WorkOrderStatus.InProgress),
        new("ST-WO-003", WorkOrderStatus.InProgress, "SubmitWorkSummary", WorkOrderStatus.AwaitingSupervisorReview),
        new("ST-WO-004", WorkOrderStatus.AwaitingSupervisorReview, "SubmitForAcceptance", WorkOrderStatus.AwaitingCustomerAcceptance),
    ];

    public static bool IsAllowed(WorkOrderStatus from, WorkOrderStatus to) =>
        All.Any(transition => transition.From == from && transition.To == to);
}
