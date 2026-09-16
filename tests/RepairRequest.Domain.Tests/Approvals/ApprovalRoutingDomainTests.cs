using RepairRequest.Domain.Approvals;
using RepairRequest.Domain.Common;
using RepairRequest.Domain.RepairRequests;
using RepairRequest.Domain.Security;
using RepairRequestAggregate = RepairRequest.Domain.RepairRequests.RepairRequest;

namespace RepairRequest.Domain.Tests.Approvals;

/// <summary>S1-007R routing domain invariants (RR-DD-001 ARC/APR; ST-RR-003; DEC-PRE-S1-007R-03/05/09).</summary>
public class ApprovalRoutingDomainTests
{
    private static readonly DateTime Now = new(2026, 9, 15, 9, 0, 0, DateTimeKind.Utc);

    private static ApprovalRoute PersistedRoute(Guid? siteId = null)
    {
        var route = ApprovalRoute.Create(Guid.NewGuid(), "ELECTRICAL", siteId);
        typeof(ApprovalRoute).GetProperty(nameof(ApprovalRoute.Id))!.SetValue(route, Guid.NewGuid());
        return route;
    }

    private static RepairRequestAggregate SubmittedRequest()
    {
        var owner = Guid.NewGuid();
        var request = RepairRequestAggregate.CreateDraft(Guid.NewGuid(), owner);
        request.EditDraft(Guid.NewGuid(), null, "ELECTRICAL", "HIGH", Guid.NewGuid(), "Pump leaking", Now, Now.AddHours(2));
        request.Submit("RR-2026-000001", owner, Now, null);
        return request;
    }

    // ---------------- Approval route ----------------

    [Fact]
    public void Route_Create_IsActive_WithOptionalSite()
    {
        var siteId = Guid.NewGuid();

        var siteRoute = ApprovalRoute.Create(Guid.NewGuid(), "ELECTRICAL", siteId);
        var defaultRoute = ApprovalRoute.Create(Guid.NewGuid(), "PLUMBING", null);

        Assert.True(siteRoute.IsActive);
        Assert.Equal(siteId, siteRoute.SiteId);
        Assert.Equal("ELECTRICAL", siteRoute.RequestCategoryCode);
        Assert.Null(defaultRoute.SiteId);
    }

    [Fact]
    public void Route_Create_RejectsEmptyTenant_BlankOrLongCategory_AndEmptySite()
    {
        Assert.Throws<ArgumentException>(() => ApprovalRoute.Create(Guid.Empty, "ELECTRICAL", null));
        Assert.Throws<ArgumentException>(() => ApprovalRoute.Create(Guid.NewGuid(), " ", null));
        Assert.Throws<ArgumentException>(() => ApprovalRoute.Create(Guid.NewGuid(), new string('C', ApprovalRoute.CategoryCodeMaxLength + 1), null));
        Assert.Throws<ArgumentException>(() => ApprovalRoute.Create(Guid.NewGuid(), "ELECTRICAL", Guid.Empty));
    }

    [Fact]
    public void Route_ActivateAndDeactivate_GuardTheCurrentState()
    {
        var route = PersistedRoute();

        Assert.Throws<DomainRuleViolationException>(route.Activate);
        route.Deactivate();
        Assert.False(route.IsActive);
        Assert.Throws<DomainRuleViolationException>(route.Deactivate);
        route.Activate();
        Assert.True(route.IsActive);
    }

    [Fact]
    public void Step_IsTheSingleFirstApproverStep_OfAPersistedRoute()
    {
        var route = PersistedRoute();
        var approver = Guid.NewGuid();

        var step = ApprovalRouteStep.CreateFirstStep(route, RoleCodes.Approver, approver);

        Assert.Equal(route.Id, step.ApprovalRouteId);
        Assert.Equal(route.TenantId, step.TenantId);
        Assert.Equal(ApprovalRouteStep.FirstStepNo, step.StepNo);
        Assert.Equal(RoleCodes.Approver, step.ApproverRoleCode);
        Assert.Equal(approver, step.ApproverUserId);
    }

    [Theory]
    [InlineData(RoleCodes.Coordinator)]
    [InlineData(RoleCodes.Supervisor)]
    [InlineData(RoleCodes.Administrator)]
    [InlineData("approver")]
    public void Step_RejectsAnyRoleOtherThanApprover(string roleCode)
    {
        Assert.Throws<ArgumentException>(() => ApprovalRouteStep.CreateFirstStep(PersistedRoute(), roleCode, null));
    }

    [Fact]
    public void Step_RequiresARouteIdentifier_AndRejectsAnEmptyApproverUser()
    {
        var unsaved = ApprovalRoute.Create(Guid.NewGuid(), "ELECTRICAL", null);

        Assert.Throws<ArgumentException>(() => ApprovalRouteStep.CreateFirstStep(unsaved, RoleCodes.Approver, null));
        Assert.Throws<ArgumentException>(() => ApprovalRouteStep.CreateFirstStep(PersistedRoute(), RoleCodes.Approver, Guid.Empty));
    }

    // ---------------- Repair request approval ----------------

    [Fact]
    public void Approval_Assigned_IsPendingWithApproverAndRoutedTime_AndNoFailure()
    {
        var approver = Guid.NewGuid();

        var approval = RepairRequestApproval.Assigned(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, approver, Now);

        Assert.Equal(ApprovalStatus.Pending, approval.Status);
        Assert.Equal(approver, approval.AssignedApproverId);
        Assert.Equal(Now, approval.RoutedAt);
        Assert.Null(approval.RoutingFailureCode);
        Assert.True(approval.IsAssignedPending);
    }

