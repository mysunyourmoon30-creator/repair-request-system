using RepairRequest.Application.Security;
using RepairRequest.Application.Sla;
using RepairRequest.Domain.Auditing;
using RepairRequest.Domain.Sla;

namespace RepairRequest.Application.Tests.Sla;

/// <summary>
/// In-memory SLA port. By default a request has no SLA record, so a stop is the successful no-op of a legacy or never-submitted
/// request (nothing stopped, no audit). <see cref="StopResult"/> models a record that the call stops.
/// </summary>
public sealed class FakeSlaRecordStore : ISlaRecordStore
{
    public sealed record StopCall(Guid TenantId, Guid RepairRequestId, SlaStopReason Reason, DateTime StoppedAt);

    public List<(SlaRecord Record, AuditHistory Audit)> Started { get; } = [];

    public List<StopCall> Stops { get; } = [];

    /// <summary>The id returned by <see cref="StopAsync"/>; null models "nothing stopped" (legacy, draft or already stopped).</summary>
    public Guid? StopResult { get; set; }

    public SlaReadModel? ReadModel { get; set; }

    public CurrentUser? LastReadUser { get; private set; }

    public void StageStart(SlaRecord record, AuditHistory audit) => Started.Add((record, audit));

    public Task<Guid?> StopAsync(Guid tenantId, Guid repairRequestId, SlaStopReason reason, DateTime stoppedAt, CancellationToken cancellationToken)
    {
        Stops.Add(new StopCall(tenantId, repairRequestId, reason, stoppedAt));
        return Task.FromResult(StopResult);
    }

    public Task<SlaReadModel?> FindForReadAsync(CurrentUser user, Guid repairRequestId, CancellationToken cancellationToken)
    {
        LastReadUser = user;
        return Task.FromResult(ReadModel);
    }
}
