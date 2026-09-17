namespace RepairRequest.Domain.Approvals;

/// <summary>
/// Routing failure results (RR-DD-001 APR-011 routing_failure_code; DEC-PRE-S1-007R-09). The request stays SUBMITTED
/// for every code. Route-level codes write no repair_request_approval row; approver-level codes write a row with the
/// resolved route and step and no assigned approver.
/// </summary>
public static class RoutingFailureCodes
{
    public const int MaxLength = 100;

    /// <summary>No active Site-specific or tenant-default route for the request's Category.</summary>
    public const string RouteNotFound = "ROUTE_NOT_FOUND";

    /// <summary>More than one active route at the selected level (never resolved by picking one).</summary>
    public const string RouteAmbiguous = "ROUTE_AMBIGUOUS";

    /// <summary>The selected route does not have exactly one valid APPROVER step (single-step MVP, DEC-PRE-S1-007R-03).</summary>
    public const string RouteStepInvalid = "ROUTE_STEP_INVALID";

    /// <summary>The step's specific approver is not an eligible APPROVER with Site scope, or is the request creator.</summary>
    public const string ApproverNotEligible = "APPROVER_NOT_ELIGIBLE";

    /// <summary>No eligible APPROVER with Site scope (other than the request creator).</summary>
    public const string ApproverNotFound = "APPROVER_NOT_FOUND";

    /// <summary>More than one eligible APPROVER: no selection rule exists, so none is assigned.</summary>
    public const string ApproverAmbiguous = "APPROVER_AMBIGUOUS";

    public static IReadOnlyList<string> RouteLevel { get; } = [RouteNotFound, RouteAmbiguous, RouteStepInvalid];

    public static IReadOnlyList<string> ApproverLevel { get; } = [ApproverNotEligible, ApproverNotFound, ApproverAmbiguous];
}
