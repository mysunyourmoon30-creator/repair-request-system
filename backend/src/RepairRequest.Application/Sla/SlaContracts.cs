using RepairRequest.Domain.Sla;

namespace RepairRequest.Application.Sla;

/// <summary>
/// What the store reads for SLA-API-001: the in-scope, previously submitted Repair Request and, when one exists, its SLA
/// columns. <see cref="StartedAt"/> is null exactly when no record exists (a legacy request).
/// </summary>
public sealed record SlaReadModel(
    Guid RepairRequestId,
    string? SlaPolicyVersion,
    DateTime? StartedAt,
    DateTime? OriginalDueAt,
    DateTime? RiskAt,
    DateTime? StoppedAt,
    SlaStopReason? StopReason);

/// <summary>`docs/16` D7. There is no breach or risk classification: breach evaluation is out of scope.</summary>
public enum SlaTrackingStatus
{
    Active,
    Stopped,
    NotTrackedLegacy
}

/// <summary>SLA-API-001 result. For <see cref="SlaTrackingStatus.NotTrackedLegacy"/> every SLA field except the id is null.</summary>
public sealed record SlaRecordDto(
    Guid RepairRequestId,
    SlaTrackingStatus TrackingStatus,
    string? SlaPolicyVersion,
    DateTime? StartedAt,
    DateTime? DueAt,
    DateTime? RiskAt,
    DateTime? StoppedAt,
    SlaStopReason? StoppedReason);
