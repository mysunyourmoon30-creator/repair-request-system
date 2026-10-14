using RepairRequest.Application.Common;
using RepairRequest.Application.Security;
using RepairRequest.Application.Tests.MasterData;
using RepairRequest.Application.WorkOrders;
using RepairRequest.Domain.Auditing;
using RepairRequest.Domain.Security;
using RepairRequest.Domain.WorkOrders;

namespace RepairRequest.Application.Tests.WorkOrders;

/// <summary>In-memory Time Correction port: staged rows become committed only when the command succeeds, like the SQL transaction.</summary>
internal sealed class FakeTimeCorrectionStore : ITimeCorrectionStore
{
    private readonly List<TimeCorrection> _staged = [];
    private readonly List<AuditHistory> _stagedAudits = [];

    public TimeCorrectionRequestContext? RequestContext { get; set; }

    public TimeCorrectionApprovalContext? ApprovalContext { get; set; }

    public TimeCorrection? RejectTarget { get; set; }

    public TimeCorrectionInsertOutcome InsertOutcome { get; set; } = TimeCorrectionInsertOutcome.Saved;

    public WorkOrderSaveOutcome SaveOutcome { get; set; } = WorkOrderSaveOutcome.Saved;

    public List<TimeCorrection> Committed { get; } = [];

    public List<AuditHistory> CommittedAudits { get; } = [];

    public int SavedApprovals { get; private set; }

    public int SavedRejections { get; private set; }

    public async Task<CommandResult<T>> RunInTransactionAsync<T>(Func<Task<CommandResult<T>>> command, CancellationToken cancellationToken)
    {
        var result = await command();
        if (result.Succeeded)
        {
            Committed.AddRange(_staged);
            CommittedAudits.AddRange(_stagedAudits);
        }

        _staged.Clear();
        _stagedAudits.Clear();
        return result;
    }

    public Task<TimeCorrectionRequestContext?> LoadForRequestAsync(CurrentUser user, Guid workSessionId, CancellationToken cancellationToken) =>
        Task.FromResult(RequestContext);

    public void Add(TimeCorrection correction)
    {
        typeof(TimeCorrection).GetProperty(nameof(TimeCorrection.Id))!.SetValue(correction, Guid.NewGuid());
        _staged.Add(correction);
    }

    public void AddAudit(AuditHistory audit) => _stagedAudits.Add(audit);

    public Task<TimeCorrectionInsertOutcome> SaveNewAsync(CancellationToken cancellationToken) => Task.FromResult(InsertOutcome);

    public Task<TimeCorrection?> LoadForRejectAsync(CurrentUser user, Guid timeCorrectionId, CancellationToken cancellationToken) =>
        Task.FromResult(RejectTarget);

    public Task<TimeCorrectionApprovalContext?> LoadForApproveAsync(CurrentUser user, Guid timeCorrectionId, CancellationToken cancellationToken) =>
        Task.FromResult(ApprovalContext);

    public Task<WorkOrderSaveOutcome> SaveRejectionAsync(TimeCorrection correction, byte[] expectedRowVersion, CancellationToken cancellationToken)
    {
        SavedRejections++;
        return Task.FromResult(SaveOutcome);
    }

    public Task<WorkOrderSaveOutcome> SaveApprovalAsync(
        TimeCorrection correction, WorkSession session, byte[] expectedRowVersion, CancellationToken cancellationToken)
    {
        SavedApprovals++;
        return Task.FromResult(SaveOutcome);
    }

