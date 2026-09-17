using RepairRequest.Domain.RepairRequests;

namespace RepairRequest.Application.Approvals;

/// <summary>What started a routing attempt (recorded in the routing audit).</summary>
public enum RoutingTrigger
{
    /// <summary>Right after a successful Submit commit (DEC-PRE-S1-007R-01).</summary>
    Submit,

    /// <summary>ADMINISTRATOR Retry Routing recovery command (DEC-PRE-S1-007R-07).</summary>
    AdminRetry
}

/// <summary>One configured step of an active route.</summary>
public sealed record RouteStepCandidate(short StepNo, string ApproverRoleCode, Guid? ApproverUserId);

/// <summary>An active route matching the request's tenant and Category, either for its Site or as the tenant default.</summary>
public sealed record RouteCandidate(Guid RouteId, bool IsSiteSpecific, IReadOnlyList<RouteStepCandidate> Steps);

/// <summary>The selected route and step, or a route-level routing failure.</summary>
public sealed record RouteSelection(Guid? RouteId, RouteStepCandidate? Step, string? FailureCode)
{
    public static RouteSelection Selected(Guid routeId, RouteStepCandidate step) => new(routeId, step, null);

    public static RouteSelection Failed(string failureCode) => new(null, null, failureCode);
}

/// <summary>
/// Routing outcome. Assigned: route, step and approver. Approver-level failure: route, step and a failure code (an
/// approval row is written). Route-level failure: only a failure code (no approval row; DEC-PRE-S1-007R-09).
/// </summary>
public sealed record RoutingDecision(Guid? RouteId, short? StepNo, Guid? AssignedApproverId, string? FailureCode)
{
    public static RoutingDecision Assigned(Guid routeId, short stepNo, Guid approverId) => new(routeId, stepNo, approverId, null);

    public static RoutingDecision ApproverFailure(Guid routeId, short stepNo, string failureCode) => new(routeId, stepNo, null, failureCode);

    public static RoutingDecision RouteFailure(string failureCode) => new(null, null, null, failureCode);
}

/// <summary>Result of one routing attempt: the request's resulting state and, on failure, the recorded code.</summary>
public sealed record RoutingResultDto(Guid RepairRequestId, RepairRequestStatus Status, string? RoutingFailureCode, byte[] RowVersion);

/// <summary>
/// Routing-issue list item for ADMINISTRATOR recovery (DEC-PRE-S1-007R-10): routing metadata only. It never carries the
/// problem description, requester or contact identity, attachments or audit history.
/// </summary>
public sealed record RoutingIssueDto(
    Guid RepairRequestId,
    string? RequestNo,
    Guid? SiteId,
    string? RequestCategoryCode,
    DateTime? SubmittedAt,
    string? LastRoutingFailureCode,
    byte[] RowVersion);
