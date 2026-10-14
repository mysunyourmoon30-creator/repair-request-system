namespace RepairRequest.Domain.WorkOrders;

/// <summary>One allowed status change, traceable to its canonical RR-STS-001 transition ID.</summary>
public sealed record TimeCorrectionStatusTransition(
    string TransitionId,
    TimeCorrectionStatus From,
    string Action,
    TimeCorrectionStatus To);

/// <summary>
/// Time Correction transition matrix (RR-STS-001, ST-TC-001..003; S3-005). Request (ST-TC-001) creates the
/// record and is not a from-state transition, same convention as Check-in (ST-WS-001).
/// </summary>
public static class TimeCorrectionStatusTransitions
{
    public static IReadOnlyList<TimeCorrectionStatusTransition> All { get; } =
    [
        new("ST-TC-002", TimeCorrectionStatus.Pending, "Approve", TimeCorrectionStatus.Approved),
        new("ST-TC-003", TimeCorrectionStatus.Pending, "Reject", TimeCorrectionStatus.Rejected),
    ];

    public static bool IsAllowed(TimeCorrectionStatus from, TimeCorrectionStatus to) =>
        All.Any(transition => transition.From == from && transition.To == to);
}
