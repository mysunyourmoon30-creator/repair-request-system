namespace RepairRequest.Domain.WorkOrders;

/// <summary>One allowed status change, traceable to its canonical RR-STS-001 transition ID.</summary>
public sealed record WorkSessionStatusTransition(
    string TransitionId,
    WorkSessionStatus From,
    string Action,
    WorkSessionStatus To);

/// <summary>
/// Work Session transition matrix (RR-STS-001, ST-WS-001..004). Check-in (ST-WS-001) creates the session and is
/// not a from-state transition; S3-002 registers Pause (ST-WS-002), S3-003 adds Resume (ST-WS-003). Check-out is
/// a later ticket.
/// </summary>
public static class WorkSessionStatusTransitions
{
    public static IReadOnlyList<WorkSessionStatusTransition> All { get; } =
    [
        new("ST-WS-002", WorkSessionStatus.CheckedIn, "Pause", WorkSessionStatus.Paused),
        new("ST-WS-003", WorkSessionStatus.Paused, "Resume", WorkSessionStatus.CheckedIn),
    ];

    public static bool IsAllowed(WorkSessionStatus from, WorkSessionStatus to) =>
        All.Any(transition => transition.From == from && transition.To == to);
}
