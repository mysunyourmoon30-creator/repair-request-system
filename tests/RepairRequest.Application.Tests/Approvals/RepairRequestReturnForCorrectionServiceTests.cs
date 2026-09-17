using RepairRequest.Application.Approvals;
using RepairRequest.Application.Common;
using RepairRequest.Application.RepairRequests;
using RepairRequest.Application.Security;
using RepairRequest.Application.Tests.MasterData;
using RepairRequest.Domain.Approvals;
using RepairRequest.Domain.RepairRequests;
using RepairRequest.Domain.Security;
using RepairRequestAggregate = RepairRequest.Domain.RepairRequests.RepairRequest;

namespace RepairRequest.Application.Tests.Approvals;

/// <summary>
/// S1-010 Return for Correction (ST-RR-006; DEC-PRE-S1-010-03): the S1-008 decision order 403 role -> 404 scope -> 403
/// self-decision -> 409 stale -> 409 state -> 403 not assigned -> 422 reason; success returns the request to DRAFT, records
/// the decision and reason on the current-cycle step and writes one audit; nothing is written on any failure.
/// </summary>
public class RepairRequestReturnForCorrectionServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 10, 0, 0, 123, TimeSpan.Zero);

    private readonly FakeRepairRequestDecisionStore _store = new();
    private readonly RepairRequestDecisionService _service;
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _creator = Guid.NewGuid();
    private readonly Guid _approver = Guid.NewGuid();

    public RepairRequestReturnForCorrectionServiceTests()
    {
        _service = new RepairRequestDecisionService(_store, new FixedClock(Now));
    }

    private CommandContext Caller(Guid userId, params string[] roles) => new(new CurrentUser(userId, _tenantId, roles), Guid.NewGuid());

    private CommandContext Approver() => Caller(_approver, RoleCodes.Approver);

    private Task<CommandResult<RepairRequestDraftDto>> ReturnAsync(CommandContext caller, RepairRequestAggregate request, string? reason = "Photo does not show the fault", byte[]? rowVersion = null) =>
        _service.ReturnForCorrectionAsync(caller, request.Id, rowVersion ?? FakeRepairRequestDecisionStore.InitialRowVersion, reason, CancellationToken.None);

    private void AssertNothingWritten(RepairRequestAggregate request, RepairRequestApproval step)
    {
        Assert.Equal(RepairRequestStatus.UnderReview, request.Status);
        Assert.Equal(ApprovalStatus.Pending, step.Status);
        Assert.Null(step.DecisionReason);
        Assert.Null(step.DecidedAt);
        Assert.Empty(_store.Audits);
        Assert.Equal(0, _store.Commits);
    }

    [Fact]
    public async Task Return_ByTheAssignedApprover_ReturnsToDraft_RecordsTheTrimmedReasonOnTheStep_AndAuditsOnce()
    {
        var (request, step) = _store.SeedUnderReview(_tenantId, _creator, _approver);
        var caller = Approver();

        var result = await ReturnAsync(caller, request, reason: "  Photo does not show the fault  ");

        Assert.True(result.Succeeded);
        Assert.Equal(RepairRequestStatus.Draft, result.Value!.Status);
        Assert.Equal("RR-2026-000001", result.Value.RequestNo);
        Assert.Equal(new DateTime(2026, 9, 15, 8, 0, 0, DateTimeKind.Utc), result.Value.SubmittedAt);
        Assert.Equal(FakeRepairRequestDecisionStore.DecidedRowVersion, result.Value.RowVersion);
        Assert.Null(request.RejectReason);

        Assert.Equal(ApprovalStatus.ReturnedForCorrection, step.Status);
        Assert.Equal("Photo does not show the fault", step.DecisionReason);
        Assert.Equal(Now.UtcDateTime, step.DecidedAt);
        Assert.Same(step, Assert.Single(_store.Approvals));

        var audit = Assert.Single(_store.Audits);
        Assert.Equal(RepairRequestAudit.ReturnedForCorrectionAction, audit.ActionCode);
        Assert.Equal("UNDER_REVIEW", audit.FromState);
        Assert.Equal("DRAFT", audit.ToState);
        Assert.Equal("Photo does not show the fault", audit.Reason);
        Assert.Equal(_approver, audit.ActorId);
        Assert.Equal(caller.CorrelationId, audit.CorrelationId);
        Assert.Equal(Now.UtcDateTime, audit.OccurredAt);
        Assert.Contains($"\"approvalId\":\"{step.Id}\"", audit.NewValueJson);
        Assert.Contains("\"approvalCycleNo\":1", audit.NewValueJson);
        Assert.Equal(1, _store.Commits);
    }

    [Fact]
    public async Task Return_DecidesTheCurrentCycleStep_AndLeavesEarlierCyclesUnchanged()
    {
        var (request, cycleOne) = _store.SeedUnderReview(_tenantId, _creator, Guid.NewGuid());
        cycleOne.ReturnForCorrection("Earlier return", Now.UtcDateTime.AddDays(-1));
        var cycleTwo = RepairRequestApproval.Assigned(_tenantId, request.Id, 2, Guid.NewGuid(), 1, _approver, Now.UtcDateTime.AddHours(-1));
        _store.Approvals.Add(cycleTwo);

        var result = await ReturnAsync(Approver(), request);

        Assert.True(result.Succeeded);
        Assert.Equal(ApprovalStatus.ReturnedForCorrection, cycleTwo.Status);
        Assert.Equal("Earlier return", cycleOne.DecisionReason);
        Assert.Equal(Now.UtcDateTime.AddDays(-1), cycleOne.DecidedAt);
        Assert.Contains("\"approvalCycleNo\":2", Assert.Single(_store.Audits).NewValueJson);
    }

    [Fact]
    public async Task Return_AcceptsAReasonOfExactlyTheMaximumLength()
    {
        var (request, step) = _store.SeedUnderReview(_tenantId, _creator, _approver);
        var reason = new string('R', RepairRequestApproval.DecisionReasonMaxLength);

        Assert.True((await ReturnAsync(Approver(), request, reason)).Succeeded);
        Assert.Equal(reason, step.DecisionReason);
    }

    [Theory]
    [InlineData(RoleCodes.Requester)]
    [InlineData(RoleCodes.Supervisor)]
    [InlineData(RoleCodes.Coordinator)]
    [InlineData(RoleCodes.Administrator)]
    public async Task CallerWithoutApprover_IsAccessDenied_WithoutTouchingTheStore(string role)
    {
        var (request, step) = _store.SeedUnderReview(_tenantId, _creator, _approver);

        Assert.Equal(CommandFailure.AccessDenied, (await ReturnAsync(Caller(_approver, role), request)).Error?.Failure);
        Assert.Equal(0, _store.Loads);
        AssertNothingWritten(request, step);
    }

    [Fact]
    public async Task UnknownOrOutOfScopeRequest_IsNotFound()
    {
        var (request, step) = _store.SeedUnderReview(_tenantId, _creator, _approver);
        _store.OutOfScope.Add(request.Id);

        Assert.Equal(CommandFailure.NotFound, (await ReturnAsync(Approver(), request)).Error?.Failure);
        Assert.Equal(CommandFailure.NotFound, (await ReturnAsync(Approver(), RepairRequestAggregate.CreateDraft(_tenantId, _creator))).Error?.Failure);
        AssertNothingWritten(request, step);
    }

    [Fact]
    public async Task Creator_IsAccessDenied_EvenWhenAssigned()
    {
        var (request, step) = _store.SeedUnderReview(_tenantId, _creator, assignee: _creator);

        Assert.Equal(CommandFailure.AccessDenied, (await ReturnAsync(Caller(_creator, RoleCodes.Requester, RoleCodes.Approver), request)).Error?.Failure);
        AssertNothingWritten(request, step);
    }

    [Fact]
    public async Task ApproverWhoIsNotAssigned_IsAccessDenied()
    {
        var (request, step) = _store.SeedUnderReview(_tenantId, _creator, _approver);

        Assert.Equal(CommandFailure.AccessDenied, (await ReturnAsync(Caller(Guid.NewGuid(), RoleCodes.Approver), request)).Error?.Failure);
        AssertNothingWritten(request, step);
    }

    [Fact]
    public async Task CycleOneAssignee_CannotDecide_WhenTheCurrentCycleIsAssignedToAnotherApprover()
    {
        var (request, cycleOne) = _store.SeedUnderReview(_tenantId, _creator, _approver);
        cycleOne.ReturnForCorrection("Earlier return", Now.UtcDateTime.AddDays(-1));
        var cycleTwo = RepairRequestApproval.Assigned(_tenantId, request.Id, 2, Guid.NewGuid(), 1, Guid.NewGuid(), Now.UtcDateTime.AddHours(-1));
        _store.Approvals.Add(cycleTwo);

        Assert.Equal(CommandFailure.AccessDenied, (await ReturnAsync(Approver(), request)).Error?.Failure);
        Assert.Equal(CommandFailure.AccessDenied, (await _service.ApproveAsync(Approver(), request.Id, FakeRepairRequestDecisionStore.InitialRowVersion, CancellationToken.None)).Error?.Failure);

        Assert.Equal(RepairRequestStatus.UnderReview, request.Status);
        Assert.True(cycleTwo.IsAssignedPending);
        Assert.Equal("Earlier return", cycleOne.DecisionReason);
        Assert.Empty(_store.Audits);
        Assert.Equal(0, _store.Commits);
    }

    [Fact]
    public async Task StaleRowVersion_IsConcurrencyConflict()
    {
        var (request, step) = _store.SeedUnderReview(_tenantId, _creator, _approver);

        Assert.Equal(CommandFailure.ConcurrencyConflict, (await ReturnAsync(Approver(), request, rowVersion: [9, 9, 9, 9, 9, 9, 9, 9])).Error?.Failure);
        AssertNothingWritten(request, step);
    }

    [Theory]
    [InlineData(RepairRequestStatus.Draft)]
    [InlineData(RepairRequestStatus.Submitted)]
    [InlineData(RepairRequestStatus.Approved)]
    [InlineData(RepairRequestStatus.Rejected)]
    [InlineData(RepairRequestStatus.Cancelled)]
    [InlineData(RepairRequestStatus.Converted)]
    public async Task RequestNotUnderReview_IsStateConflict(RepairRequestStatus status)
    {
        var (request, step) = _store.SeedUnderReview(_tenantId, _creator, _approver);
        FakeRepairRequestDecisionStore.Set(request, nameof(RepairRequestAggregate.Status), status);

        var result = await ReturnAsync(Approver(), request);

        Assert.Equal(CommandFailure.StateConflict, result.Error?.Failure);
        Assert.Equal(status, request.Status);
        Assert.Equal(ApprovalStatus.Pending, step.Status);
        Assert.Empty(_store.Audits);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Return_WithoutAReason_IsValidationFailedOnReason(string? reason)
    {
        var (request, step) = _store.SeedUnderReview(_tenantId, _creator, _approver);

        var result = await ReturnAsync(Approver(), request, reason);

        Assert.Equal(CommandFailure.ValidationFailed, result.Error?.Failure);
        Assert.True(result.Error!.Errors.ContainsKey(RepairRequestFields.Reason));
        AssertNothingWritten(request, step);
    }

    [Fact]
    public async Task Return_WithATooLongReason_IsValidationFailedOnReason()
    {
        var (request, step) = _store.SeedUnderReview(_tenantId, _creator, _approver);

        Assert.Equal(CommandFailure.ValidationFailed, (await ReturnAsync(Approver(), request, new string('R', RepairRequestApproval.DecisionReasonMaxLength + 1))).Error?.Failure);
        AssertNothingWritten(request, step);
    }

    [Fact]
    public async Task LockNotGranted_OrSaveConflict_IsConcurrencyConflict_AndCommitsNothing()
    {
        var (locked, lockedStep) = _store.SeedUnderReview(_tenantId, _creator, _approver);
        _store.LockUnavailable = true;
        Assert.Equal(CommandFailure.ConcurrencyConflict, (await ReturnAsync(Approver(), locked)).Error?.Failure);
        AssertNothingWritten(locked, lockedStep);

        _store.LockUnavailable = false;
        _store.SaveOutcome = RepairRequestSaveOutcome.ConcurrencyConflict;
        var (conflicted, _) = _store.SeedUnderReview(_tenantId, _creator, _approver);
        Assert.Equal(CommandFailure.ConcurrencyConflict, (await ReturnAsync(Approver(), conflicted)).Error?.Failure);
        Assert.Empty(_store.Audits);
        Assert.Equal(0, _store.Commits);
    }
}
