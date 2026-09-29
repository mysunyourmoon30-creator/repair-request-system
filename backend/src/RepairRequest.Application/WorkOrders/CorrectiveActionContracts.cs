using RepairRequest.Domain.WorkOrders;

namespace RepairRequest.Application.WorkOrders;

/// <summary>
/// Corrective Action projection (CA-001/003..012; `docs/13` §4.21) — returned by Submit Plan and Approve Plan.
/// <see cref="WorkOrderRowVersion"/> is the token the caller's *next* If-Match must carry (the Work Order's own
/// RowVersion, not a `corrective_action` token — see `docs/13` §4.21's own concurrency note), the same "cross-
/// resource ETag" shape already used by <see cref="WorkSessionDto.WorkOrderRowVersion"/>.
/// </summary>
public sealed record CorrectiveActionDto(
    Guid CorrectiveActionId,
    Guid WorkOrderId,
    int CycleNo,
    CorrectiveActionStatus Status,
    Guid? OwnerTeamLeadId,
    string? PlanText,
    Guid? PlanFileAssetId,
    Guid? ApprovedBy,
    DateTime? ApprovedAt,
    byte[] WorkOrderRowVersion);

/// <summary>CA-API-001 Submit Plan body fields (`docs/13` §4.21) — used as 422 error keys.</summary>
public static class CorrectiveActionFields
{
    public const string PlanText = "planText";
    public const string PlanFileAssetId = "planFileAssetId";
}

/// <summary>Canonical upper-snake Corrective Action status codes (RR-DD-001 CA-006; RR-STS-001 §1).</summary>
public static class CorrectiveActionStatusCodes
{
    public static string ToCode(CorrectiveActionStatus status) => status switch
    {
        CorrectiveActionStatus.Draft => "DRAFT",
        CorrectiveActionStatus.PendingPlanApproval => "PENDING_PLAN_APPROVAL",
        CorrectiveActionStatus.Approved => "APPROVED",
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };
}
