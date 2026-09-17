using RepairRequest.Application.Common;
using RepairRequest.Application.RepairRequests;
using RepairRequest.Application.Security;
using RepairRequest.Application.Tests.MasterData;
using RepairRequest.Domain.Auditing;
using RepairRequest.Domain.RepairRequests;
using RepairRequest.Domain.Security;
using RepairRequestAggregate = RepairRequest.Domain.RepairRequests.RepairRequest;

namespace RepairRequest.Application.Tests.RepairRequests;

/// <summary>
/// S1-009 Cancel rules (ST-RR-007; UC-RR-004): order 404 not own / out of scope -> 409 stale -> 409 state -> 422 reason;
/// success moves the request to CANCELLED with the trimmed reason and writes one audit from the actual source state;
/// nothing is written on any failure.
/// </summary>
public class RepairRequestCancelServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 11, 0, 0, 456, TimeSpan.Zero);

    private readonly FakeRepairRequestCancelStore _store = new();
    private readonly RepairRequestCancelService _service;
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _owner = Guid.NewGuid();

    public RepairRequestCancelServiceTests()
    {
        _service = new RepairRequestCancelService(_store, new FixedClock(Now));
    }

    private CommandContext Owner() => new(new CurrentUser(_owner, _tenantId, [RoleCodes.Requester]), Guid.NewGuid());

    private Task<CommandResult<RepairRequestDraftDto>> CancelAsync(CommandContext caller, RepairRequestAggregate request, string? reason = "No longer needed", byte[]? rowVersion = null) =>
        _service.CancelAsync(caller, request.Id, rowVersion ?? FakeRepairRequestCancelStore.InitialRowVersion, reason, CancellationToken.None);

    private void AssertNothingWritten(RepairRequestAggregate request, RepairRequestStatus status)
    {
        Assert.Equal(status, request.Status);
        Assert.Null(request.CancelReason);
        Assert.Empty(_store.Audits);
        Assert.Equal(0, _store.Commits);
    }

    [Theory]
    [InlineData(RepairRequestStatus.Draft)]
    [InlineData(RepairRequestStatus.Submitted)]
    [InlineData(RepairRequestStatus.UnderReview)]
    [InlineData(RepairRequestStatus.Approved)]
    public async Task Cancel_ByTheOwner_FromAnAllowedState_Cancels_AndAuditsTheSourceState(RepairRequestStatus status)
    {
        var request = _store.Seed(_tenantId, _owner, status);
        var caller = Owner();

        var result = await CancelAsync(caller, request, reason: "  No longer needed  ");

        Assert.True(result.Succeeded);
        Assert.Equal(RepairRequestStatus.Cancelled, result.Value!.Status);
        Assert.Equal(FakeRepairRequestCancelStore.CancelledRowVersion, result.Value.RowVersion);
        Assert.Equal("No longer needed", request.CancelReason);

        var audit = Assert.Single(_store.Audits);
        Assert.Equal(RepairRequestAudit.CancelledAction, audit.ActionCode);
        Assert.Equal(RepairRequestStatusCodes.ToCode(status), audit.FromState);
        Assert.Equal("CANCELLED", audit.ToState);
        Assert.Equal("No longer needed", audit.Reason);
        Assert.Equal(_owner, audit.ActorId);
        Assert.Equal(caller.CorrelationId, audit.CorrelationId);
        Assert.Equal(Now.UtcDateTime.AddTicks(-(Now.UtcDateTime.Ticks % TimeSpan.TicksPerMillisecond)), audit.OccurredAt);
        Assert.Equal(1, _store.Commits);
    }

    [Fact]
    public async Task Cancel_AcceptsAReasonOfExactlyTheMaximumLength()
    {
        var request = _store.Seed(_tenantId, _owner, RepairRequestStatus.Submitted);
        var reason = new string('R', RepairRequestAggregate.ReasonMaxLength);

        Assert.True((await CancelAsync(Owner(), request, reason)).Succeeded);
        Assert.Equal(reason, request.CancelReason);
    }

    [Fact]
    public async Task RequestOfAnotherUser_OrOutOfScope_OrUnknown_IsNotFound()
    {
        var otherOwners = _store.Seed(_tenantId, Guid.NewGuid(), RepairRequestStatus.Submitted);
        var outOfScope = _store.Seed(_tenantId, _owner, RepairRequestStatus.Submitted);
        _store.OutOfScope.Add(outOfScope.Id);
        var unknown = RepairRequestAggregate.CreateDraft(_tenantId, _owner);

        Assert.Equal(CommandFailure.NotFound, (await CancelAsync(Owner(), otherOwners)).Error?.Failure);
        Assert.Equal(CommandFailure.NotFound, (await CancelAsync(Owner(), outOfScope)).Error?.Failure);
        Assert.Equal(CommandFailure.NotFound, (await CancelAsync(Owner(), unknown)).Error?.Failure);
        AssertNothingWritten(otherOwners, RepairRequestStatus.Submitted);
        AssertNothingWritten(outOfScope, RepairRequestStatus.Submitted);
    }

    [Fact]
    public async Task StaleRowVersion_IsConcurrencyConflict()
    {
        var request = _store.Seed(_tenantId, _owner, RepairRequestStatus.UnderReview);

        var result = await CancelAsync(Owner(), request, rowVersion: [9, 9, 9, 9, 9, 9, 9, 9]);

        Assert.Equal(CommandFailure.ConcurrencyConflict, result.Error?.Failure);
        AssertNothingWritten(request, RepairRequestStatus.UnderReview);
    }

    [Theory]
    [InlineData(RepairRequestStatus.Rejected)]
    [InlineData(RepairRequestStatus.Cancelled)]
    [InlineData(RepairRequestStatus.Converted)]
    public async Task RejectedCancelledOrConvertedRequest_IsStateConflict(RepairRequestStatus status)
    {
        var request = _store.Seed(_tenantId, _owner, status);

        var result = await CancelAsync(Owner(), request);

        Assert.Equal(CommandFailure.StateConflict, result.Error?.Failure);
        AssertNothingWritten(request, status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("    ")]
    public async Task Cancel_WithoutAReason_IsValidationFailedOnReason(string? reason)
    {
        var request = _store.Seed(_tenantId, _owner, RepairRequestStatus.Draft);

        var result = await CancelAsync(Owner(), request, reason);

        Assert.Equal(CommandFailure.ValidationFailed, result.Error?.Failure);
        Assert.True(result.Error!.Errors.ContainsKey(RepairRequestFields.Reason));
        AssertNothingWritten(request, RepairRequestStatus.Draft);
    }

    [Fact]
    public async Task Cancel_WithATooLongReason_IsValidationFailedOnReason()
    {
        var request = _store.Seed(_tenantId, _owner, RepairRequestStatus.Approved);

        var result = await CancelAsync(Owner(), request, new string('R', RepairRequestAggregate.ReasonMaxLength + 1));

        Assert.Equal(CommandFailure.ValidationFailed, result.Error?.Failure);
        AssertNothingWritten(request, RepairRequestStatus.Approved);
    }

    [Fact]
    public async Task LockNotGranted_IsConcurrencyConflict_AndWritesNothing()
    {
        var request = _store.Seed(_tenantId, _owner, RepairRequestStatus.UnderReview);
        _store.LockUnavailable = true;

        Assert.Equal(CommandFailure.ConcurrencyConflict, (await CancelAsync(Owner(), request)).Error?.Failure);
        AssertNothingWritten(request, RepairRequestStatus.UnderReview);
    }

    [Fact]
    public async Task SaveConflict_IsConcurrencyConflict_AndCommitsNothing()
    {
        var request = _store.Seed(_tenantId, _owner, RepairRequestStatus.Submitted);
        _store.SaveOutcome = RepairRequestSaveOutcome.ConcurrencyConflict;

        Assert.Equal(CommandFailure.ConcurrencyConflict, (await CancelAsync(Owner(), request)).Error?.Failure);
        Assert.Empty(_store.Audits);
        Assert.Equal(0, _store.Commits);
    }
}

