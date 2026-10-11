using RepairRequest.Application.Security;

namespace RepairRequest.Application.Sla;

/// <summary>
/// SLA-API-001 (UC-SLA-001/002 read): the SLA state of one Repair Request. Role capability is the API policy's; tenant, Site
/// and Requester-ownership scope are the store's (<c>IDataScope.RepairRequests</c>), so a missing, out-of-scope and
/// never-submitted request are indistinguishable (null). A previously submitted request without a record is
/// <see cref="SlaTrackingStatus.NotTrackedLegacy"/>: not breached, no notification, and never backfilled by a read.
/// </summary>
public sealed class SlaRecordReadService
{
    private readonly ISlaRecordStore _store;

    public SlaRecordReadService(ISlaRecordStore store)
    {
        _store = store;
    }

    public async Task<SlaRecordDto?> GetAsync(CurrentUser user, Guid repairRequestId, CancellationToken cancellationToken)
    {
        var model = await _store.FindForReadAsync(user, repairRequestId, cancellationToken);
        if (model is null)
        {
            return null;
        }

        if (model.StartedAt is null)
        {
            return new SlaRecordDto(model.RepairRequestId, SlaTrackingStatus.NotTrackedLegacy, null, null, null, null, null, null);
        }

        var stopped = model.StoppedAt is not null && model.StopReason is not null;
        return new SlaRecordDto(
            model.RepairRequestId,
            stopped ? SlaTrackingStatus.Stopped : SlaTrackingStatus.Active,
            model.SlaPolicyVersion,
            model.StartedAt,
            model.OriginalDueAt,
            model.RiskAt,
            model.StoppedAt,
            model.StopReason);
    }
}
