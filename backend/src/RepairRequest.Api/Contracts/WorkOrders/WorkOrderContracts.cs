using System.Text.Json.Serialization;
using RepairRequest.Api.Contracts.RepairRequests;
using RepairRequest.Application.WorkOrders;

namespace RepairRequest.Api.Contracts.WorkOrders;

/// <summary>Query string of the Work Order list: page (1-based), pageSize (clamped to the configured maximum), status.</summary>
public sealed class WorkOrderListRequest
{
    public int? Page { get; init; }

    public int? PageSize { get; init; }

    /// <summary>One of the Work Order status codes (RR-DD-001 WO-005); omitted lists every status.</summary>
    public string? Status { get; init; }
}

/// <summary>
/// Work Order list/detail response (S2-001; extended S2-003 with <see cref="Visits"/>). Reused for both the list
/// and the detail action, and returned by Schedule and every Service Visit action so the caller never needs a
/// separate visit fetch. Customer/Site/Equipment are codes, not display names — the entities expose no name field.
/// </summary>
public sealed record WorkOrderResponse(
    Guid WorkOrderId,
    string WorkOrderNo,
    string Status,
    Guid RepairRequestId,
    string? RepairRequestNo,
    string? CustomerCode,
    string? SiteCode,
    string? EquipmentCode,
    string RowVersion,
    IReadOnlyList<ServiceVisitResponse> Visits);

/// <summary>Service Visit response (S2-003; RR-DD-001 SV-001..018). Team/technician are raw ids — no directory to resolve a display name from.</summary>
public sealed record ServiceVisitResponse(
    Guid ServiceVisitId,
    Guid WorkOrderId,
    string VisitType,
    string Status,
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
    string? MissedDecisionCode,
    DateTime? MissedDecidedAt,
    string RowVersion);

/// <summary>RR-API-002 (WO-API-002) Schedule body. All four fields are required. Timestamps need an explicit UTC offset.</summary>
public sealed record ScheduleWorkOrderRequest(
    Guid? OwnerTeamId,
    Guid? AssignedTechnicianId,
    [property: JsonConverter(typeof(ExplicitOffsetDateTimeOffsetConverter))] DateTimeOffset? ScheduledStartAt,
    [property: JsonConverter(typeof(ExplicitOffsetDateTimeOffsetConverter))] DateTimeOffset? ScheduledEndAt);

/// <summary>WO-API-004 Reschedule body. Reason and the new window are required.</summary>
public sealed record RescheduleServiceVisitRequest(
    string? Reason,
    [property: JsonConverter(typeof(ExplicitOffsetDateTimeOffsetConverter))] DateTimeOffset? ScheduledStartAt,
    [property: JsonConverter(typeof(ExplicitOffsetDateTimeOffsetConverter))] DateTimeOffset? ScheduledEndAt);

/// <summary>WO-API-003 Reassign body. Reason and the new team/technician are required.</summary>
public sealed record ReassignServiceVisitRequest(string? Reason, Guid? AssignedTeamId, Guid? AssignedTechnicianId);

/// <summary>WO-API-005 Cancel Visit body. Reason is required.</summary>
public sealed record CancelServiceVisitRequest(string? Reason);

/// <summary>WO-API-006 Mark Missed body. Reason is required.</summary>
public sealed record MarkMissedServiceVisitRequest(string? Reason);

/// <summary>WO-API-007 Decide Missed body. <see cref="NewSchedule"/> is required only for RESCHEDULE/FOLLOW_UP/REASSIGN.</summary>
public sealed record MissedDecisionRequest(string? Decision, string? Reason, NewVisitScheduleRequest? NewSchedule);

/// <summary>My Visits list query: paging only (S3-001) — always the caller's own SCHEDULED visits.</summary>
public sealed class MyVisitsListRequest
{
    public int? Page { get; init; }

    public int? PageSize { get; init; }
}

/// <summary>My Visits list item (S3-001; UI-040) — a lightweight projection, not the full Work Order graph.</summary>
public sealed record MyVisitSummaryResponse(
    Guid ServiceVisitId,
    Guid WorkOrderId,
    string WorkOrderNo,
    string Status,
    string? SiteCode,
    string? EquipmentCode,
    DateTime? ScheduledStartAt,
    DateTime? ScheduledEndAt,
    string RowVersion);

/// <summary>
/// WS-API-002 Pause body (S3-002). Only the reason: the session's status and the pause time are always
/// server-derived, so any other member a client sends (e.g. <c>status</c>, <c>pausedAt</c>) is ignored.
/// </summary>
public sealed record PauseWorkSessionRequest(string? Reason);

/// <summary>One pause period (S3-002), newest first in <see cref="WorkSessionResponse.Pauses"/>.</summary>
public sealed record WorkSessionPauseResponse(Guid WorkSessionPauseId, DateTime PausedAt, string PauseReason, DateTime? ResumedAt);

