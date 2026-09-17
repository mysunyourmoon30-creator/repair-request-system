using RepairRequest.Application.Common;
using RepairRequest.Domain.WorkOrders;

namespace RepairRequest.Application.WorkOrders;

/// <summary>
/// Work Order list/detail projection (S2-001 / DEC-S2-001-01..04). Customer/Site/Equipment are code-only
/// (no display name exists on those entities); Scheduled Date and Assigned Team/Technician are intentionally
/// absent (DEC-S2-001-02/03).
/// </summary>
public sealed record WorkOrderDto(
    Guid WorkOrderId,
    string WorkOrderNo,
    WorkOrderStatus Status,
    Guid RepairRequestId,
    string? RepairRequestNo,
    string? CustomerCode,
    string? SiteCode,
    string? EquipmentCode,
    byte[] RowVersion);

/// <summary>List query: paging plus an optional status filter. Sort is fixed newest-first (DEC-S2-001-04).</summary>
public sealed record WorkOrderListQuery(PageRequest Paging, WorkOrderStatus? Status);

/// <summary>Canonical upper-snake Work Order status codes (RR-DD-001 WO-005; RR-STS-001 section 3).</summary>
public static class WorkOrderStatusCodes
{
    public static string ToCode(WorkOrderStatus status) => status switch
    {
        WorkOrderStatus.Open => "OPEN",
        WorkOrderStatus.Scheduled => "SCHEDULED",
        WorkOrderStatus.InProgress => "IN_PROGRESS",
        WorkOrderStatus.AwaitingSupervisorReview => "AWAITING_SUPERVISOR_REVIEW",
        WorkOrderStatus.AwaitingCustomerAcceptance => "AWAITING_CUSTOMER_ACCEPTANCE",
        WorkOrderStatus.Completed => "COMPLETED",
        WorkOrderStatus.CorrectiveActionRequired => "CORRECTIVE_ACTION_REQUIRED",
        WorkOrderStatus.CorrectivePlanPending => "CORRECTIVE_PLAN_PENDING",
        WorkOrderStatus.CorrectivePlanApproved => "CORRECTIVE_PLAN_APPROVED",
        WorkOrderStatus.Closed => "CLOSED",
        WorkOrderStatus.Cancelled => "CANCELLED",
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };

    public static bool TryParse(string? code, out WorkOrderStatus status)
    {
        switch (code)
        {
            case "OPEN": status = WorkOrderStatus.Open; return true;
            case "SCHEDULED": status = WorkOrderStatus.Scheduled; return true;
            case "IN_PROGRESS": status = WorkOrderStatus.InProgress; return true;
            case "AWAITING_SUPERVISOR_REVIEW": status = WorkOrderStatus.AwaitingSupervisorReview; return true;
            case "AWAITING_CUSTOMER_ACCEPTANCE": status = WorkOrderStatus.AwaitingCustomerAcceptance; return true;
            case "COMPLETED": status = WorkOrderStatus.Completed; return true;
            case "CORRECTIVE_ACTION_REQUIRED": status = WorkOrderStatus.CorrectiveActionRequired; return true;
            case "CORRECTIVE_PLAN_PENDING": status = WorkOrderStatus.CorrectivePlanPending; return true;
            case "CORRECTIVE_PLAN_APPROVED": status = WorkOrderStatus.CorrectivePlanApproved; return true;
            case "CLOSED": status = WorkOrderStatus.Closed; return true;
            case "CANCELLED": status = WorkOrderStatus.Cancelled; return true;
            default: status = default; return false;
        }
    }
}

/// <summary>API field names used as keys of the 400 BAD_REQUEST error for the list query string.</summary>
public static class WorkOrderFields
{
    public const string Status = "status";
}
