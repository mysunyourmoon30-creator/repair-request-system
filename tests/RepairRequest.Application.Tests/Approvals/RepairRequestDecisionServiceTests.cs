using RepairRequest.Application.Approvals;
using RepairRequest.Application.Common;
using RepairRequest.Application.RepairRequests;
using RepairRequest.Application.Security;
using RepairRequest.Application.Tests.MasterData;
using RepairRequest.Domain.Approvals;
using RepairRequest.Domain.Auditing;
using RepairRequest.Domain.RepairRequests;
using RepairRequest.Domain.Security;
using RepairRequestAggregate = RepairRequest.Domain.RepairRequests.RepairRequest;

namespace RepairRequest.Application.Tests.Approvals;

/// <summary>
/// S1-008 Approve/Reject rules: order 403 role -> 404 scope -> 403 self-decision -> 409 stale -> 409 state -> 403 not assigned
/// -> 422 reason; a successful decision decides the step, moves the request and writes one audit; nothing is written on any
/// failure (DEC-PRE-S1-008-01/03).
/// </summary>
public class RepairRequestDecisionServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 10, 0, 0, 123, TimeSpan.Zero);

    private readonly FakeRepairRequestDecisionStore _store = new();
    private readonly RepairRequestDecisionService _service;
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _creator = Guid.NewGuid();
    private readonly Guid _approver = Guid.NewGuid();

    public RepairRequestDecisionServiceTests()
    {
        _service = new RepairRequestDecisionService(_store, new FixedClock(Now));
    }

    private CommandContext Caller(Guid userId, params string[] roles) => new(new CurrentUser(userId, _tenantId, roles), Guid.NewGuid());

    private CommandContext Approver() => Caller(_approver, RoleCodes.Approver);

    private Task<CommandResult<RepairRequestDraftDto>> DecideAsync(CommandContext caller, RepairRequestAggregate request, bool approve, string? reason = "Out of warranty", byte[]? rowVersion = null) =>
        approve
            ? _service.ApproveAsync(caller, request.Id, rowVersion ?? FakeRepairRequestDecisionStore.InitialRowVersion, CancellationToken.None)
            : _service.RejectAsync(caller, request.Id, rowVersion ?? FakeRepairRequestDecisionStore.InitialRowVersion, reason, CancellationToken.None);

    private void AssertNothingWritten(RepairRequestAggregate request, RepairRequestApproval? step = null)
    {
        Assert.Equal(RepairRequestStatus.UnderReview, request.Status);
        Assert.Null(request.RejectReason);
        Assert.Empty(_store.Audits);
        Assert.Equal(0, _store.Commits);
        if (step is not null)
        {
            Assert.Equal(ApprovalStatus.Pending, step.Status);
            Assert.Null(step.DecidedAt);
        }
    }

    // ---------------- Success ----------------

    [Fact]
    public async Task Approve_ByTheAssignedApprover_Approves_DecidesTheStep_AndAuditsOnce()
    {
        var (request, step) = _store.SeedUnderReview(_tenantId, _creator, _approver);
        var caller = Approver();

        var result = await DecideAsync(caller, request, approve: true);

        Assert.True(result.Succeeded);
        Assert.Equal(RepairRequestStatus.Approved, result.Value!.Status);
        Assert.Equal(FakeRepairRequestDecisionStore.DecidedRowVersion, result.Value.RowVersion);
        Assert.Equal("RR-2026-000001", result.Value.RequestNo);
        Assert.Null(request.RejectReason);

        Assert.Equal(ApprovalStatus.Approved, step.Status);
        Assert.Equal(Now.UtcDateTime, step.DecidedAt);
        Assert.Null(step.DecisionReason);

        var audit = Assert.Single(_store.Audits);
        Assert.Equal(RepairRequestAudit.ApprovedAction, audit.ActionCode);
        Assert.Equal("UNDER_REVIEW", audit.FromState);
        Assert.Equal("APPROVED", audit.ToState);
        Assert.Null(audit.Reason);
        Assert.Equal(_approver, audit.ActorId);
        Assert.Equal(caller.CorrelationId, audit.CorrelationId);
        Assert.Equal(Now.UtcDateTime, audit.OccurredAt);
        Assert.Contains($"\"approvalId\":\"{step.Id}\"", audit.NewValueJson);
        Assert.Equal(1, _store.Commits);
    }

    [Fact]
    public async Task Reject_ByTheAssignedApprover_TrimsTheReason_AndStoresItOnTheRequestStepAndAudit()
    {
        var (request, step) = _store.SeedUnderReview(_tenantId, _creator, _approver);

        var result = await DecideAsync(Approver(), request, approve: false, reason: "  Covered by the service contract  ");

        Assert.True(result.Succeeded);
        Assert.Equal(RepairRequestStatus.Rejected, result.Value!.Status);
        Assert.Equal("Covered by the service contract", request.RejectReason);
        Assert.Equal(ApprovalStatus.Rejected, step.Status);
        Assert.Equal("Covered by the service contract", step.DecisionReason);
        Assert.Equal(Now.UtcDateTime, step.DecidedAt);

        var audit = Assert.Single(_store.Audits);
        Assert.Equal(RepairRequestAudit.RejectedAction, audit.ActionCode);
        Assert.Equal("UNDER_REVIEW", audit.FromState);
        Assert.Equal("REJECTED", audit.ToState);
        Assert.Equal("Covered by the service contract", audit.Reason);
        Assert.Equal(_approver, audit.ActorId);
    }

    [Fact]
    public async Task Reject_AcceptsAReasonOfExactlyTheMaximumLength()
    {
        var (request, _) = _store.SeedUnderReview(_tenantId, _creator, _approver);
        var reason = new string('R', RepairRequestAggregate.ReasonMaxLength);

        Assert.True((await DecideAsync(Approver(), request, approve: false, reason: reason)).Succeeded);
        Assert.Equal(reason, request.RejectReason);
    }

    [Fact]
    public async Task AdministratorAlsoHoldingApprover_WhoIsAssigned_DecidesLikeAnyAssignedApprover()
    {
        var (request, _) = _store.SeedUnderReview(_tenantId, _creator, _approver);

        var result = await DecideAsync(Caller(_approver, RoleCodes.Administrator, RoleCodes.Approver), request, approve: true);

        Assert.True(result.Succeeded);
        Assert.Equal(RepairRequestStatus.Approved, result.Value!.Status);
    }

    // ---------------- Authorization ----------------

    [Theory]
    [InlineData(RoleCodes.Requester)]
    [InlineData(RoleCodes.Supervisor)]
    [InlineData(RoleCodes.Coordinator)]
    [InlineData(RoleCodes.Administrator)]
    public async Task CallerWithoutApprover_IsAccessDenied_WithoutTouchingTheStore(string role)
    {
        var (request, step) = _store.SeedUnderReview(_tenantId, _creator, _approver);

        Assert.Equal(CommandFailure.AccessDenied, (await DecideAsync(Caller(_approver, role), request, approve: true)).Error?.Failure);
        Assert.Equal(CommandFailure.AccessDenied, (await DecideAsync(Caller(_approver, role), request, approve: false)).Error?.Failure);

        Assert.Equal(0, _store.Loads);
        AssertNothingWritten(request, step);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnknownOrOutOfScopeRequest_IsNotFound(bool approve)
    {
        var (request, step) = _store.SeedUnderReview(_tenantId, _creator, _approver);
        _store.OutOfScope.Add(request.Id);
        var unknown = RepairRequestAggregate.CreateDraft(_tenantId, _creator);

        Assert.Equal(CommandFailure.NotFound, (await DecideAsync(Approver(), request, approve)).Error?.Failure);
        Assert.Equal(CommandFailure.NotFound, (await DecideAsync(Approver(), unknown, approve)).Error?.Failure);
        AssertNothingWritten(request, step);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreatorHoldingApprover_IsAccessDenied_EvenWhenTheStepIsAssignedToThem(bool approve)
    {
        // Routing never assigns the creator; the rule is re-checked independently (DEC-PRE-S1-008-01).
        var (request, step) = _store.SeedUnderReview(_tenantId, _creator, assignee: _creator);

        var result = await DecideAsync(Caller(_creator, RoleCodes.Requester, RoleCodes.Approver), request, approve);

        Assert.Equal(CommandFailure.AccessDenied, result.Error?.Failure);
        AssertNothingWritten(request, step);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ApproverWhoIsNotTheAssignedApprover_IsAccessDenied(bool approve)
    {
        var (request, step) = _store.SeedUnderReview(_tenantId, _creator, _approver);

        var result = await DecideAsync(Caller(Guid.NewGuid(), RoleCodes.Approver), request, approve);

        Assert.Equal(CommandFailure.AccessDenied, result.Error?.Failure);
        AssertNothingWritten(request, step);
    }

    [Fact]
    public async Task AdministratorPlusApprover_WhoIsNotAssigned_IsAccessDenied()
    {
        var (request, step) = _store.SeedUnderReview(_tenantId, _creator, _approver);

        var result = await DecideAsync(Caller(Guid.NewGuid(), RoleCodes.Administrator, RoleCodes.Approver), request, approve: true);

        Assert.Equal(CommandFailure.AccessDenied, result.Error?.Failure);
        AssertNothingWritten(request, step);
    }

    [Fact]
    public async Task UnderReviewWithoutAnAssignedPendingStep_IsAccessDenied_FailClosed()
    {
        var (withoutStep, _) = _store.SeedUnderReview(_tenantId, _creator, _approver);
        _store.Approvals.Clear();
        var (withFailureRow, _) = _store.SeedUnderReview(_tenantId, _creator, _approver);
        _store.Approvals.RemoveAll(approval => approval.RepairRequestId == withFailureRow.Id);
        _store.Approvals.Add(RepairRequestApproval.AssignmentFailed(_tenantId, withFailureRow.Id, RepairRequestApproval.FirstCycleNo, Guid.NewGuid(), 1, RoutingFailureCodes.ApproverAmbiguous));

        Assert.Equal(CommandFailure.AccessDenied, (await DecideAsync(Approver(), withoutStep, approve: true)).Error?.Failure);
        Assert.Equal(CommandFailure.AccessDenied, (await DecideAsync(Approver(), withFailureRow, approve: false)).Error?.Failure);
        Assert.Empty(_store.Audits);
    }

    // ---------------- Concurrency and state ----------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StaleRowVersion_IsConcurrencyConflict(bool approve)
    {
        var (request, step) = _store.SeedUnderReview(_tenantId, _creator, _approver);

        var result = await DecideAsync(Approver(), request, approve, rowVersion: [9, 9, 9, 9, 9, 9, 9, 9]);

        Assert.Equal(CommandFailure.ConcurrencyConflict, result.Error?.Failure);
        AssertNothingWritten(request, step);
    }

    [Theory]
    [InlineData(RepairRequestStatus.Submitted, true)]
    [InlineData(RepairRequestStatus.Submitted, false)]
    [InlineData(RepairRequestStatus.Approved, true)]
    [InlineData(RepairRequestStatus.Approved, false)]
    [InlineData(RepairRequestStatus.Rejected, true)]
    [InlineData(RepairRequestStatus.Rejected, false)]
    [InlineData(RepairRequestStatus.Draft, true)]
    [InlineData(RepairRequestStatus.Cancelled, false)]
    [InlineData(RepairRequestStatus.Converted, true)]
    public async Task RequestNotUnderReview_IsStateConflict(RepairRequestStatus status, bool approve)
    {
        var (request, _) = _store.SeedUnderReview(_tenantId, _creator, _approver);
        FakeRepairRequestDecisionStore.Set(request, nameof(RepairRequestAggregate.Status), status);

        var result = await DecideAsync(Approver(), request, approve);

        Assert.Equal(CommandFailure.StateConflict, result.Error?.Failure);
        Assert.Equal(status, request.Status);
        Assert.Empty(_store.Audits);
        Assert.Equal(0, _store.Commits);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("    ")]
    public async Task Reject_WithoutAReason_IsValidationFailedOnReason(string? reason)
    {
        var (request, step) = _store.SeedUnderReview(_tenantId, _creator, _approver);

        var result = await DecideAsync(Approver(), request, approve: false, reason: reason);

        Assert.Equal(CommandFailure.ValidationFailed, result.Error?.Failure);
        Assert.True(result.Error!.Errors.ContainsKey(RepairRequestFields.Reason));
        AssertNothingWritten(request, step);
    }

    [Fact]
    public async Task Reject_WithATooLongReason_IsValidationFailedOnReason()
    {
        var (request, step) = _store.SeedUnderReview(_tenantId, _creator, _approver);

        var result = await DecideAsync(Approver(), request, approve: false, reason: new string('R', RepairRequestAggregate.ReasonMaxLength + 1));

        Assert.Equal(CommandFailure.ValidationFailed, result.Error?.Failure);
        AssertNothingWritten(request, step);
    }

    [Fact]
    public async Task LockNotGranted_IsConcurrencyConflict_AndWritesNothing()
    {
        var (request, step) = _store.SeedUnderReview(_tenantId, _creator, _approver);
        _store.LockUnavailable = true;

        var result = await DecideAsync(Approver(), request, approve: true);

        Assert.Equal(CommandFailure.ConcurrencyConflict, result.Error?.Failure);
        AssertNothingWritten(request, step);
    }

    [Fact]
    public async Task SaveConflict_IsConcurrencyConflict_AndCommitsNothing()
    {
        var (request, _) = _store.SeedUnderReview(_tenantId, _creator, _approver);
        _store.SaveOutcome = RepairRequestSaveOutcome.ConcurrencyConflict;

        var result = await DecideAsync(Approver(), request, approve: false);

        Assert.Equal(CommandFailure.ConcurrencyConflict, result.Error?.Failure);
        Assert.Empty(_store.Audits);
        Assert.Equal(0, _store.Commits);
    }
}

/// <summary>
/// In-memory decision port. Audits are committed only when the save succeeds and the row version still matches; the lock
/// and SQL scope are covered by integration tests.
/// </summary>
internal sealed class FakeRepairRequestDecisionStore : IRepairRequestDecisionStore
{
    public static readonly byte[] InitialRowVersion = [0, 0, 0, 0, 0, 0, 0, 1];
    public static readonly byte[] DecidedRowVersion = [0, 0, 0, 0, 0, 0, 0, 2];

    private readonly List<AuditHistory> _pendingAudits = [];

    public List<RepairRequestAggregate> Requests { get; } = [];

    public List<RepairRequestApproval> Approvals { get; } = [];

    public List<AuditHistory> Audits { get; } = [];

    public HashSet<Guid> OutOfScope { get; } = [];

    public bool LockUnavailable { get; set; }

    public RepairRequestSaveOutcome SaveOutcome { get; set; } = RepairRequestSaveOutcome.Saved;

    public int Commits { get; private set; }

    public int Loads { get; private set; }

    public static void Set<T>(T target, string property, object? value) =>
        typeof(T).GetProperty(property)!.SetValue(target, value);

    public (RepairRequestAggregate Request, RepairRequestApproval Step) SeedUnderReview(Guid tenantId, Guid createdBy, Guid assignee)
    {
        var start = new DateTime(2026, 9, 20, 8, 0, 0, DateTimeKind.Utc);
        var request = RepairRequestAggregate.CreateDraft(tenantId, createdBy);
        request.EditDraft(Guid.NewGuid(), null, "ELECTRICAL", "HIGH", Guid.NewGuid(), "Pump leaking", start, start.AddHours(2));
        request.Submit("RR-2026-000001", createdBy, new DateTime(2026, 9, 15, 8, 0, 0, DateTimeKind.Utc), null);
        request.RouteForReview();
        Set(request, nameof(RepairRequestAggregate.Id), Guid.NewGuid());
        Set(request, nameof(RepairRequestAggregate.RowVersion), InitialRowVersion);
        Requests.Add(request);

        var step = RepairRequestApproval.Assigned(tenantId, request.Id, RepairRequestApproval.FirstCycleNo, Guid.NewGuid(), 1, assignee, new DateTime(2026, 9, 15, 8, 0, 1, DateTimeKind.Utc));
        Set(step, nameof(RepairRequestApproval.Id), Guid.NewGuid());
        Approvals.Add(step);
        return (request, step);
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

    public Task<RepairRequestAggregate?> LockForDecisionAsync(CurrentUser user, Guid repairRequestId, CancellationToken cancellationToken)
    {
        Loads++;
        return Task.FromResult(Requests.SingleOrDefault(request =>
            request.Id == repairRequestId && request.TenantId == user.TenantId && !OutOfScope.Contains(request.Id)));
    }

    public Task<RepairRequestApproval?> FindStepApprovalAsync(Guid tenantId, Guid repairRequestId, short stepNo, CancellationToken cancellationToken) =>
        Task.FromResult(Approvals
            .Where(approval => approval.TenantId == tenantId && approval.RepairRequestId == repairRequestId && approval.ApprovalStepNo == stepNo)
            .OrderByDescending(approval => approval.ApprovalCycleNo)
            .FirstOrDefault());

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
        Set(request, nameof(RepairRequestAggregate.RowVersion), DecidedRowVersion);
        return Task.FromResult(RepairRequestSaveOutcome.Saved);
    }
}
