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
/// BR-07/BR-15; `docs/13` §4.17), same actor as ST-WO-005. ST-WO-006 is added for Work Order Close (BR-08;
/// `docs/13` §4.20), actor Supervisor — its Cost Summary/Work Summary/acceptance prerequisites are data guards
/// the Application service checks, not part of this status matrix. ST-WO-008/009 are added for Corrective Action
/// Submit Plan (Team Lead) and Approve Plan (Supervisor) (`docs/13` §4.21; Ticket 6) — the Corrective Action's own
/// status guard is enforced by the Application service, not here. ST-WO-010 is added for Technician Check-in on
/// the corrective Visit (`docs/13` §4.23) — the existing generic Check-in flow (<see cref="RepairRequest.Application.WorkOrders.TechnicianCheckInService"/>)
/// was already Visit-type-agnostic everywhere except this one matrix entry. Actor enforcement itself always lives
/// in the API authorization policy / Application service, not here. ST-WO-011 (Cancel; `docs/13` §4.25) is added
/// as eight rows, one per non-terminal, pre-Accept source status (RR-STS-001's own "OPEN/SCHEDULED/IN_PROGRESS/
/// AWAITING_*/CORRECTIVE_*" shorthand) — COMPLETED, CLOSED and CANCELLED itself remain the only denied sources.
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
        new("ST-WO-006", WorkOrderStatus.Completed, "Close", WorkOrderStatus.Closed),
        new("ST-WO-007", WorkOrderStatus.AwaitingCustomerAcceptance, "Reject", WorkOrderStatus.CorrectiveActionRequired),
        new("ST-WO-008", WorkOrderStatus.CorrectiveActionRequired, "SubmitCorrectivePlan", WorkOrderStatus.CorrectivePlanPending),
        new("ST-WO-009", WorkOrderStatus.CorrectivePlanPending, "ApproveCorrectivePlan", WorkOrderStatus.CorrectivePlanApproved),
        new("ST-WO-010", WorkOrderStatus.CorrectivePlanApproved, "StartRework", WorkOrderStatus.InProgress),
        new("ST-WO-011", WorkOrderStatus.Open, "Cancel", WorkOrderStatus.Cancelled),
        new("ST-WO-011", WorkOrderStatus.Scheduled, "Cancel", WorkOrderStatus.Cancelled),
        new("ST-WO-011", WorkOrderStatus.InProgress, "Cancel", WorkOrderStatus.Cancelled),
        new("ST-WO-011", WorkOrderStatus.AwaitingSupervisorReview, "Cancel", WorkOrderStatus.Cancelled),
        new("ST-WO-011", WorkOrderStatus.AwaitingCustomerAcceptance, "Cancel", WorkOrderStatus.Cancelled),
        new("ST-WO-011", WorkOrderStatus.CorrectiveActionRequired, "Cancel", WorkOrderStatus.Cancelled),
        new("ST-WO-011", WorkOrderStatus.CorrectivePlanPending, "Cancel", WorkOrderStatus.Cancelled),
        new("ST-WO-011", WorkOrderStatus.CorrectivePlanApproved, "Cancel", WorkOrderStatus.Cancelled),
    ];

    public static bool IsAllowed(WorkOrderStatus from, WorkOrderStatus to) =>
        All.Any(transition => transition.From == from && transition.To == to);
}
