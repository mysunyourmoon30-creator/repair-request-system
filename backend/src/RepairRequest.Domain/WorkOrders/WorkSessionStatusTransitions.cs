namespace RepairRequest.Domain.WorkOrders;

/// <summary>One allowed status change, traceable to its canonical RR-STS-001 transition ID.</summary>
public sealed record WorkSessionStatusTransition(
    string TransitionId,
    WorkSessionStatus From,
    string Action,
    WorkSessionStatus To);

/// <summary>
/// Work Session transition matrix (RR-STS-001, ST-WS-001..004). Check-in (ST-WS-001) creates the session and is
/// not a from-state transition; S3-002 registers only Pause (ST-WS-002). Resume and Check-out are later tickets.
/// </summary>
public static class WorkSessionStatusTransitions
{
    public static IReadOnlyList<WorkSessionStatusTransition> All { get; } =
    [
        new("ST-WS-002", WorkSessionStatus.CheckedIn, "Pause", WorkSessionStatus.Paused),
    ];

    public static bool IsAllowed(WorkSessionStatus from, WorkSessionStatus to) =>
        All.Any(transition => transition.From == from && transition.To == to);
}
