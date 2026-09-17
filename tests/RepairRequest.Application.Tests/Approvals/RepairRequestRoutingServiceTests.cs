using RepairRequest.Application.Approvals;
using RepairRequest.Application.Common;
using RepairRequest.Application.RepairRequests;
using RepairRequest.Application.Security;
using RepairRequest.Application.Tests.MasterData;
using RepairRequest.Domain.Approvals;
using RepairRequest.Domain.RepairRequests;
using RepairRequest.Domain.Security;

namespace RepairRequest.Application.Tests.Approvals;

/// <summary>
/// ST-RR-003 routing service (DEC-PRE-S1-007R-01/07/08/09): deterministic order 404 -> 409 stale -> 409 state -> 409 already
/// assigned; success assigns and moves to UNDER_REVIEW with a ROUTED audit; failures stay SUBMITTED with the approved
/// failure data; Admin Retry re-runs the same rules and never serves a caller without configuration scope.
/// </summary>
public class RepairRequestRoutingServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 10, 0, 0, TimeSpan.Zero);

    private readonly FakeApprovalRoutingStore _store = new();
    private readonly RepairRequestRoutingService _service;
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _siteId = Guid.NewGuid();
    private readonly Guid _creator = Guid.NewGuid();

    public RepairRequestRoutingServiceTests()
    {
        _service = new RepairRequestRoutingService(_store, new FixedClock(Now));
    }

    private CommandContext Requester() => new(new CurrentUser(_creator, _tenantId, [RoleCodes.Requester]), Guid.NewGuid());

    private CommandContext Administrator(Guid? tenantId = null) =>
        new(new CurrentUser(Guid.NewGuid(), tenantId ?? _tenantId, [RoleCodes.Administrator]), Guid.NewGuid());

    private RouteCandidate AddRoute(Guid? approverUserId = null, bool siteSpecific = true)
    {
        var route = new RouteCandidate(Guid.NewGuid(), siteSpecific, [new RouteStepCandidate(1, RoleCodes.Approver, approverUserId)]);
        _store.ActiveRoutes.Add(route);
        return route;
    }

    private Guid AddEligibleApprover()
    {
        var approver = Guid.NewGuid();
        _store.EligibleApprovers.Add((approver, _siteId));
        return approver;
    }

    private Task<CommandResult<RoutingResultDto>> RouteSubmittedAsync(Guid requestId, byte[]? rowVersion = null) =>
        _service.RouteSubmittedAsync(Requester(), requestId, rowVersion ?? FakeApprovalRoutingStore.InitialRowVersion, CancellationToken.None);

    // ---------------- Success ----------------

    [Fact]
    public async Task RoleOnlyRoute_WithOneEligibleApprover_AssignsIt_MovesToUnderReview_AndAuditsOnce()
    {
        var request = _store.SeedSubmitted(_tenantId, _creator, _siteId);
        var route = AddRoute();
        var approver = AddEligibleApprover();

        var result = await RouteSubmittedAsync(request.Id);

        Assert.True(result.Succeeded);
        Assert.Equal(RepairRequestStatus.UnderReview, result.Value!.Status);
        Assert.Null(result.Value.RoutingFailureCode);
        Assert.Equal(FakeApprovalRoutingStore.RoutedRowVersion, result.Value.RowVersion);
        Assert.Equal("RR-2026-000001", request.RequestNo);

        var approval = Assert.Single(_store.Approvals);
        Assert.Equal(route.RouteId, approval.ApprovalRouteId);
        Assert.Equal(approver, approval.AssignedApproverId);
        Assert.Equal(ApprovalStatus.Pending, approval.Status);
        Assert.Null(approval.RoutingFailureCode);

        var audit = Assert.Single(_store.Audits);
        Assert.Equal(RepairRequestRoutingAudit.RoutedAction, audit.ActionCode);
        Assert.Equal("SUBMITTED", audit.FromState);
        Assert.Equal("UNDER_REVIEW", audit.ToState);
        Assert.Contains("\"trigger\":\"SUBMIT\"", audit.NewValueJson);
        Assert.Equal(1, _store.Commits);

        // ST-RR-003: System performs routing; the submitting requester is only the initiator.
        Assert.Equal(SystemActors.ApprovalRouting, audit.ActorId);
        Assert.NotEqual(_creator, audit.ActorId);
        Assert.Contains($"\"initiatedBy\":\"{_creator}\"", audit.NewValueJson);
    }

    [Fact]
    public async Task SpecificApproverRoute_AssignsThatApprover_WhenEligible()
    {
        var request = _store.SeedSubmitted(_tenantId, _creator, _siteId);
        var approver = AddEligibleApprover();
        AddEligibleApprover();
        AddRoute(approver);

        var result = await RouteSubmittedAsync(request.Id);

        Assert.Equal(RepairRequestStatus.UnderReview, result.Value!.Status);
        Assert.Equal(approver, Assert.Single(_store.Approvals).AssignedApproverId);
    }

    // ---------------- Failures (DEC-PRE-S1-007R-09) ----------------

    [Fact]
    public async Task NoRoute_StaysSubmitted_WritesNoApprovalRow_AndAuditsTheFailureCode()
    {
        var request = _store.SeedSubmitted(_tenantId, _creator, _siteId);
        AddEligibleApprover();

        var result = await RouteSubmittedAsync(request.Id);

        Assert.True(result.Succeeded);
        Assert.Equal(RepairRequestStatus.Submitted, result.Value!.Status);
        Assert.Equal(RoutingFailureCodes.RouteNotFound, result.Value.RoutingFailureCode);
        Assert.Equal(FakeApprovalRoutingStore.InitialRowVersion, result.Value.RowVersion);
        Assert.Empty(_store.Approvals);

        var audit = Assert.Single(_store.Audits);
        Assert.Equal(RepairRequestRoutingAudit.RoutingFailedAction, audit.ActionCode);
        Assert.Equal(RoutingFailureCodes.RouteNotFound, RepairRequestRoutingAudit.ReadFailureCode(audit.NewValueJson));
        Assert.Null(audit.FromState);
        Assert.Null(audit.ToState);
        Assert.Equal(SystemActors.ApprovalRouting, audit.ActorId);
        Assert.Contains($"\"initiatedBy\":\"{_creator}\"", audit.NewValueJson);
    }

    [Fact]
    public async Task RouteResolved_ButSeveralEligibleApprovers_WritesAFailureRowWithoutApprover()
    {
        var request = _store.SeedSubmitted(_tenantId, _creator, _siteId);
        var route = AddRoute();
        AddEligibleApprover();
        AddEligibleApprover();

        var result = await RouteSubmittedAsync(request.Id);

        Assert.Equal(RepairRequestStatus.Submitted, result.Value!.Status);
        Assert.Equal(RoutingFailureCodes.ApproverAmbiguous, result.Value.RoutingFailureCode);
        var approval = Assert.Single(_store.Approvals);
        Assert.Equal(route.RouteId, approval.ApprovalRouteId);
        Assert.Null(approval.AssignedApproverId);
        Assert.Equal(RoutingFailureCodes.ApproverAmbiguous, approval.RoutingFailureCode);
    }

    [Fact]
    public async Task SpecificApproverWhoCreatedTheRequest_IsNeverAssigned()
    {
        var request = _store.SeedSubmitted(_tenantId, _creator, _siteId);
        _store.EligibleApprovers.Add((_creator, _siteId));
        AddRoute(_creator);

        var result = await RouteSubmittedAsync(request.Id);

        Assert.Equal(RepairRequestStatus.Submitted, result.Value!.Status);
        Assert.Equal(RoutingFailureCodes.ApproverNotEligible, result.Value.RoutingFailureCode);
        Assert.Null(Assert.Single(_store.Approvals).AssignedApproverId);
    }

    [Fact]
    public async Task RoleOnlyRoute_WhereTheCreatorIsTheOnlyApprover_IsApproverNotFound()
    {
        var request = _store.SeedSubmitted(_tenantId, _creator, _siteId);
        _store.EligibleApprovers.Add((_creator, _siteId));
        AddRoute();

        var result = await RouteSubmittedAsync(request.Id);

        Assert.Equal(RoutingFailureCodes.ApproverNotFound, result.Value!.RoutingFailureCode);
        Assert.Equal(RepairRequestStatus.Submitted, request.Status);
    }

    // ---------------- Order of checks ----------------

    [Fact]
    public async Task UnknownOrOtherTenantRequest_IsNotFound_WithoutRouteQueries()
    {
        var otherTenant = _store.SeedSubmitted(Guid.NewGuid(), _creator, _siteId);

        Assert.Equal(CommandFailure.NotFound, (await RouteSubmittedAsync(Guid.NewGuid())).Error?.Failure);
        Assert.Equal(CommandFailure.NotFound, (await RouteSubmittedAsync(otherTenant.Id)).Error?.Failure);
        Assert.Equal(0, _store.RouteQueries);
    }

    [Fact]
    public async Task StaleRowVersion_IsConcurrencyConflict_WithoutRouteQueries()
    {
        var request = _store.SeedSubmitted(_tenantId, _creator, _siteId);

        var result = await RouteSubmittedAsync(request.Id, [9, 9, 9, 9, 9, 9, 9, 9]);

        Assert.Equal(CommandFailure.ConcurrencyConflict, result.Error?.Failure);
        Assert.Equal(0, _store.RouteQueries);
    }

    [Theory]
    [InlineData(RepairRequestStatus.UnderReview)]
    [InlineData(RepairRequestStatus.Approved)]
    [InlineData(RepairRequestStatus.Rejected)]
    [InlineData(RepairRequestStatus.Cancelled)]
    public async Task NotSubmitted_IsStateConflict(RepairRequestStatus status)
    {
        var request = _store.SeedSubmitted(_tenantId, _creator, _siteId, status);

        var result = await RouteSubmittedAsync(request.Id);

        Assert.Equal(CommandFailure.StateConflict, result.Error?.Failure);
        Assert.Empty(_store.Audits);
    }

    [Fact]
    public async Task AlreadyAssignedPendingApproval_IsStateConflict_AndNothingChanges()
    {
        var request = _store.SeedSubmitted(_tenantId, _creator, _siteId);
        _store.Approvals.Add(RepairRequestApproval.Assigned(_tenantId, request.Id, RepairRequestApproval.FirstCycleNo, Guid.NewGuid(), 1, Guid.NewGuid(), Now.UtcDateTime));
        AddRoute();
        AddEligibleApprover();

        var result = await _service.RetryAsync(Administrator(), request.Id, FakeApprovalRoutingStore.InitialRowVersion, CancellationToken.None);

        Assert.Equal(CommandFailure.StateConflict, result.Error?.Failure);
        Assert.Equal(RepairRequestStatus.Submitted, request.Status);
        Assert.Empty(_store.Audits);
    }

    [Theory]
    [InlineData(ApprovalStatus.Approved)]
    [InlineData(ApprovalStatus.Rejected)]
    public async Task DecidedApproval_IsStateConflict_AndIsNeverRemovedByARouteLevelRetry(ApprovalStatus decision)
    {
        var request = _store.SeedSubmitted(_tenantId, _creator, _siteId);
        var decided = RepairRequestApproval.Assigned(_tenantId, request.Id, RepairRequestApproval.FirstCycleNo, Guid.NewGuid(), 1, Guid.NewGuid(), Now.UtcDateTime);
        FakeApprovalRoutingStore.Set(decided, nameof(RepairRequestApproval.Status), decision);
        _store.Approvals.Add(decided);

        // No active route: a route-level failure would apply, but a decided approval stops the command before any change.
        var result = await _service.RetryAsync(Administrator(), request.Id, FakeApprovalRoutingStore.InitialRowVersion, CancellationToken.None);

        Assert.Equal(CommandFailure.StateConflict, result.Error?.Failure);
        Assert.Same(decided, Assert.Single(_store.Approvals));
        Assert.Empty(_store.Audits);
    }

    [Fact]
    public async Task ReturnedApproval_IsHistory_ARouteLevelFailureKeepsIt_AndRecordsTheNextCycle()
    {
        // DEC-PRE-S1-010-01: a step returned for correction belongs to an earlier cycle; routing a resubmission never
        // changes or removes it.
        var request = _store.SeedSubmitted(_tenantId, _creator, _siteId);
        var returned = RepairRequestApproval.Assigned(_tenantId, request.Id, RepairRequestApproval.FirstCycleNo, Guid.NewGuid(), 1, Guid.NewGuid(), Now.UtcDateTime);
        FakeApprovalRoutingStore.Set(returned, nameof(RepairRequestApproval.Status), ApprovalStatus.ReturnedForCorrection);
        _store.Approvals.Add(returned);

        var result = await RouteSubmittedAsync(request.Id);

        Assert.True(result.Succeeded);
        Assert.Equal(RepairRequestStatus.Submitted, result.Value!.Status);
        Assert.Same(returned, Assert.Single(_store.Approvals));
        Assert.Equal(ApprovalStatus.ReturnedForCorrection, returned.Status);
        var audit = Assert.Single(_store.Audits);
        Assert.Equal(RepairRequestRoutingAudit.RoutingFailedAction, audit.ActionCode);
        Assert.Contains("\"approvalCycleNo\":2", audit.NewValueJson);
    }

    [Fact]
    public async Task ReturnedApproval_ResubmissionIsAssignedInTheNextCycle()
    {
        var request = _store.SeedSubmitted(_tenantId, _creator, _siteId);
        var returned = RepairRequestApproval.Assigned(_tenantId, request.Id, RepairRequestApproval.FirstCycleNo, Guid.NewGuid(), 1, Guid.NewGuid(), Now.UtcDateTime);
        FakeApprovalRoutingStore.Set(returned, nameof(RepairRequestApproval.Status), ApprovalStatus.ReturnedForCorrection);
        _store.Approvals.Add(returned);
        AddRoute();
        var approver = AddEligibleApprover();

        var result = await RouteSubmittedAsync(request.Id);

        Assert.True(result.Succeeded);
        Assert.Equal(RepairRequestStatus.UnderReview, result.Value!.Status);
        Assert.Equal(2, _store.Approvals.Count);
        Assert.Equal(ApprovalStatus.ReturnedForCorrection, returned.Status);
        var next = Assert.Single(_store.Approvals, approval => approval != returned);
        Assert.Equal(2, next.ApprovalCycleNo);
        Assert.Equal(approver, next.AssignedApproverId);
        Assert.True(next.IsAssignedPending);
    }

    [Fact]
    public async Task SaveConflict_CommitsNothing()
    {
        var request = _store.SeedSubmitted(_tenantId, _creator, _siteId);
        AddRoute();
        AddEligibleApprover();
        _store.SaveOutcome = RepairRequestSaveOutcome.ConcurrencyConflict;

        var result = await RouteSubmittedAsync(request.Id);

        Assert.Equal(CommandFailure.ConcurrencyConflict, result.Error?.Failure);
        Assert.Empty(_store.Approvals);
        Assert.Empty(_store.Audits);
        Assert.Equal(0, _store.Commits);
    }

    // ---------------- Admin Retry (DEC-PRE-S1-007R-07/08) ----------------

    [Fact]
    public async Task Retry_OfALegacyUnroutedRequest_RoutesWithTheSameRules_AndRecordsTheAdminTrigger()
    {
        var request = _store.SeedSubmitted(_tenantId, _creator, _siteId);
        AddRoute();
        var approver = AddEligibleApprover();
        var admin = Administrator();

        var result = await _service.RetryAsync(admin, request.Id, FakeApprovalRoutingStore.InitialRowVersion, CancellationToken.None);

        Assert.Equal(RepairRequestStatus.UnderReview, result.Value!.Status);
        Assert.Equal(approver, Assert.Single(_store.Approvals).AssignedApproverId);
        var audit = Assert.Single(_store.Audits);
        Assert.Contains("\"trigger\":\"ADMIN_RETRY\"", audit.NewValueJson);

        // The ADMINISTRATOR initiates the retry; the routing decision and assignment are still performed by System.
        Assert.Equal(SystemActors.ApprovalRouting, audit.ActorId);
        Assert.NotEqual(admin.User.UserId, audit.ActorId);
        Assert.Contains($"\"initiatedBy\":\"{admin.User.UserId}\"", audit.NewValueJson);
    }

    [Fact]
    public async Task Retry_RouteLevelFailureAfterApproverFailure_RemovesTheStaleRow_AndAuditsTheNewCode()
    {
        var request = _store.SeedSubmitted(_tenantId, _creator, _siteId);
        _store.Approvals.Add(RepairRequestApproval.AssignmentFailed(_tenantId, request.Id, RepairRequestApproval.FirstCycleNo, Guid.NewGuid(), 1, RoutingFailureCodes.ApproverAmbiguous));

        var result = await _service.RetryAsync(Administrator(), request.Id, FakeApprovalRoutingStore.InitialRowVersion, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(RepairRequestStatus.Submitted, result.Value!.Status);
        Assert.Equal(RoutingFailureCodes.RouteNotFound, result.Value.RoutingFailureCode);
        Assert.Empty(_store.Approvals);
        Assert.Equal(RoutingFailureCodes.RouteNotFound, RepairRequestRoutingAudit.ReadFailureCode(Assert.Single(_store.Audits).NewValueJson));
    }

    [Fact]
    public async Task Retry_OfAFailureRow_UpdatesThatRow_InsteadOfAddingAnother()
    {
        var request = _store.SeedSubmitted(_tenantId, _creator, _siteId);
        var failureRow = RepairRequestApproval.AssignmentFailed(_tenantId, request.Id, RepairRequestApproval.FirstCycleNo, Guid.NewGuid(), 1, RoutingFailureCodes.ApproverNotFound);
        _store.Approvals.Add(failureRow);
        var route = AddRoute();
        var approver = AddEligibleApprover();

        var result = await _service.RetryAsync(Administrator(), request.Id, FakeApprovalRoutingStore.InitialRowVersion, CancellationToken.None);

        Assert.Equal(RepairRequestStatus.UnderReview, result.Value!.Status);
        var approval = Assert.Single(_store.Approvals);
        Assert.Same(failureRow, approval);
        Assert.Equal(route.RouteId, approval.ApprovalRouteId);
        Assert.Equal(approver, approval.AssignedApproverId);
        Assert.Null(approval.RoutingFailureCode);
    }

    [Fact]
    public async Task Retry_ThatFailsAgain_StaysSubmitted_AndRecordsTheNewFailure()
    {
        var request = _store.SeedSubmitted(_tenantId, _creator, _siteId);

        var result = await _service.RetryAsync(Administrator(), request.Id, FakeApprovalRoutingStore.InitialRowVersion, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(RepairRequestStatus.Submitted, result.Value!.Status);
        Assert.Equal(RoutingFailureCodes.RouteNotFound, result.Value.RoutingFailureCode);
        Assert.Contains("\"trigger\":\"ADMIN_RETRY\"", Assert.Single(_store.Audits).NewValueJson);
    }

    [Theory]
    [InlineData(RoleCodes.Approver)]
    [InlineData(RoleCodes.Supervisor)]
    [InlineData(RoleCodes.Requester)]
    public async Task Retry_ByACallerWithoutConfigurationScope_IsNotFound(string role)
    {
        var request = _store.SeedSubmitted(_tenantId, _creator, _siteId);
        AddRoute();
        AddEligibleApprover();
        var caller = new CommandContext(new CurrentUser(Guid.NewGuid(), _tenantId, [role]), Guid.NewGuid());

        var result = await _service.RetryAsync(caller, request.Id, FakeApprovalRoutingStore.InitialRowVersion, CancellationToken.None);

        Assert.Equal(CommandFailure.NotFound, result.Error?.Failure);
        Assert.Equal(RepairRequestStatus.Submitted, request.Status);
        Assert.Equal(0, _store.RouteQueries);
    }

    [Fact]
    public async Task Retry_ByAnAdministratorOfAnotherTenant_IsNotFound()
    {
        var request = _store.SeedSubmitted(_tenantId, _creator, _siteId);

        var result = await _service.RetryAsync(Administrator(Guid.NewGuid()), request.Id, FakeApprovalRoutingStore.InitialRowVersion, CancellationToken.None);

        Assert.Equal(CommandFailure.NotFound, result.Error?.Failure);
    }
}
