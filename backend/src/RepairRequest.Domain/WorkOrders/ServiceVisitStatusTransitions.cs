namespace RepairRequest.Domain.WorkOrders;

/// <summary>One allowed status change, traceable to its canonical RR-STS-001 transition ID.</summary>
public sealed record ServiceVisitStatusTransition(
    string TransitionId,
    ServiceVisitStatus From,
    string Action,
    ServiceVisitStatus To);

/// <summary>
/// Service Visit transition matrix (RR-STS-001, ST-SV-001..009), limited to the true cross-state transitions
/// S2-003 implements. Reschedule and Reassign are same-state actions (guarded directly by <see cref="ServiceVisit"/>'s
/// own methods, since a from==to row does not fit this table); Decide Missed likewise never changes the original
/// Visit's own status. Check-in/Check-out (ST-SV-002/003) are a later ticket's transitions and are not listed here.
/// </summary>
public static class ServiceVisitStatusTransitions
{
    public static IReadOnlyList<ServiceVisitStatusTransition> All { get; } =
    [
        new("ST-SV-007", ServiceVisitStatus.Scheduled, "Cancel", ServiceVisitStatus.Cancelled),
        new("ST-SV-008", ServiceVisitStatus.Scheduled, "MarkMissed", ServiceVisitStatus.Missed),
    ];

    public static bool IsAllowed(ServiceVisitStatus from, ServiceVisitStatus to) =>
        All.Any(transition => transition.From == from && transition.To == to);
}
