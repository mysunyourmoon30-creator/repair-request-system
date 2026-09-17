namespace RepairRequest.Domain.Approvals;

/// <summary>
/// Approval decision codes of a Repair Request approval step (RR-DD-001 APR-007; RR-DBD-001 repair_request_approval).
/// Persisted as PENDING / APPROVED / REJECTED / RETURNED_FOR_CORRECTION. S1-007R only creates PENDING rows; decisions
/// belong to S1-008 (Approve/Reject) and the Return-for-Correction ticket.
/// </summary>
public enum ApprovalStatus
{
    Pending,
    Approved,
    Rejected,
    ReturnedForCorrection
}