    [Fact]
    public void Approval_AssignmentFailed_HasRouteAndStep_ButNoApprover()
    {
        var routeId = Guid.NewGuid();

        var approval = RepairRequestApproval.AssignmentFailed(Guid.NewGuid(), Guid.NewGuid(), routeId, 1, RoutingFailureCodes.ApproverAmbiguous);

        Assert.Equal(routeId, approval.ApprovalRouteId);
        Assert.Equal(1, approval.ApprovalStepNo);
        Assert.Null(approval.AssignedApproverId);
        Assert.Null(approval.RoutedAt);
        Assert.Equal(RoutingFailureCodes.ApproverAmbiguous, approval.RoutingFailureCode);
        Assert.False(approval.IsAssignedPending);
    }

    [Theory]
    [InlineData(RoutingFailureCodes.RouteNotFound)]
    [InlineData(RoutingFailureCodes.RouteAmbiguous)]
    [InlineData(RoutingFailureCodes.RouteStepInvalid)]
    [InlineData("UNKNOWN_CODE")]
    public void Approval_RouteLevelOrUnknownFailureCodes_AreNeverStoredOnARow(string code)
    {
        Assert.Throws<ArgumentException>(() => RepairRequestApproval.AssignmentFailed(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, code));
    }

    [Fact]
    public void Approval_RequiresRouteStepAndUtcTime()
    {
        Assert.Throws<ArgumentException>(() => RepairRequestApproval.Assigned(Guid.NewGuid(), Guid.NewGuid(), Guid.Empty, 1, Guid.NewGuid(), Now));
        Assert.Throws<ArgumentOutOfRangeException>(() => RepairRequestApproval.Assigned(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 0, Guid.NewGuid(), Now));
        Assert.Throws<ArgumentException>(() => RepairRequestApproval.Assigned(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, Guid.Empty, Now));
        Assert.Throws<ArgumentException>(() => RepairRequestApproval.Assigned(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid(), DateTime.SpecifyKind(Now, DateTimeKind.Local)));
    }

    [Fact]
    public void Approval_Retry_AssignsOrRecordsANewFailure_OnlyWhileUnassigned()
    {
        var failed = RepairRequestApproval.AssignmentFailed(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, RoutingFailureCodes.ApproverNotFound);
        var newRoute = Guid.NewGuid();

        failed.FailOnRetry(newRoute, 1, RoutingFailureCodes.ApproverNotEligible);
        Assert.Equal(newRoute, failed.ApprovalRouteId);
        Assert.Equal(RoutingFailureCodes.ApproverNotEligible, failed.RoutingFailureCode);

        var approver = Guid.NewGuid();
        failed.AssignOnRetry(newRoute, 1, approver, Now);
        Assert.Equal(approver, failed.AssignedApproverId);
        Assert.Null(failed.RoutingFailureCode);

        Assert.Throws<DomainRuleViolationException>(() => failed.AssignOnRetry(newRoute, 1, Guid.NewGuid(), Now));
        Assert.Throws<DomainRuleViolationException>(() => failed.FailOnRetry(newRoute, 1, RoutingFailureCodes.ApproverNotFound));
    }

    [Fact]
    public void Approval_OnlyAnUnassignedRoutingFailure_IsDiscardable()
    {
        var failure = RepairRequestApproval.AssignmentFailed(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, RoutingFailureCodes.ApproverAmbiguous);
        var assigned = RepairRequestApproval.Assigned(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid(), Now);

        failure.EnsureDiscardableRoutingFailure();
        Assert.Throws<DomainRuleViolationException>(assigned.EnsureDiscardableRoutingFailure);
    }

    [Theory]
    [InlineData(ApprovalStatus.Approved)]
    [InlineData(ApprovalStatus.Rejected)]
    [InlineData(ApprovalStatus.ReturnedForCorrection)]
    public void Approval_WithADecision_IsNeverDiscardable(ApprovalStatus decision)
    {
        // Decisions are not produced by S1-007R; the status is forced to prove the removal guard never accepts a decided row.
        var decided = RepairRequestApproval.AssignmentFailed(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, RoutingFailureCodes.ApproverNotFound);
        typeof(RepairRequestApproval).GetProperty(nameof(RepairRequestApproval.Status))!.SetValue(decided, decision);

        Assert.Throws<DomainRuleViolationException>(decided.EnsureDiscardableRoutingFailure);
    }

    // ---------------- ST-RR-003 ----------------

    [Fact]
    public void RouteForReview_MovesSubmittedToUnderReview_AndKeepsSubmitData()
    {
        var request = SubmittedRequest();

        request.RouteForReview();

        Assert.Equal(RepairRequestStatus.UnderReview, request.Status);
        Assert.Equal("RR-2026-000001", request.RequestNo);
        Assert.Equal(Now, request.SubmittedAt);
    }

    [Fact]
    public void RouteForReview_IsRejectedOutsideSubmitted()
    {
        var draft = RepairRequestAggregate.CreateDraft(Guid.NewGuid(), Guid.NewGuid());
        var routed = SubmittedRequest();
        routed.RouteForReview();

        Assert.Throws<DomainRuleViolationException>(draft.RouteForReview);
        Assert.Throws<DomainRuleViolationException>(routed.RouteForReview);
        Assert.Equal(RepairRequestStatus.Draft, draft.Status);
    }
}
