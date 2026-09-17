using RepairRequest.Domain.Approvals;
using RepairRequest.Domain.Security;

namespace RepairRequest.Application.Approvals;

/// <summary>
/// Pure ST-RR-003 routing rules approved for S1-007R (DEC-PRE-S1-007R-02/03/05/06). No selection is invented: ambiguity
/// is always a routing failure.
/// <list type="number">
/// <item>Route precedence: the active Site-specific route, else the active tenant-default route, else ROUTE_NOT_FOUND.
/// More than one active route at the chosen level is ROUTE_AMBIGUOUS (never First()).</item>
/// <item>Single step only: the route must have exactly one step, number 1, role APPROVER; otherwise ROUTE_STEP_INVALID.</item>
/// <item>Specific approver: must be an eligible APPROVER with business Site scope and must not be the request creator;
/// otherwise APPROVER_NOT_ELIGIBLE.</item>
/// <item>Role-only step: exactly one eligible APPROVER with business Site scope other than the creator; none is
/// APPROVER_NOT_FOUND, several is APPROVER_AMBIGUOUS.</item>
/// </list>
/// The ACTIVE user-state check for approvers is deferred to REQ-FU-USR-001.
/// </summary>
public static class ApprovalRoutingRules
{
    /// <summary>Candidates read for a role-only step: two are enough to distinguish "exactly one" from "several".</summary>
    public const int CandidateProbeSize = 2;

    public static RouteSelection SelectRoute(IReadOnlyCollection<RouteCandidate> activeRoutes)
    {
        ArgumentNullException.ThrowIfNull(activeRoutes);

        var siteRoutes = activeRoutes.Where(route => route.IsSiteSpecific).ToList();
        var level = siteRoutes.Count > 0 ? siteRoutes : activeRoutes.Where(route => !route.IsSiteSpecific).ToList();

        if (level.Count == 0)
        {
            return RouteSelection.Failed(RoutingFailureCodes.RouteNotFound);
        }

        if (level.Count > 1)
        {
            return RouteSelection.Failed(RoutingFailureCodes.RouteAmbiguous);
        }

        var route = level[0];
        if (route.Steps.Count != 1)
        {
            return RouteSelection.Failed(RoutingFailureCodes.RouteStepInvalid);
        }

        var step = route.Steps[0];
        if (step.StepNo != ApprovalRouteStep.FirstStepNo || !string.Equals(step.ApproverRoleCode, RoleCodes.Approver, StringComparison.Ordinal))
        {
            return RouteSelection.Failed(RoutingFailureCodes.RouteStepInvalid);
        }

        return RouteSelection.Selected(route.RouteId, step);
    }

    /// <summary>Decision for a step with a specific approver, given that user's eligibility at the request Site.</summary>
    public static RoutingDecision ResolveSpecificApprover(Guid routeId, RouteStepCandidate step, Guid createdBy, bool approverEligible)
    {
        ArgumentNullException.ThrowIfNull(step);

        return step.ApproverUserId is { } approverId && approverId != createdBy && approverEligible
            ? RoutingDecision.Assigned(routeId, step.StepNo, approverId)
            : RoutingDecision.ApproverFailure(routeId, step.StepNo, RoutingFailureCodes.ApproverNotEligible);
    }

    /// <summary>Decision for a role-only step from the eligible candidates (already excluding the creator).</summary>
    public static RoutingDecision ResolveRolePool(Guid routeId, RouteStepCandidate step, Guid createdBy, IReadOnlyCollection<Guid> eligibleCandidates)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(eligibleCandidates);

        // Defense in depth: the creator is never assigned, even if a store returned it (segregation of duties, DEC-PRE-S1-008-01).
        var candidates = eligibleCandidates.Where(candidate => candidate != createdBy).Distinct().ToList();

        return candidates.Count switch
        {
            0 => RoutingDecision.ApproverFailure(routeId, step.StepNo, RoutingFailureCodes.ApproverNotFound),
            1 => RoutingDecision.Assigned(routeId, step.StepNo, candidates[0]),
            _ => RoutingDecision.ApproverFailure(routeId, step.StepNo, RoutingFailureCodes.ApproverAmbiguous)
        };
    }
}
