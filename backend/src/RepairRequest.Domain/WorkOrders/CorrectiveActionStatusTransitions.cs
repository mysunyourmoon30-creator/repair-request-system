namespace RepairRequest.Domain.WorkOrders;

/// <summary>One allowed Corrective Action status change, traceable to its canonical RR-STS-001 transition ID.</summary>
public sealed record CorrectiveActionStatusTransition(
    string TransitionId,
    CorrectiveActionStatus From,
    string Action,
    CorrectiveActionStatus To);

/// <summary>
/// Corrective Action transition matrix (RR-STS-001 section 5 "Work Session / Corrective Action / Time
/// Correction"; `docs/13` §4.21; Ticket 6), mirroring <see cref="WorkOrderStatusTransitions"/>. ST-CA-001
/// (creation into DRAFT) is a construction, not a transition, so it is not listed here — see
/// <see cref="CorrectiveAction.CreateDraft"/>. ST-CA-002 (Submit Plan) and ST-CA-003 (Approve Plan) are added by
/// Ticket 6; the corresponding <see cref="WorkOrder"/> transitions (ST-WO-008/009) are enforced separately by
/// <see cref="WorkOrderStatusTransitions"/>, since both status machines advance together in the same command.
/// Rework/resubmission (beyond ST-CA-003) remains out of scope.
/// </summary>
public static class CorrectiveActionStatusTransitions
{
    public static IReadOnlyList<CorrectiveActionStatusTransition> All { get; } =
    [
        new("ST-CA-002", CorrectiveActionStatus.Draft, "SubmitPlan", CorrectiveActionStatus.PendingPlanApproval),
        new("ST-CA-003", CorrectiveActionStatus.PendingPlanApproval, "ApprovePlan", CorrectiveActionStatus.Approved),
    ];

    public static bool IsAllowed(CorrectiveActionStatus from, CorrectiveActionStatus to) =>
        All.Any(transition => transition.From == from && transition.To == to);
}