/// <summary>
/// Work Session response (S3-002; RR-DD-001 WS-001..010): returned by Pause and by the current-session read.
/// <see cref="RowVersion"/> is the session's own token, the If-Match value of the next session action.
/// <see cref="WorkOrderRowVersion"/> is the parent Work Order's own token (added for `docs/13` §4.15) — a
/// Technician has no other way to reach it, since `GET /work-orders/{id}` excludes TECHNICIAN
/// (DEC-S2-001-03); it is the If-Match value of `WSM-API-001` (Submit Work Summary) once the session reaches
/// CHECKED_OUT.
/// </summary>
public sealed record WorkSessionResponse(
    Guid WorkSessionId,
    Guid ServiceVisitId,
    Guid WorkOrderId,
    string WorkOrderNo,
    string? SiteCode,
    string? EquipmentCode,
    string Status,
    DateTime CheckInAt,
    DateTime? PauseStartAt,
    DateTime? ResumeAt,
    DateTime? CheckOutAt,
    IReadOnlyList<WorkSessionPauseResponse> Pauses,
    string RowVersion,
    string WorkOrderRowVersion);

/// <summary>WSM-API-001 Submit Work Summary body (`docs/13` §4.15 Decision 3). Both fields are required.</summary>
public sealed record SubmitWorkSummaryRequest(string? SummaryText, string? RepairOutcomeCode);

/// <summary>Work Summary response (UC-WO-020; `docs/13` §4.15) — returned by Submit and by the Work Summary read.</summary>
public sealed record WorkSummaryResponse(
    Guid WorkSummaryId,
    Guid WorkOrderId,
    Guid ServiceVisitId,
    int RevisionNo,
    string SummaryText,
    string RepairOutcomeCode);

public sealed record NewVisitScheduleRequest(
    Guid? AssignedTeamId,
    Guid? AssignedTechnicianId,
    [property: JsonConverter(typeof(ExplicitOffsetDateTimeOffsetConverter))] DateTimeOffset? ScheduledStartAt,
    [property: JsonConverter(typeof(ExplicitOffsetDateTimeOffsetConverter))] DateTimeOffset? ScheduledEndAt);

/// <summary>Focused response projections; the EF entities are never serialized.</summary>
public static class WorkOrderResponses
{
    public static WorkOrderResponse ToResponse(WorkOrderDto dto) =>
        new(
            dto.WorkOrderId,
            dto.WorkOrderNo,
            WorkOrderStatusCodes.ToCode(dto.Status),
            dto.RepairRequestId,
            dto.RepairRequestNo,
            dto.CustomerCode,
            dto.SiteCode,
            dto.EquipmentCode,
            Convert.ToBase64String(dto.RowVersion),
            dto.Visits.Select(ServiceVisitResponses.ToResponse).ToList());
}

public static class WorkSessionResponses
{
    public static WorkSessionResponse ToResponse(WorkSessionDto dto) =>
        new(
            dto.WorkSessionId,
            dto.ServiceVisitId,
            dto.WorkOrderId,
            dto.WorkOrderNo,
            dto.SiteCode,
            dto.EquipmentCode,
            WorkSessionStatusCodes.ToCode(dto.Status),
            dto.CheckInAt,
            dto.PauseStartAt,
            dto.ResumeAt,
            dto.CheckOutAt,
            dto.Pauses.Select(pause => new WorkSessionPauseResponse(pause.WorkSessionPauseId, pause.PausedAt, pause.PauseReason, pause.ResumedAt)).ToList(),
            Convert.ToBase64String(dto.RowVersion),
            Convert.ToBase64String(dto.WorkOrderRowVersion));
}

public static class WorkSummaryResponses
{
    public static WorkSummaryResponse ToResponse(WorkSummaryDto dto) =>
        new(dto.WorkSummaryId, dto.WorkOrderId, dto.ServiceVisitId, dto.RevisionNo, dto.SummaryText, RepairOutcomeCodes.ToCode(dto.RepairOutcomeCode));
}

public static class MyVisitSummaryResponses
{
    public static MyVisitSummaryResponse ToResponse(MyVisitSummaryDto dto) =>
        new(
            dto.ServiceVisitId,
            dto.WorkOrderId,
            dto.WorkOrderNo,
            ServiceVisitStatusCodes.ToCode(dto.Status),
            dto.SiteCode,
            dto.EquipmentCode,
            dto.ScheduledStartAt,
            dto.ScheduledEndAt,
            Convert.ToBase64String(dto.RowVersion));
}

public static class ServiceVisitResponses
{
    public static ServiceVisitResponse ToResponse(ServiceVisitDto dto) =>
        new(
            dto.ServiceVisitId,
            dto.WorkOrderId,
            ServiceVisitTypeCodes.ToCode(dto.VisitType),
            ServiceVisitStatusCodes.ToCode(dto.Status),
            dto.AssignedTeamId,
            dto.AssignedTechnicianId,
            dto.ScheduledStartAt,
            dto.ScheduledEndAt,
            dto.RescheduleReason,
            dto.ReassignReason,
            dto.CancelReason,
            dto.MissedReason,
            dto.CompletedAt,
            dto.SourceMissedVisitId,
            dto.MissedDecisionCode is { } decision ? MissedVisitDecisionCodes.ToCode(decision) : null,
            dto.MissedDecidedAt,
            Convert.ToBase64String(dto.RowVersion));
}
