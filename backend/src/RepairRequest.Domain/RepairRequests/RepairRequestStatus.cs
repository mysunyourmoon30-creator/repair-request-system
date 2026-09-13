namespace RepairRequest.Domain.RepairRequests;

/// <summary>
/// Canonical Repair Request states (RR-STS-001 section 1; RR-DD-001 RR-004).
/// Persisted as DRAFT / SUBMITTED / UNDER_REVIEW / APPROVED / REJECTED / CANCELLED / CONVERTED.
/// There is no RETURNED state: Return for Correction is an action back to DRAFT.
/// </summary>
public enum RepairRequestStatus
{
    Draft,
    Submitted,
    UnderReview,
    Approved,
    Rejected,
    Cancelled,
    Converted
}
