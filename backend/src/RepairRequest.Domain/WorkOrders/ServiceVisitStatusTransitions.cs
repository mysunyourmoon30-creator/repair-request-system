namespace RepairRequest.Domain.WorkOrders;

/// <summary>One allowed status change, traceable to its canonical RR-STS-001 transition ID.</summary>
public sealed record ServiceVisitStatusTransition(
    string TransitionId,
    ServiceVisitStatus From,
    string Action,
    ServiceVisitStatus To);

/// <summary>
/// Service Visit transition matrix (RR-STS-001, ST-SV-001..009), limited to the true cross-state transitions
/// implemented so far. Reschedule and Reassign are same-state actions (guarded directly by <see cref="ServiceVisit"/>'s
/// own methods, since a from==to row does not fit this table); Decide Missed likewise never changes the original
/// Visit's own status. S3-001 adds Check-in (ST-SV-002); S3-004 adds Check-out (ST-SV-003).
/// </summary>
public static class ServiceVisitStatusTransitions
{
    public static IReadOnlyList<ServiceVisitStatusTransition> All { get; } =
    [
        new("ST-SV-002", ServiceVisitStatus.Scheduled, "CheckIn", ServiceVisitStatus.InProgress),
        new("ST-SV-003", ServiceVisitStatus.InProgress, "CheckOut", ServiceVisitStatus.Completed),
        new("ST-SV-007", ServiceVisitStatus.Scheduled, "Cancel", ServiceVisitStatus.Cancelled),
        new("ST-SV-008", ServiceVisitStatus.Scheduled, "MarkMissed", ServiceVisitStatus.Missed),
    ];

    public static bool IsAllowed(ServiceVisitStatus from, ServiceVisitStatus to) =>
        All.Any(transition => transition.From == from && transition.To == to);
}
