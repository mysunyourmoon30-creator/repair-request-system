using RepairRequest.Application.Security;
using RepairRequest.Application.Sla;
using RepairRequest.Domain.Security;
using RepairRequest.Domain.Sla;

namespace RepairRequest.Application.Tests.Sla;

/// <summary>SLA-API-001 read mapping (`docs/16` D7): ACTIVE, STOPPED, NOT_TRACKED_LEGACY and the non-leaking null.</summary>
public class SlaRecordReadServiceTests
{
    private static readonly DateTime Start = new(2026, 10, 11, 8, 0, 0, DateTimeKind.Utc);

    private readonly FakeSlaRecordStore _store = new();
    private readonly SlaRecordReadService _service;
    private readonly CurrentUser _user = new(Guid.NewGuid(), Guid.NewGuid(), [RoleCodes.Requester]);
    private readonly Guid _requestId = Guid.NewGuid();

    public SlaRecordReadServiceTests()
    {
        _service = new SlaRecordReadService(_store);
    }

    [Fact]
    public async Task Get_WithARecordAndNoStop_IsActive_WithPolicyDueAndRisk()
    {
        _store.ReadModel = new SlaReadModel(_requestId, "SLA-004/v1", Start, Start.AddHours(24), Start.AddHours(20), null, null);

        var dto = await _service.GetAsync(_user, _requestId, CancellationToken.None);

        Assert.NotNull(dto);
        Assert.Equal(SlaTrackingStatus.Active, dto.TrackingStatus);
        Assert.Equal(_requestId, dto.RepairRequestId);
        Assert.Equal("SLA-004/v1", dto.SlaPolicyVersion);
        Assert.Equal(Start, dto.StartedAt);
        Assert.Equal(Start.AddHours(24), dto.DueAt);
        Assert.Equal(Start.AddHours(20), dto.RiskAt);
        Assert.Null(dto.StoppedAt);
        Assert.Null(dto.StoppedReason);
        Assert.Same(_user, _store.LastReadUser);
    }

    [Theory]
    [InlineData(SlaStopReason.RequestRejected)]
    [InlineData(SlaStopReason.RequestCancelled)]
    [InlineData(SlaStopReason.WoClosed)]
    [InlineData(SlaStopReason.WoCancelled)]
    public async Task Get_WithBothStopValues_IsStopped_WithTheStopReason(SlaStopReason reason)
    {
        var stoppedAt = Start.AddHours(3);
        _store.ReadModel = new SlaReadModel(_requestId, "SLA-004/v1", Start, Start.AddHours(24), Start.AddHours(20), stoppedAt, reason);

        var dto = await _service.GetAsync(_user, _requestId, CancellationToken.None);

        Assert.NotNull(dto);
        Assert.Equal(SlaTrackingStatus.Stopped, dto.TrackingStatus);
        Assert.Equal(stoppedAt, dto.StoppedAt);
        Assert.Equal(reason, dto.StoppedReason);
        Assert.Equal(Start.AddHours(24), dto.DueAt);
    }

    [Fact]
    public async Task Get_ForAPreviouslySubmittedRequestWithoutARecord_IsNotTrackedLegacy_WithNoSlaFields()
    {
        _store.ReadModel = new SlaReadModel(_requestId, null, null, null, null, null, null);

        var dto = await _service.GetAsync(_user, _requestId, CancellationToken.None);

        Assert.NotNull(dto);
        Assert.Equal(SlaTrackingStatus.NotTrackedLegacy, dto.TrackingStatus);
        Assert.Equal(_requestId, dto.RepairRequestId);
        Assert.Null(dto.SlaPolicyVersion);
        Assert.Null(dto.StartedAt);
        Assert.Null(dto.DueAt);
        Assert.Null(dto.RiskAt);
        Assert.Null(dto.StoppedAt);
        Assert.Null(dto.StoppedReason);

        // A legacy read is side-effect free: nothing is started or stopped.
        Assert.Empty(_store.Started);
        Assert.Empty(_store.Stops);
    }

    [Fact]
    public async Task Get_ForAMissingOutOfScopeOrNeverSubmittedRequest_IsNull()
    {
        _store.ReadModel = null;

        Assert.Null(await _service.GetAsync(_user, _requestId, CancellationToken.None));
        Assert.Empty(_store.Started);
        Assert.Empty(_store.Stops);
    }
}
