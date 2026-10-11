using System.Text.Json;
using RepairRequest.Application.Common;
using RepairRequest.Domain.Auditing;
using RepairRequest.Domain.Sla;

namespace RepairRequest.Application.Sla;

/// <summary>
/// SLA start and stop audit records (`docs/16` D5–D7; TC-SLA-001 "SLA event audit"). The actor is always System
/// (<see cref="SystemActors.SlaTracking"/>); the user whose command triggered the change is kept by the triggering lifecycle
/// audit row and the shared <see cref="CommandContext.CorrelationId"/>. Values carry identifiers, codes and timestamps only.
/// </summary>
public static class SlaAudit
{
    public const string EntityType = "SLA_RECORD";
    public const string StartedAction = "SLA_STARTED";
    public const string StoppedAction = "SLA_STOPPED";

    public const string RepairRequestIdField = "repairRequestId";
    public const string PolicyVersionField = "policyVersion";
    public const string StartedAtField = "startedAt";
    public const string DueAtField = "dueAt";
    public const string RiskAtField = "riskAt";
    public const string StoppedAtField = "stoppedAt";
    public const string StoppedReasonField = "stoppedReason";

    public static AuditHistory Started(CommandContext context, SlaRecord record, DateTime occurredAt) =>
        new(
            record.TenantId,
            EntityType,
            record.Id,
            StartedAction,
            fromState: null,
            toState: null,
            oldValueJson: null,
            newValueJson: JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                [RepairRequestIdField] = record.RepairRequestId,
                [PolicyVersionField] = record.SlaPolicyVersion,
                [StartedAtField] = record.StartedAt,
                [DueAtField] = record.OriginalDueAt,
                [RiskAtField] = record.RiskAt
            }),
            reason: null,
            SystemActors.SlaTracking,
            occurredAt,
            context.CorrelationId);

    public static AuditHistory Stopped(
        CommandContext context,
        Guid tenantId,
        Guid slaRecordId,
        Guid repairRequestId,
        SlaStopReason reason,
        DateTime stoppedAt) =>
        new(
            tenantId,
            EntityType,
            slaRecordId,
            StoppedAction,
            fromState: null,
            toState: null,
            oldValueJson: null,
            newValueJson: JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                [RepairRequestIdField] = repairRequestId,
                [StoppedAtField] = stoppedAt,
                [StoppedReasonField] = SlaStopReasonCodes.ToCode(reason)
            }),
            reason: null,
            SystemActors.SlaTracking,
            stoppedAt,
            context.CorrelationId);
}