/// <summary>In-memory Cancel port: owner + scope lookup, lock availability and a row-version-checked save.</summary>
internal sealed class FakeRepairRequestCancelStore : IRepairRequestCancelStore
{
    public static readonly byte[] InitialRowVersion = [0, 0, 0, 0, 0, 0, 0, 1];
    public static readonly byte[] CancelledRowVersion = [0, 0, 0, 0, 0, 0, 0, 2];

    private readonly List<AuditHistory> _pendingAudits = [];

    public List<RepairRequestAggregate> Requests { get; } = [];

    public List<AuditHistory> Audits { get; } = [];

    public HashSet<Guid> OutOfScope { get; } = [];

    public bool LockUnavailable { get; set; }

    public RepairRequestSaveOutcome SaveOutcome { get; set; } = RepairRequestSaveOutcome.Saved;

    public int Commits { get; private set; }

    private static void Set<T>(T target, string property, object? value) =>
        typeof(T).GetProperty(property)!.SetValue(target, value);

    public RepairRequestAggregate Seed(Guid tenantId, Guid owner, RepairRequestStatus status)
    {
        var start = new DateTime(2026, 9, 20, 8, 0, 0, DateTimeKind.Utc);
        var request = RepairRequestAggregate.CreateDraft(tenantId, owner);
        if (status != RepairRequestStatus.Draft)
        {
            request.EditDraft(Guid.NewGuid(), null, "ELECTRICAL", "HIGH", Guid.NewGuid(), "Pump leaking", start, start.AddHours(2));
            request.Submit("RR-2026-000001", owner, new DateTime(2026, 9, 15, 8, 0, 0, DateTimeKind.Utc), null);
            Set(request, nameof(RepairRequestAggregate.Status), status);
        }

        Set(request, nameof(RepairRequestAggregate.Id), Guid.NewGuid());
        Set(request, nameof(RepairRequestAggregate.RowVersion), InitialRowVersion);
        Requests.Add(request);
        return request;
    }

