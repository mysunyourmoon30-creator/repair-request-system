using RepairRequest.Application.Security;
using RepairRequest.Domain.Auditing;
using RepairRequest.Domain.Sla;

namespace RepairRequest.Application.Sla;

/// <summary>
/// Persistence port of the SLA record (`docs/16` D5–D7). Start and stop share the transaction and the unit of work of the
/// lifecycle command that calls them; this port never begins or commits a transaction of its own.
/// </summary>
public interface ISlaRecordStore
{
    /// <summary>
    /// Stages the new record and its <c>SLA_STARTED</c> audit row in the caller's unit of work; they are persisted by the
    /// caller's single save together with the first Submit, or not at all.
    /// </summary>
    void StageStart(SlaRecord record, AuditHistory audit);

    /// <summary>
    /// Stops the record of the Repair Request with one server-side conditional update (<c>stopped_at IS NULL</c>), inside the
    /// caller's transaction. Returns the id of the record this call stopped, or null when nothing was stopped (legacy request
    /// without a record, never-submitted draft, or already stopped — all successful no-ops). The first stop reason and time
    /// are never overwritten.
    /// </summary>
    Task<Guid?> StopAsync(Guid tenantId, Guid repairRequestId, SlaStopReason reason, DateTime stoppedAt, CancellationToken cancellationToken);

    /// <summary>
    /// SLA-API-001 read: the caller's in-scope Repair Request with its record, or null when the request is missing, out of
    /// scope or has never been submitted. Never writes.
    /// </summary>
    Task<SlaReadModel?> FindForReadAsync(CurrentUser user, Guid repairRequestId, CancellationToken cancellationToken);
}
