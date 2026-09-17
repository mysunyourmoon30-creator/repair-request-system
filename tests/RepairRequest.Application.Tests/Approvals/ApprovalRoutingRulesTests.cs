using RepairRequest.Application.Approvals;
using RepairRequest.Domain.Approvals;
using RepairRequest.Domain.Security;

namespace RepairRequest.Application.Tests.Approvals;

/// <summary>Pure routing rules matrix (DEC-PRE-S1-007R-02/03/05/06; segregation of duties DEC-PRE-S1-008-01).</summary>
public class ApprovalRoutingRulesTests
{
    private static readonly Guid Creator = Guid.NewGuid();

    private static RouteStepCandidate Step(Guid? approverUserId = null, short stepNo = 1, string role = RoleCodes.Approver) =>
        new(stepNo, role, approverUserId);

    private static RouteCandidate Route(bool siteSpecific, params RouteStepCandidate[] steps) =>
        new(Guid.NewGuid(), siteSpecific, steps.Length == 0 ? [Step()] : steps);

    // ---------------- Route selection ----------------

    [Fact]
    public void ActiveSiteRoute_TakesPrecedenceOverTheTenantDefault()
    {
        var site = Route(siteSpecific: true);
        var tenantDefault = Route(siteSpecific: false);

        var selection = ApprovalRoutingRules.SelectRoute([tenantDefault, site]);

        Assert.Null(selection.FailureCode);
        Assert.Equal(site.RouteId, selection.RouteId);
    }

    [Fact]
    public void WithoutASiteRoute_TheTenantDefaultIsSelected()
    {
        var tenantDefault = Route(siteSpecific: false);

        Assert.Equal(tenantDefault.RouteId, ApprovalRoutingRules.SelectRoute([tenantDefault]).RouteId);
    }

    [Fact]
    public void NoActiveRoute_IsRouteNotFound()
    {
        var selection = ApprovalRoutingRules.SelectRoute([]);

        Assert.Equal(RoutingFailureCodes.RouteNotFound, selection.FailureCode);
        Assert.Null(selection.RouteId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DuplicateActiveRoutesAtTheSelectedLevel_AreAmbiguous_NeverFirst(bool siteSpecific)
    {
        var selection = ApprovalRoutingRules.SelectRoute([Route(siteSpecific), Route(siteSpecific)]);

        Assert.Equal(RoutingFailureCodes.RouteAmbiguous, selection.FailureCode);
    }

    [Fact]
    public void AmbiguousSiteRoutes_DoNotFallBackToTheTenantDefault()
    {
        var selection = ApprovalRoutingRules.SelectRoute([Route(true), Route(true), Route(false)]);

        Assert.Equal(RoutingFailureCodes.RouteAmbiguous, selection.FailureCode);
    }

    public static TheoryData<RouteStepCandidate[]> InvalidStepSets => new()
    {
        { [Step(), Step(stepNo: 2)] },
        { [Step(stepNo: 2)] },
        { [Step(role: RoleCodes.Coordinator)] }
    };

    [Theory]
    [MemberData(nameof(InvalidStepSets))]
    public void RouteWithoutExactlyOneFirstApproverStep_IsRouteStepInvalid(RouteStepCandidate[] steps)
    {
        var selection = ApprovalRoutingRules.SelectRoute([new RouteCandidate(Guid.NewGuid(), true, steps)]);

        Assert.Equal(RoutingFailureCodes.RouteStepInvalid, selection.FailureCode);
    }

    [Fact]
    public void RouteWithNoSteps_IsRouteStepInvalid()
    {
        var selection = ApprovalRoutingRules.SelectRoute([new RouteCandidate(Guid.NewGuid(), false, [])]);

        Assert.Equal(RoutingFailureCodes.RouteStepInvalid, selection.FailureCode);
    }

    // ---------------- Specific approver ----------------

    [Fact]
    public void SpecificApprover_WhoIsEligible_IsAssigned()
    {
        var routeId = Guid.NewGuid();
        var approver = Guid.NewGuid();

        var decision = ApprovalRoutingRules.ResolveSpecificApprover(routeId, Step(approver), Creator, approverEligible: true);

        Assert.Equal(approver, decision.AssignedApproverId);
        Assert.Equal(routeId, decision.RouteId);
        Assert.Equal((short)1, decision.StepNo);
        Assert.Null(decision.FailureCode);
    }

    [Fact]
    public void SpecificApprover_NotEligible_IsApproverNotEligible_WithRouteAndStep()
    {
        var routeId = Guid.NewGuid();

        var decision = ApprovalRoutingRules.ResolveSpecificApprover(routeId, Step(Guid.NewGuid()), Creator, approverEligible: false);

        Assert.Null(decision.AssignedApproverId);
        Assert.Equal(RoutingFailureCodes.ApproverNotEligible, decision.FailureCode);
        Assert.Equal(routeId, decision.RouteId);
    }

    [Fact]
    public void SpecificApprover_WhoCreatedTheRequest_IsNeverAssigned_EvenIfEligible()
    {
        var decision = ApprovalRoutingRules.ResolveSpecificApprover(Guid.NewGuid(), Step(Creator), Creator, approverEligible: true);

        Assert.Null(decision.AssignedApproverId);
        Assert.Equal(RoutingFailureCodes.ApproverNotEligible, decision.FailureCode);
    }

    // ---------------- Role-only step ----------------

    [Fact]
    public void RoleOnlyStep_ExactlyOneCandidate_IsAssigned()
    {
        var candidate = Guid.NewGuid();

        var decision = ApprovalRoutingRules.ResolveRolePool(Guid.NewGuid(), Step(), Creator, [candidate]);

        Assert.Equal(candidate, decision.AssignedApproverId);
    }

    [Fact]
    public void RoleOnlyStep_NoCandidate_IsApproverNotFound()
    {
        var decision = ApprovalRoutingRules.ResolveRolePool(Guid.NewGuid(), Step(), Creator, []);

        Assert.Equal(RoutingFailureCodes.ApproverNotFound, decision.FailureCode);
        Assert.Null(decision.AssignedApproverId);
    }

    [Fact]
    public void RoleOnlyStep_SeveralCandidates_IsApproverAmbiguous_NeverFirst()
    {
        var decision = ApprovalRoutingRules.ResolveRolePool(Guid.NewGuid(), Step(), Creator, [Guid.NewGuid(), Guid.NewGuid()]);

        Assert.Equal(RoutingFailureCodes.ApproverAmbiguous, decision.FailureCode);
        Assert.Null(decision.AssignedApproverId);
    }

    [Fact]
    public void RoleOnlyStep_TheCreatorIsNeverACandidate()
    {
        var other = Guid.NewGuid();

        var onlyCreator = ApprovalRoutingRules.ResolveRolePool(Guid.NewGuid(), Step(), Creator, [Creator]);
        var creatorAndOther = ApprovalRoutingRules.ResolveRolePool(Guid.NewGuid(), Step(), Creator, [Creator, other]);

        Assert.Equal(RoutingFailureCodes.ApproverNotFound, onlyCreator.FailureCode);
        Assert.Equal(other, creatorAndOther.AssignedApproverId);
    }
}
