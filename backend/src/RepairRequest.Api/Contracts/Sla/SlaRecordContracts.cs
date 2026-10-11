using RepairRequest.Application.Sla;
using RepairRequest.Domain.Sla;

namespace RepairRequest.Api.Contracts.Sla;

/// <summary>
/// SLA-API-001 response (`docs/16` D5-D7; `docs/13` SLA-API-001). <c>trackingStatus</c> is exactly <c>ACTIVE</c>,
/// <c>STOPPED</c> or <c>NOT_TRACKED_LEGACY</c>; all times are UTC. <c>dueAt</c> is the record's original due time. There is
/// deliberately no breach or risk classification field. For <c>NOT_TRACKED_LEGACY</c> every SLA field is null.
/// </summary>
public sealed record SlaRecordResponse(
    Guid RepairRequestId,
    string TrackingStatus,
    string? SlaPolicyVersion,
    DateTime? StartedAt,
    DateTime? DueAt,
    DateTime? RiskAt,
    DateTime? StoppedAt,
    string? StoppedReason);

public static class SlaRecordResponses
{
    public static SlaRecordResponse ToResponse(SlaRecordDto dto) =>
        new(
            dto.RepairRequestId,
            TrackingStatus(dto.TrackingStatus),
            dto.SlaPolicyVersion,
            dto.StartedAt,
            dto.DueAt,
            dto.RiskAt,
            dto.StoppedAt,
            dto.StoppedReason is { } reason ? SlaStopReasonCodes.ToCode(reason) : null);

    private static string TrackingStatus(SlaTrackingStatus status) => status switch
    {
        SlaTrackingStatus.Active => "ACTIVE",
        SlaTrackingStatus.Stopped => "STOPPED",
        SlaTrackingStatus.NotTrackedLegacy => "NOT_TRACKED_LEGACY",
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };
}
