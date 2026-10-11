using RepairRequest.Domain.Common;

namespace RepairRequest.Domain.Sla;

/// <summary>
/// The SLA record of one Repair Request (`docs/16` D5–D7; `sla_record`). Started once, at the first successful Submit, with
/// <see cref="StartedAt"/> equal to the persisted <c>submitted_at</c>; <see cref="OriginalDueAt"/> and <see cref="RiskAt"/>
/// follow from <see cref="SlaPolicy"/> and are never recalculated. The record stops permanently at the first of Repair
/// Request rejected / cancelled and Work Order closed / cancelled. There is deliberately no stop, edit or restart method on
/// this class: a stop is one server-side conditional update (<c>stopped_at IS NULL</c>) so the first stop wins atomically,
/// and a stopped record is never edited again. Instances therefore expose no mutators.
/// </summary>
public sealed class SlaRecord
{
    /// <summary>EF Core materialization.</summary>
    private SlaRecord()
    {
        SlaPolicyVersion = null!;
    }

    private SlaRecord(Guid tenantId, Guid repairRequestId, DateTime startedAt)
    {
        Id = Guid.CreateVersion7();
        TenantId = DomainGuard.NotEmpty(tenantId, nameof(tenantId));
        RepairRequestId = DomainGuard.NotEmpty(repairRequestId, nameof(repairRequestId));
        SlaPolicyVersion = SlaPolicy.Version;
        StartedAt = Normalize(DomainGuard.Utc(startedAt, nameof(startedAt)));
        OriginalDueAt = SlaPolicy.DueAt(StartedAt);
        RiskAt = SlaPolicy.RiskAt(OriginalDueAt);
    }

    /// <summary>
    /// Starts the record at <paramref name="startedAt"/> (the persisted <c>submitted_at</c>, UTC). The value is truncated to
    /// the <c>datetime2(3)</c> precision of the database so the due and risk times stay exactly 24 h / 4 h apart after a
    /// round trip.
    /// </summary>
    public static SlaRecord Start(Guid tenantId, Guid repairRequestId, DateTime startedAt) =>
        new(tenantId, repairRequestId, startedAt);

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid RepairRequestId { get; private set; }

    public string SlaPolicyVersion { get; private set; }

    public DateTime StartedAt { get; private set; }

    /// <summary>The API's <c>dueAt</c>.</summary>
    public DateTime OriginalDueAt { get; private set; }

    public DateTime RiskAt { get; private set; }

    public DateTime? StoppedAt { get; private set; }

    public SlaStopReason? StopReason { get; private set; }

    public bool IsStopped => StoppedAt is not null;

    private static DateTime Normalize(DateTime value) =>
        new(value.Ticks - (value.Ticks % TimeSpan.TicksPerMillisecond), DateTimeKind.Utc);
}
