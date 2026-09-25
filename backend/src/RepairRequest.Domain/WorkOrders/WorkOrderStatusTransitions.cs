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
/// Lead OR Supervisor, not the baseline's "Supervisor" alone). ST-WO-005 is added for Customer Accept (UC-WO-021;
/// `docs/13` §4.16), actor the exact designated Acceptance Contact (a REQUESTER, per Decision 1 — a
/// resource-specific check, not a role-scoped query). ST-WO-007 is added for Customer Reject (UC-WO-022;
/// BR-07/BR-15; `docs/13` §4.17), same actor as ST-WO-005; ST-WO-006 (Work Order Close) remains out of scope for
/// this ticket, matching how ST-WO-005 was added ahead of ST-WO-006 in the previous ticket. Actor enforcement
/// itself always lives in the API authorization policy / Application service, not here. Every later Work Order
/// transition (ST-WO-006, ST-WO-008..011) remains out of scope.
/// </summary>
public static class WorkOrderStatusTransitions
{
    public static IReadOnlyList<WorkOrderStatusTransition> All { get; } =
    [
        new("ST-WO-001", WorkOrderStatus.Open, "Schedule", WorkOrderStatus.Scheduled),
        new("ST-WO-002", WorkOrderStatus.Scheduled, "CheckIn", WorkOrderStatus.InProgress),
        new("ST-WO-003", WorkOrderStatus.InProgress, "SubmitWorkSummary", WorkOrderStatus.AwaitingSupervisorReview),
        new("ST-WO-004", WorkOrderStatus.AwaitingSupervisorReview, "SubmitForAcceptance", WorkOrderStatus.AwaitingCustomerAcceptance),
        new("ST-WO-005", WorkOrderStatus.AwaitingCustomerAcceptance, "Accept", WorkOrderStatus.Completed),
        new("ST-WO-007", WorkOrderStatus.AwaitingCustomerAcceptance, "Reject", WorkOrderStatus.CorrectiveActionRequired),
    ];

    public static bool IsAllowed(WorkOrderStatus from, WorkOrderStatus to) =>
        All.Any(transition => transition.From == from && transition.To == to);
}
