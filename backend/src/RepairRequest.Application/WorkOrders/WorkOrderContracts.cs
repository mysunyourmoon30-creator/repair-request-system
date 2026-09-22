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
    byte[] RowVersion,
    IReadOnlyList<ServiceVisitDto> Visits);

/// <summary>
/// Service Visit projection (S2-003; RR-DD-001 SV-001..018), embedded in the Work Order detail response — no
/// dedicated Service Visit list/detail resource exists (Portfolio Project Owner directive, S2-003
/// pre-implementation). Newest-first by <see cref="ScheduledStartAt"/> so a MISSED visit and its follow-up (if
/// any) are both visible.
/// </summary>
public sealed record ServiceVisitDto(
    Guid ServiceVisitId,
    Guid WorkOrderId,
    ServiceVisitType VisitType,
    ServiceVisitStatus Status,
    Guid? AssignedTeamId,
    Guid? AssignedTechnicianId,
    DateTime? ScheduledStartAt,
    DateTime? ScheduledEndAt,
    string? RescheduleReason,
    string? ReassignReason,
    string? CancelReason,
    string? MissedReason,
    DateTime? CompletedAt,
    Guid? SourceMissedVisitId,
    MissedVisitDecisionCode? MissedDecisionCode,
    DateTime? MissedDecidedAt,
    byte[] RowVersion);

/// <summary>List query: paging plus an optional status filter. Sort is fixed newest-first (DEC-S2-001-04).</summary>
public sealed record WorkOrderListQuery(PageRequest Paging, WorkOrderStatus? Status);

/// <summary>
/// "My Visits" list projection (S3-001; UI-040): a lightweight, technician-facing summary — never the full
/// Work Order aggregate graph, per `docs/09` section 9's "no full aggregate graph for list" principle. Always
/// scoped to the caller's own <see cref="IDataScope.AssignedServiceVisits"/>; no status filter is offered
/// since the list is meant to show only actionable (SCHEDULED) visits.
/// </summary>
public sealed record MyVisitSummaryDto(
    Guid ServiceVisitId,
    Guid WorkOrderId,
    string WorkOrderNo,
    ServiceVisitStatus Status,
    string? SiteCode,
    string? EquipmentCode,
    DateTime? ScheduledStartAt,
    DateTime? ScheduledEndAt,
    byte[] RowVersion);

/// <summary>My Visits list query: paging only (S3-001) — always the caller's own SCHEDULED visits, newest-first by scheduled start.</summary>
public sealed record MyVisitsQuery(PageRequest Paging);

/// <summary>One pause period of a Work Session (S3-002). Newest first in <see cref="WorkSessionDto.Pauses"/>; history is never replaced.</summary>
public sealed record WorkSessionPauseDto(Guid WorkSessionPauseId, DateTime PausedAt, string PauseReason, DateTime? ResumedAt);

/// <summary>
/// Work Session projection for the owning Technician (S3-002; RR-DD-001 WS-001..010): the session, a small summary
/// of the Visit/Work Order it belongs to, and its pause history. <see cref="RowVersion"/> is the session's own
/// token — the If-Match value of Pause (WS-API-002).
/// </summary>
public sealed record WorkSessionDto(
    Guid WorkSessionId,
    Guid ServiceVisitId,
    Guid WorkOrderId,
    string WorkOrderNo,
    string? SiteCode,
    string? EquipmentCode,
    WorkSessionStatus Status,
    DateTime CheckInAt,
    DateTime? PauseStartAt,
    DateTime? ResumeAt,
    DateTime? CheckOutAt,
    IReadOnlyList<WorkSessionPauseDto> Pauses,
    byte[] RowVersion);

/// <summary>Canonical upper-snake Work Session status codes (RR-DD-001 WS-005; RR-STS-001 section 1).</summary>
public static class WorkSessionStatusCodes
{
    public static string ToCode(WorkSessionStatus status) => status switch
    {
        WorkSessionStatus.CheckedIn => "CHECKED_IN",
        WorkSessionStatus.Paused => "PAUSED",
        WorkSessionStatus.CheckedOut => "CHECKED_OUT",
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };
}

