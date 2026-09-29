using RepairRequest.Domain.WorkOrders;

namespace RepairRequest.Application.WorkOrders;

/// <summary>
/// Corrective Action projection (CA-001/003..012; `docs/13` §4.21/§4.22) — returned by Submit Plan, Approve Plan
/// and Schedule Rework. <see cref="WorkOrderRowVersion"/> is Submit/Approve Plan's own next If-Match token (the
/// Work Order's own RowVersion — see `docs/13` §4.21's own concurrency note), the same "cross-resource ETag" shape
/// already used by <see cref="WorkSessionDto.WorkOrderRowVersion"/>. <see cref="RowVersion"/> is Schedule Rework's
/// own next If-Match token instead — the Corrective Action's own row, since that action changes neither the
/// Corrective Action's <see cref="Status"/> nor the Work Order's (`docs/13` §4.22).
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
    byte[] WorkOrderRowVersion,
    Guid? CorrectiveServiceVisitId,
    byte[] RowVersion);

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
