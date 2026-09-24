namespace RepairRequest.Domain.WorkOrders;

/// <summary>
/// CA-006. Closed three-value allowlist (`docs/02` CA-006; `docs/03` §1 "Allowed states / codes") — no
/// IN_PROGRESS/COMPLETED/REWORK/CANCELLED value exists anywhere in the baseline. Only <see cref="Draft"/> is
/// reachable in this ticket's scope (ST-CA-001); <see cref="PendingPlanApproval"/> (ST-CA-002) and
/// <see cref="Approved"/> (ST-CA-003) belong to the future Corrective Action plan/approve ticket.
/// </summary>
public enum CorrectiveActionStatus
{
    Draft,
    PendingPlanApproval,
    Approved
}