/// <summary>API field names used as keys of Work Session actions' 422 errors.</summary>
public static class WorkSessionFields
{
    public const string Reason = "reason";
}

/// <summary>
/// The new follow-up Visit's schedule for a Decide Missed decision (`newSchedule`, RR-API-009 WO-API-007) — required
/// only for RESCHEDULE/FOLLOW_UP/REASSIGN, absent for NO_FOLLOW_UP.
/// </summary>
public sealed record MissedVisitFollowUpSchedule(
    Guid? AssignedTeamId,
    Guid? AssignedTechnicianId,
    DateTimeOffset? ScheduledStartAt,
    DateTimeOffset? ScheduledEndAt);

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

/// <summary>API field names used as keys of the 400 BAD_REQUEST error for the list query string, and of Schedule's 422 errors.</summary>
public static class WorkOrderFields
{
    public const string Status = "status";
    public const string OwnerTeamId = "ownerTeamId";
    public const string AssignedTechnicianId = "assignedTechnicianId";
    public const string ScheduledStartAt = "scheduledStartAt";
    public const string ScheduledEndAt = "scheduledEndAt";
}

/// <summary>Canonical upper-snake Service Visit status codes (RR-DD-001 SV-005; RR-STS-001 section 1).</summary>
public static class ServiceVisitStatusCodes
{
    public static string ToCode(ServiceVisitStatus status) => status switch
    {
        ServiceVisitStatus.Scheduled => "SCHEDULED",
        ServiceVisitStatus.Rescheduled => "RESCHEDULED",
        ServiceVisitStatus.InProgress => "IN_PROGRESS",
        ServiceVisitStatus.Completed => "COMPLETED",
        ServiceVisitStatus.Missed => "MISSED",
        ServiceVisitStatus.Cancelled => "CANCELLED",
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };
}

/// <summary>Canonical upper-snake Service Visit type codes (RR-DD-001 SV-004).</summary>
public static class ServiceVisitTypeCodes
{
    public static string ToCode(ServiceVisitType visitType) => visitType switch
    {
        ServiceVisitType.Initial => "INITIAL",
        ServiceVisitType.FollowUp => "FOLLOW_UP",
        ServiceVisitType.Corrective => "CORRECTIVE",
        _ => throw new ArgumentOutOfRangeException(nameof(visitType))
    };
}

/// <summary>Canonical upper-snake Missed Decision codes (RR-DD-001 SV-017; D-15).</summary>
public static class MissedVisitDecisionCodes
{
    public static string ToCode(MissedVisitDecisionCode decision) => decision switch
    {
        MissedVisitDecisionCode.Reschedule => "RESCHEDULE",
        MissedVisitDecisionCode.FollowUp => "FOLLOW_UP",
        MissedVisitDecisionCode.Reassign => "REASSIGN",
        MissedVisitDecisionCode.NoFollowUp => "NO_FOLLOW_UP",
        _ => throw new ArgumentOutOfRangeException(nameof(decision))
    };

    public static bool TryParse(string? code, out MissedVisitDecisionCode decision)
    {
        switch (code)
        {
            case "RESCHEDULE": decision = MissedVisitDecisionCode.Reschedule; return true;
            case "FOLLOW_UP": decision = MissedVisitDecisionCode.FollowUp; return true;
            case "REASSIGN": decision = MissedVisitDecisionCode.Reassign; return true;
            case "NO_FOLLOW_UP": decision = MissedVisitDecisionCode.NoFollowUp; return true;
            default: decision = default; return false;
        }
    }
}

/// <summary>API field names used as keys of Service Visit actions' 422 errors.</summary>
public static class ServiceVisitFields
{
    public const string Reason = "reason";
    public const string AssignedTeamId = "assignedTeamId";
    public const string AssignedTechnicianId = "assignedTechnicianId";
    public const string ScheduledStartAt = "scheduledStartAt";
    public const string ScheduledEndAt = "scheduledEndAt";
    public const string Decision = "decision";
    public const string NewSchedule = "newSchedule";
}
