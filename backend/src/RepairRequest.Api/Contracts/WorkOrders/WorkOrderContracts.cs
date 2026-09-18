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
