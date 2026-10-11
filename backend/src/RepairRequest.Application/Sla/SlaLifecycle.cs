using RepairRequest.Application.Common;
using RepairRequest.Domain.Auditing;
using RepairRequest.Domain.Sla;

namespace RepairRequest.Application.Sla;

/// <summary>
/// The two SLA steps a lifecycle command performs inside its own transaction. Both use the command's single authoritative
/// time so the transition, the SLA change and the SLA audit agree.
/// </summary>
public static class SlaLifecycle
{
    /// <summary>
    /// First Submit: stages the record (started at the persisted <c>submitted_at</c>) and its audit row. Called only for a
    /// first Submit, never for a Resubmit; the caller's single save persists everything atomically.
    /// </summary>
    public static void Start(
        ISlaRecordStore store, CommandContext context, Guid tenantId, Guid repairRequestId, DateTime submittedAt)
    {
        var record = SlaRecord.Start(tenantId, repairRequestId, submittedAt);
        store.StageStart(record, SlaAudit.Started(context, record, record.StartedAt));
    }

    /// <summary>
    /// Stops the Repair Request's SLA record, once. Zero affected rows (legacy, never submitted, already stopped) is a
    /// successful no-op without an audit row; exactly one stopped row stages exactly one <c>SLA_STOPPED</c> audit row through
    /// <paramref name="addAudit"/>. Call it after every guard has passed and immediately before the lifecycle save, so a
    /// failed or conflicting save rolls the stop back with the transaction.
    /// </summary>
    public static async Task StopAsync(
        ISlaRecordStore store,
        Action<AuditHistory> addAudit,
        CommandContext context,
        Guid tenantId,
        Guid repairRequestId,
        SlaStopReason reason,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var stoppedId = await store.StopAsync(tenantId, repairRequestId, reason, now, cancellationToken);
        if (stoppedId is { } slaRecordId)
        {
            addAudit(SlaAudit.Stopped(context, tenantId, slaRecordId, repairRequestId, reason, now));
        }
    }
}