    public Task<TimeCorrectionDto?> GetAsync(CurrentUser user, Guid timeCorrectionId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<PagedResult<TimeCorrectionDto>> ListPendingAsync(CurrentUser user, PendingTimeCorrectionsQuery query, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

/// <summary>
/// Request, Approve and Reject of a Time Correction against in-memory ports (`docs/17`): the check order, the exact pause target, the Work
/// Order guard (Reject stays possible), no self-decision, stale value, chronology at request and approval, the conditional Visit completion
/// synchronization, the audit payload (session id, pause id) and that SLA is never involved.
/// </summary>
public class TimeCorrectionServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTime T0 = new(2026, 10, 12, 8, 0, 0, DateTimeKind.Utc);

    private static DateTime At(int minutes) => T0.AddMinutes(minutes);

    private readonly FakeTimeCorrectionStore _store = new();
    private readonly TimeCorrectionService _service;
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly CommandContext _teamLead;
    private readonly CommandContext _supervisor;
    private readonly byte[] _version = [1, 2, 3, 4, 5, 6, 7, 8];

    public TimeCorrectionServiceTests()
    {
        _service = new TimeCorrectionService(_store, new FixedClock(Now));
        _teamLead = new CommandContext(new CurrentUser(Guid.NewGuid(), _tenantId, [RoleCodes.TeamLead]), Guid.NewGuid());
        _supervisor = new CommandContext(new CurrentUser(Guid.NewGuid(), _tenantId, [RoleCodes.Supervisor]), Guid.NewGuid());
    }

    private sealed record World(WorkSession Session, List<WorkSessionPause> Pauses, ServiceVisit Visit);

    /// <summary>A checked-out session: check-in 0, pauses (10..20) and (30..40), check-out 60; the Visit is COMPLETED at the check-out instant.</summary>
    private World CheckedOutWorld()
    {
        var session = WorkSession.Create(_tenantId, Guid.NewGuid(), Guid.NewGuid(), T0);
        typeof(WorkSession).GetProperty(nameof(WorkSession.Id))!.SetValue(session, Guid.NewGuid());
        var pauses = new List<WorkSessionPause>();
        foreach (var (start, end) in new[] { (10, 20), (30, 40) })
        {
            var pause = session.Pause("Waiting for a part", At(start));
            typeof(WorkSessionPause).GetProperty(nameof(WorkSessionPause.Id))!.SetValue(pause, Guid.NewGuid());
            pauses.Add(pause);
            session.Resume(pause, At(end));
        }

        session.CheckOut(At(60));

        var visit = ServiceVisit.Create(_tenantId, Guid.NewGuid(), ServiceVisitType.Initial, Guid.NewGuid(), Guid.NewGuid(), T0, T0.AddHours(2));
        visit.CheckIn();
        visit.CheckOut(At(60));
        return new World(session, pauses, visit);
    }

    private void GivenRequestContext(World world, WorkOrderStatus status = WorkOrderStatus.InProgress) =>
        _store.RequestContext = new TimeCorrectionRequestContext(world.Session, world.Pauses, status);

    private TimeCorrection Pending(World world, TimeCorrectionTimestampType type, DateTime newValue, WorkSessionPause? pause = null, Guid? requestedBy = null)
    {
        var correction = TimeCorrection.Request(
            _tenantId, world.Session.Id, pause?.Id, type, world.Session.CurrentTimestamp(type, pause), newValue, "Clock was wrong.", requestedBy ?? Guid.NewGuid());
        typeof(TimeCorrection).GetProperty(nameof(TimeCorrection.Id))!.SetValue(correction, Guid.NewGuid());
        typeof(TimeCorrection).GetProperty(nameof(TimeCorrection.RowVersion))!.SetValue(correction, _version);
        return correction;
    }

    private void GivenApproval(World world, TimeCorrection correction, WorkOrderStatus status = WorkOrderStatus.InProgress, int sessionCount = 1) =>
        _store.ApprovalContext = new TimeCorrectionApprovalContext(correction, world.Session, world.Pauses, world.Visit, sessionCount, status);

    private Task<CommandResult<Guid>> RequestAsync(
        string? type, Guid? pauseId = null, DateTimeOffset? value = null, string? reason = "Clock was wrong.") =>
        _service.RequestAsync(_teamLead, Guid.NewGuid(), type, pauseId, value, reason, CancellationToken.None);

    // ---------------- Request ----------------

    [Fact]
    public async Task Request_ForAnUnknownOrOutOfScopeSession_Is404()
    {
        var result = await RequestAsync("CHECK_IN", value: Now.AddHours(-5));

        Assert.Equal(CommandFailure.NotFound, result.Error?.Failure);
    }

    [Theory]
    [InlineData(WorkOrderStatus.Closed)]
    [InlineData(WorkOrderStatus.Cancelled)]
    public async Task Request_OnAClosedOrCancelledWorkOrder_Is409_BeforeAnyFieldValidation(WorkOrderStatus status)
    {
        GivenRequestContext(CheckedOutWorld(), status);

        var result = await RequestAsync(type: null, value: null, reason: null); // every field invalid: the state is decided first

        Assert.Equal(CommandFailure.StateConflict, result.Error?.Failure);
        Assert.Empty(_store.Committed);
    }

    [Theory]
    [InlineData(WorkOrderStatus.Open)]
    [InlineData(WorkOrderStatus.Scheduled)]
    [InlineData(WorkOrderStatus.InProgress)]
    [InlineData(WorkOrderStatus.AwaitingSupervisorReview)]
    [InlineData(WorkOrderStatus.AwaitingCustomerAcceptance)]
    [InlineData(WorkOrderStatus.Completed)]
    [InlineData(WorkOrderStatus.CorrectiveActionRequired)]
    [InlineData(WorkOrderStatus.CorrectivePlanPending)]
    [InlineData(WorkOrderStatus.CorrectivePlanApproved)]
    public async Task Request_IsPermittedInEveryStateExceptClosedAndCancelled(WorkOrderStatus status)
    {
        GivenRequestContext(CheckedOutWorld(), status);

        var result = await RequestAsync("CHECK_IN", value: new DateTimeOffset(At(-5)));

        Assert.True(result.Succeeded, result.Error?.Message);
    }

    [Fact]
    public async Task Request_WithAnUnsupportedType_Is422()
    {
        GivenRequestContext(CheckedOutWorld());

        var result = await RequestAsync("CHECK_SIDEWAYS", value: new DateTimeOffset(At(-5)));

        Assert.Equal(CommandFailure.ValidationFailed, result.Error?.Failure);
        Assert.Contains(TimeCorrectionFields.TimestampType, result.Error!.Errors.Keys);
    }

    [Theory]
    [InlineData("PAUSE_START")]
    [InlineData("RESUME")]
    public async Task Request_ForAPauseType_NeedsAPauseOfThisSession_AndRefusesAnUnknownOne(string type)
    {
        var world = CheckedOutWorld();
        GivenRequestContext(world);

        var missing = await RequestAsync(type, pauseId: null, value: new DateTimeOffset(At(12)));
        var unknown = await RequestAsync(type, pauseId: Guid.NewGuid(), value: new DateTimeOffset(At(12)));

        Assert.Contains(TimeCorrectionFields.WorkSessionPauseId, missing.Error!.Errors.Keys);
        Assert.Contains(TimeCorrectionFields.WorkSessionPauseId, unknown.Error!.Errors.Keys);
        Assert.Empty(_store.Committed);
    }

    [Theory]
    [InlineData("CHECK_IN")]
    [InlineData("CHECK_OUT")]
    public async Task Request_ForCheckInOrCheckOut_RefusesAPauseId(string type)
    {
        var world = CheckedOutWorld();
        GivenRequestContext(world);

        var result = await RequestAsync(type, pauseId: world.Pauses[0].Id, value: new DateTimeOffset(At(30)));

        Assert.Contains(TimeCorrectionFields.WorkSessionPauseId, result.Error!.Errors.Keys);
    }

    [Fact]
    public async Task Request_WithoutACheckOut_OrWithAnOpenPauseResume_HasNoValueToCorrect()
    {
        var session = WorkSession.Create(_tenantId, Guid.NewGuid(), Guid.NewGuid(), T0);
        typeof(WorkSession).GetProperty(nameof(WorkSession.Id))!.SetValue(session, Guid.NewGuid());
        var open = session.Pause("Waiting", At(10));
        typeof(WorkSessionPause).GetProperty(nameof(WorkSessionPause.Id))!.SetValue(open, Guid.NewGuid());
        _store.RequestContext = new TimeCorrectionRequestContext(session, [open], WorkOrderStatus.InProgress);

        var noCheckOut = await RequestAsync("CHECK_OUT", value: new DateTimeOffset(At(30)));
        var openResume = await RequestAsync("RESUME", pauseId: open.Id, value: new DateTimeOffset(At(30)));

        Assert.Contains(TimeCorrectionFields.TimestampType, noCheckOut.Error!.Errors.Keys);
        Assert.Contains(TimeCorrectionFields.TimestampType, openResume.Error!.Errors.Keys);
    }

    [Fact]
    public async Task Request_ValidatesTheValueAndTheReason()
    {
        GivenRequestContext(CheckedOutWorld());

        Assert.Contains(TimeCorrectionFields.NewValue, (await RequestAsync("CHECK_IN", value: null)).Error!.Errors.Keys);
        Assert.Contains(TimeCorrectionFields.Reason, (await RequestAsync("CHECK_IN", value: new DateTimeOffset(At(-5)), reason: "   ")).Error!.Errors.Keys);
        Assert.Contains(TimeCorrectionFields.Reason, (await RequestAsync("CHECK_IN", value: new DateTimeOffset(At(-5)), reason: new string('x', 1001))).Error!.Errors.Keys);
    }

    [Fact]
    public async Task Request_WithAValueAfterTheServerUtcNow_Is422_ButTheServerNowItselfIsAllowed()
    {
        var world = CheckedOutWorld();
        GivenRequestContext(world);

        var future = await RequestAsync("CHECK_OUT", value: Now.AddMilliseconds(1));
        Assert.Contains(TimeCorrectionFields.NewValue, future.Error!.Errors.Keys);
        Assert.Contains("future", future.Error.Errors[TimeCorrectionFields.NewValue][0], StringComparison.OrdinalIgnoreCase);

        var equalToNow = await RequestAsync("CHECK_OUT", value: Now);
        Assert.True(equalToNow.Succeeded, equalToNow.Error?.Message);
    }

    [Fact]
    public async Task Request_WithTheCurrentValue_Is422()
    {
        var world = CheckedOutWorld();
        GivenRequestContext(world);

        var result = await RequestAsync("CHECK_IN", value: new DateTimeOffset(T0));

        Assert.Contains(TimeCorrectionFields.NewValue, result.Error!.Errors.Keys);
    }

    [Fact]
    public async Task Request_ThatBreaksTheChronologyOfAnyAffectedInterval_Is422()
    {
        var world = CheckedOutWorld();
        GivenRequestContext(world);

        // Check-in after the first pause started; second resume after the check-out; pause start before the earlier pause ended.
        Assert.Contains(TimeCorrectionFields.NewValue, (await RequestAsync("CHECK_IN", value: new DateTimeOffset(At(15)))).Error!.Errors.Keys);
        Assert.Contains(TimeCorrectionFields.NewValue, (await RequestAsync("RESUME", world.Pauses[1].Id, new DateTimeOffset(At(70)))).Error!.Errors.Keys);
        Assert.Contains(TimeCorrectionFields.NewValue, (await RequestAsync("PAUSE_START", world.Pauses[1].Id, new DateTimeOffset(At(15)))).Error!.Errors.Keys);
        Assert.Empty(_store.Committed);
    }

    [Fact]
    public async Task Request_Succeeds_WithAServerSnapshotOfTheOldValue_ThePauseTarget_AndOneAuditRowCarryingTheSessionAndPauseIds()
    {
        var world = CheckedOutWorld();
        GivenRequestContext(world);

        var result = await RequestAsync("PAUSE_START", world.Pauses[0].Id, new DateTimeOffset(At(12)), "  Technician forgot to press Pause.  ");

        Assert.True(result.Succeeded, result.Error?.Message);
        var correction = Assert.Single(_store.Committed);
        Assert.Equal(world.Pauses[0].Id, correction.WorkSessionPauseId);
        Assert.Equal(At(10), correction.OldValue);
        Assert.Equal(At(12), correction.NewValue);
        Assert.Equal("Technician forgot to press Pause.", correction.Reason);
        Assert.Equal(_teamLead.User.UserId, correction.RequestedBy);
        Assert.Equal(TimeCorrectionStatus.Pending, correction.Status);
        Assert.False(correction.SlaRecalculated);

        var audit = Assert.Single(_store.CommittedAudits);
        Assert.Equal((WorkOrderAudit.TimeCorrectionEntityType, WorkOrderAudit.TimeCorrectionRequestedAction), (audit.EntityType, audit.ActionCode));
        Assert.Equal(correction.Id, audit.EntityId);
        Assert.Equal(_teamLead.CorrelationId, audit.CorrelationId);
        Assert.Contains(world.Session.Id.ToString(), audit.NewValueJson!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(world.Pauses[0].Id.ToString(), audit.NewValueJson!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Technician forgot to press Pause.", audit.Reason);

        // The request changed no time fact.
        Assert.Equal(At(10), world.Pauses[0].PausedAt);
    }

    [Fact]
    public async Task Request_ForTheSameLogicalTargetAsAPendingOne_Is409_AndCommitsNothing()
    {
        var world = CheckedOutWorld();
        GivenRequestContext(world);
        _store.InsertOutcome = TimeCorrectionInsertOutcome.DuplicatePending;

        var result = await RequestAsync("CHECK_IN", value: new DateTimeOffset(At(-5)));

        Assert.Equal(CommandFailure.StateConflict, result.Error?.Failure);
        Assert.Empty(_store.Committed);
        Assert.Empty(_store.CommittedAudits);
    }

    // ---------------- Approve ----------------

    private Task<CommandResult<Guid>> ApproveAsync(byte[]? rowVersion = null, CommandContext? by = null) =>
        _service.ApproveAsync(by ?? _supervisor, Guid.NewGuid(), rowVersion ?? _version, CancellationToken.None);

    [Fact]
    public async Task Approve_ForAnUnknownOrOutOfScopeCorrection_Is404()
    {
        Assert.Equal(CommandFailure.NotFound, (await ApproveAsync()).Error?.Failure);
    }

    [Fact]
    public async Task Approve_ByTheRequester_Is403_BeforeConcurrencyAndState()
    {
        var world = CheckedOutWorld();
        var correction = Pending(world, TimeCorrectionTimestampType.CheckIn, At(-5), requestedBy: _supervisor.User.UserId);
        GivenApproval(world, correction, WorkOrderStatus.Closed); // stale token AND locked: the 403 still comes first

        var result = await ApproveAsync(rowVersion: [9, 9, 9, 9, 9, 9, 9, 9]);

        Assert.Equal(CommandFailure.AccessDenied, result.Error?.Failure);
        Assert.Equal(At(0), world.Session.CheckInAt);
        Assert.Equal(0, _store.SavedApprovals);
    }

    [Fact]
    public async Task Approve_WithAStaleRowVersion_Is409Concurrency_ThenANonPendingOne_Is409State()
    {
        var world = CheckedOutWorld();
        var correction = Pending(world, TimeCorrectionTimestampType.CheckIn, At(-5));
        GivenApproval(world, correction);

        Assert.Equal(CommandFailure.ConcurrencyConflict, (await ApproveAsync(rowVersion: [9, 9, 9, 9, 9, 9, 9, 9])).Error?.Failure);

        correction.Approve(At(100), Guid.NewGuid());
        Assert.Equal(CommandFailure.StateConflict, (await ApproveAsync()).Error?.Failure);
    }

    [Theory]
    [InlineData(WorkOrderStatus.Closed)]
    [InlineData(WorkOrderStatus.Cancelled)]
    public async Task Approve_OnAClosedOrCancelledWorkOrder_Is409_AndChangesNothing(WorkOrderStatus status)
    {
        var world = CheckedOutWorld();
        var correction = Pending(world, TimeCorrectionTimestampType.CheckIn, At(-5));
        GivenApproval(world, correction, status);

        var result = await ApproveAsync();

        Assert.Equal(CommandFailure.StateConflict, result.Error?.Failure);
        Assert.Equal(T0, world.Session.CheckInAt);
        Assert.Equal(TimeCorrectionStatus.Pending, correction.Status);
        Assert.Equal(0, _store.SavedApprovals);
    }

    [Fact]
    public async Task Approve_WhenTheTargetValueChangedSinceTheRequest_Is409_AndNothingIsApplied()
    {
        var world = CheckedOutWorld();
        var correction = Pending(world, TimeCorrectionTimestampType.CheckOut, At(55));
        world.Session.ApplyTimeCorrection(TimeCorrectionTimestampType.CheckOut, At(50), null, world.Pauses); // another correction applied first
        GivenApproval(world, correction);

        var result = await ApproveAsync();

        Assert.Equal(CommandFailure.StateConflict, result.Error?.Failure);
        Assert.Equal(At(50), world.Session.CheckOutAt);
        Assert.Equal(TimeCorrectionStatus.Pending, correction.Status);
    }

    [Fact]
    public async Task Approve_WhenTheChronologyNoLongerHolds_Is409_AndNothingIsApplied()
    {
        var world = CheckedOutWorld();
        var correction = Pending(world, TimeCorrectionTimestampType.CheckIn, At(5)); // valid when requested: the first pause started at 10
        world.Session.ApplyTimeCorrection(TimeCorrectionTimestampType.PauseStart, At(2), world.Pauses[0], world.Pauses); // an affected interval moved meanwhile
        GivenApproval(world, correction);

        var result = await ApproveAsync();

        Assert.Equal(CommandFailure.StateConflict, result.Error?.Failure);
        Assert.Contains("chronological", result.Error!.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(T0, world.Session.CheckInAt);
        Assert.Equal(TimeCorrectionStatus.Pending, correction.Status);
        Assert.Equal(0, _store.SavedApprovals);
    }

    [Fact]
    public async Task Approve_AppliesTheCorrectionToTheExactPause_AuditsSessionAndPause_AndTouchesNoOtherFact()
    {
        var world = CheckedOutWorld();
        var correction = Pending(world, TimeCorrectionTimestampType.Resume, At(22), world.Pauses[0]);
        GivenApproval(world, correction);

        var result = await ApproveAsync();

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Equal(At(22), world.Pauses[0].ResumedAt);
        Assert.Equal(At(40), world.Session.ResumeAt);                       // an older pause is not the latest event
        Assert.Equal((At(30), At(40)), (world.Pauses[1].PausedAt, world.Pauses[1].ResumedAt));
        Assert.Equal(WorkSessionStatus.CheckedOut, world.Session.Status);
        Assert.Equal(TimeCorrectionStatus.Approved, correction.Status);
        Assert.Equal(_supervisor.User.UserId, correction.DecidedBy);
        Assert.Equal(Now.UtcDateTime, correction.DecidedAt);
        Assert.False(correction.SlaRecalculated);
        Assert.Equal(1, _store.SavedApprovals);

        var audit = Assert.Single(_store.CommittedAudits);
        Assert.Equal(WorkOrderAudit.TimeCorrectionApprovedAction, audit.ActionCode);
        Assert.Equal(_supervisor.User.UserId, audit.ActorId);
        Assert.Contains(world.Session.Id.ToString(), audit.NewValueJson!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(world.Pauses[0].Id.ToString(), audit.NewValueJson!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(WorkOrderAudit.VisitCompletedAtSynchronizedField, audit.NewValueJson!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Approve_OfACheckOut_SynchronizesTheVisitCompletionOnlyWhenTheCheckOutIsItsAuthoritativeSource()
    {
        // Authoritative: the Visit is COMPLETED at the old check-out instant and this is its only session.
        var world = CheckedOutWorld();
        GivenApproval(world, Pending(world, TimeCorrectionTimestampType.CheckOut, At(55)));
        Assert.True((await ApproveAsync()).Succeeded);
        Assert.Equal(At(55), world.Session.CheckOutAt);
        Assert.Equal(At(55), world.Visit.CompletedAt);
        Assert.Contains("\"visitCompletedAtSynchronized\":true", _store.CommittedAudits[^1].NewValueJson!, StringComparison.Ordinal);

        // Another session on the Visit: its completion is never overwritten.
        var shared = CheckedOutWorld();
        GivenApproval(shared, Pending(shared, TimeCorrectionTimestampType.CheckOut, At(55)), sessionCount: 2);
        Assert.True((await ApproveAsync()).Succeeded);
        Assert.Equal(At(55), shared.Session.CheckOutAt);
        Assert.Equal(At(60), shared.Visit.CompletedAt);
        Assert.Contains("\"visitCompletedAtSynchronized\":false", _store.CommittedAudits[^1].NewValueJson!, StringComparison.Ordinal);

        // The Visit's completion differs from the recorded old check-out (it came from elsewhere): left untouched.
        var other = CheckedOutWorld();
        other.Visit.CorrectCompletedAt(At(61));
        GivenApproval(other, Pending(other, TimeCorrectionTimestampType.CheckOut, At(55)));
        Assert.True((await ApproveAsync()).Succeeded);
        Assert.Equal(At(61), other.Visit.CompletedAt);
        Assert.Contains("\"visitCompletedAtSynchronized\":false", _store.CommittedAudits[^1].NewValueJson!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Approve_OfACheckOut_DoesNotSynchronizeAVisitThatIsNotCompleted_AndTheAuditSaysSo()
    {
        // The session is checked out but its Visit is still IN_PROGRESS (no completion exists to be "corrected"): nothing is invented.
        var world = CheckedOutWorld();
        var inProgress = ServiceVisit.Create(_tenantId, Guid.NewGuid(), ServiceVisitType.Initial, Guid.NewGuid(), Guid.NewGuid(), T0, T0.AddHours(2));
        inProgress.CheckIn();
        world = world with { Visit = inProgress };
        GivenApproval(world, Pending(world, TimeCorrectionTimestampType.CheckOut, At(55)));

        Assert.True((await ApproveAsync()).Succeeded);

        Assert.Equal(At(55), world.Session.CheckOutAt);
        Assert.Equal(ServiceVisitStatus.InProgress, inProgress.Status);
        Assert.Null(inProgress.CompletedAt);
        Assert.Contains("\"visitCompletedAtSynchronized\":false", _store.CommittedAudits[^1].NewValueJson!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Approve_ThatLosesTheRaceAtSave_IsAConcurrencyConflict_AndCommitsNoAudit()
    {
        var world = CheckedOutWorld();
        GivenApproval(world, Pending(world, TimeCorrectionTimestampType.CheckIn, At(-5)));
        _store.SaveOutcome = WorkOrderSaveOutcome.ConcurrencyConflict;

        var result = await ApproveAsync();

        Assert.Equal(CommandFailure.ConcurrencyConflict, result.Error?.Failure);
        Assert.Empty(_store.CommittedAudits);
    }

    // ---------------- Reject ----------------

    private Task<CommandResult<Guid>> RejectAsync(string? reason = "Not supported by the visit log.", byte[]? rowVersion = null, CommandContext? by = null) =>
        _service.RejectAsync(by ?? _supervisor, Guid.NewGuid(), rowVersion ?? _version, reason, CancellationToken.None);

    [Fact]
    public async Task Reject_ForAnUnknownCorrection_Is404_AndTheRequesterIsRefused()
    {
        Assert.Equal(CommandFailure.NotFound, (await RejectAsync()).Error?.Failure);

        var world = CheckedOutWorld();
        _store.RejectTarget = Pending(world, TimeCorrectionTimestampType.CheckIn, At(-5), requestedBy: _supervisor.User.UserId);
        Assert.Equal(CommandFailure.AccessDenied, (await RejectAsync(rowVersion: [9, 9, 9, 9, 9, 9, 9, 9])).Error?.Failure);
    }

    [Fact]
    public async Task Reject_NeedsAReason_AFreshToken_AndAPendingRequest()
    {
        var world = CheckedOutWorld();
        var correction = Pending(world, TimeCorrectionTimestampType.CheckIn, At(-5));
        _store.RejectTarget = correction;

        Assert.Equal(CommandFailure.ConcurrencyConflict, (await RejectAsync(rowVersion: [9, 9, 9, 9, 9, 9, 9, 9])).Error?.Failure);
        var blank = await RejectAsync(reason: "  ");
        Assert.Contains(TimeCorrectionFields.DecisionReason, blank.Error!.Errors.Keys);

        Assert.True((await RejectAsync()).Succeeded);
        Assert.Equal(CommandFailure.StateConflict, (await RejectAsync()).Error?.Failure);
    }

    [Fact]
    public async Task Reject_IsStillPossibleAfterTheWorkOrderIsClosed_BecauseItChangesNoTimeFact()
    {
        var world = CheckedOutWorld();
        var correction = Pending(world, TimeCorrectionTimestampType.CheckOut, At(55));
        _store.RejectTarget = correction; // the Reject path never reads the Work Order status

        var result = await RejectAsync();

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Equal(TimeCorrectionStatus.Rejected, correction.Status);
        Assert.Equal("Not supported by the visit log.", correction.DecisionReason);
        Assert.Equal(At(60), world.Session.CheckOutAt);
        Assert.Equal(At(60), world.Visit.CompletedAt);
        var audit = Assert.Single(_store.CommittedAudits);
        Assert.Equal(WorkOrderAudit.TimeCorrectionRejectedAction, audit.ActionCode);
        Assert.Equal("Not supported by the visit log.", audit.Reason);
        Assert.Contains(world.Session.Id.ToString(), audit.NewValueJson!, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------- SLA ----------------

    [Fact]
    public void TheServiceAndItsPortHaveNoDependencyOnSla()
    {
        var types = new[] { typeof(TimeCorrectionService), typeof(ITimeCorrectionStore) };
        foreach (var type in types)
        {
            Assert.DoesNotContain(
                type.GetConstructors().SelectMany(constructor => constructor.GetParameters()),
                parameter => parameter.ParameterType.FullName!.Contains("Sla", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(type.GetMethods(), method => method.Name.Contains("Sla", StringComparison.OrdinalIgnoreCase));
        }
    }
}