    public async Task<CommandResult<T>> RunInTransactionAsync<T>(Func<Task<CommandResult<T>>> command, CancellationToken cancellationToken)
    {
        if (LockUnavailable)
        {
            return CommandError.ConcurrencyConflict;
        }

        var result = await command();
        if (result.Succeeded)
        {
            Commits++;
        }

        _pendingAudits.Clear();
        return result;
    }

    public Task<RepairRequestAggregate?> LockOwnForCancelAsync(CurrentUser user, Guid repairRequestId, CancellationToken cancellationToken) =>
        Task.FromResult(Requests.SingleOrDefault(request =>
            request.Id == repairRequestId
            && request.TenantId == user.TenantId
            && request.CreatedBy == user.UserId
            && !OutOfScope.Contains(request.Id)));

    public void AddAudit(AuditHistory audit) => _pendingAudits.Add(audit);

    public Task<RepairRequestSaveOutcome> SaveChangesAsync(RepairRequestAggregate request, byte[] expectedRowVersion, CancellationToken cancellationToken)
    {
        if (SaveOutcome != RepairRequestSaveOutcome.Saved || !request.RowVersion.AsSpan().SequenceEqual(expectedRowVersion))
        {
            _pendingAudits.Clear();
            return Task.FromResult(RepairRequestSaveOutcome.ConcurrencyConflict);
        }

        Audits.AddRange(_pendingAudits);
        _pendingAudits.Clear();
        Set(request, nameof(RepairRequestAggregate.RowVersion), CancelledRowVersion);
        return Task.FromResult(RepairRequestSaveOutcome.Saved);
    }
}
