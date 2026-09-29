namespace RepairRequest.Domain.WorkOrders;

/// <summary>
/// CA-006. Closed three-value allowlist (`docs/02` CA-006; `docs/03` §1 "Allowed states / codes") — no
/// IN_PROGRESS/COMPLETED/REWORK/CANCELLED value exists anywhere in the baseline. <see cref="Draft"/> (ST-CA-001,
/// Ticket 3), <see cref="PendingPlanApproval"/> (ST-CA-002) and <see cref="Approved"/> (ST-CA-003, both Ticket 6;
/// `docs/13` §4.21) are all reachable; rework/resubmission beyond ST-CA-003 remains out of scope.
/// </summary>
public enum CorrectiveActionStatus
{
    Draft,
    PendingPlanApproval,
    Approved
}
