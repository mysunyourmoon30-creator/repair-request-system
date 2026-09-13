namespace RepairRequest.Domain.RepairRequests;

/// <summary>One allowed status change, traceable to its canonical RR-STS-001 transition ID.</summary>
public sealed record RepairRequestStatusTransition(
    string TransitionId,
    RepairRequestStatus From,
    string Action,
    RepairRequestStatus To);

/// <summary>
/// Repair Request transition matrix, encoded verbatim from RR-STS-001 section 2.
/// ST-RR-001 (Create/Edit, DRAFT -> DRAFT) is not a status change and is therefore
/// not listed. Actor, guard and side-effect rules (reason, scope, rowversion,
/// SLA, Work Order creation) are enforced by the use-case commands that invoke
/// these transitions, not by this table.
/// </summary>
public static class RepairRequestStatusTransitions
{
    public static IReadOnlyList<RepairRequestStatusTransition> All { get; } =
    [
        new("ST-RR-002", RepairRequestStatus.Draft, "Submit", RepairRequestStatus.Submitted),
        new("ST-RR-003", RepairRequestStatus.Submitted, "RouteForReview", RepairRequestStatus.UnderReview),
        new("ST-RR-004", RepairRequestStatus.Submitted, "Approve", RepairRequestStatus.Approved),
        new("ST-RR-004", RepairRequestStatus.UnderReview, "Approve", RepairRequestStatus.Approved),
        new("ST-RR-005", RepairRequestStatus.Submitted, "Reject", RepairRequestStatus.Rejected),
        new("ST-RR-005", RepairRequestStatus.UnderReview, "Reject", RepairRequestStatus.Rejected),
        new("ST-RR-006", RepairRequestStatus.Submitted, "ReturnForCorrection", RepairRequestStatus.Draft),
        new("ST-RR-006", RepairRequestStatus.UnderReview, "ReturnForCorrection", RepairRequestStatus.Draft),
        new("ST-RR-007", RepairRequestStatus.Draft, "Cancel", RepairRequestStatus.Cancelled),
        new("ST-RR-007", RepairRequestStatus.Submitted, "Cancel", RepairRequestStatus.Cancelled),
        new("ST-RR-007", RepairRequestStatus.UnderReview, "Cancel", RepairRequestStatus.Cancelled),
        new("ST-RR-007", RepairRequestStatus.Approved, "Cancel", RepairRequestStatus.Cancelled),
        new("ST-RR-008", RepairRequestStatus.Approved, "CreateWorkOrder", RepairRequestStatus.Converted),
    ];

    public static bool IsAllowed(RepairRequestStatus from, RepairRequestStatus to) =>
        All.Any(transition => transition.From == from && transition.To == to);

    public static IEnumerable<RepairRequestStatusTransition> From(RepairRequestStatus status) =>
        All.Where(transition => transition.From == status);
}
