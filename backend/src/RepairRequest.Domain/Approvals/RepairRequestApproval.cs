using RepairRequest.Domain.Common;

namespace RepairRequest.Domain.Approvals;

/// <summary>
/// Approval assignment and decision history of one Repair Request step (RR-DD-001 APR-001..011; RR-DBD-001
/// repair_request_approval, UNIQUE(request, step)). S1-007R writes a PENDING row when routing either assigns exactly one
/// approver or resolves a route and step but cannot assign one (routing_failure_code set, no approver;
/// DEC-PRE-S1-007R-09). A row is either assigned or carries a routing failure, never both. Decisions (APPROVED /
/// REJECTED / RETURNED_FOR_CORRECTION) are not made here.
/// </summary>
public sealed class RepairRequestApproval
{
    public const int DecisionReasonMaxLength = 1000;

    /// <summary>EF Core materialization.</summary>
    private RepairRequestApproval()
    {
    }

    private RepairRequestApproval(Guid tenantId, Guid repairRequestId, Guid approvalRouteId, short approvalStepNo)
    {
        TenantId = DomainGuard.NotEmpty(tenantId, nameof(tenantId));
        RepairRequestId = DomainGuard.NotEmpty(repairRequestId, nameof(repairRequestId));
        Status = ApprovalStatus.Pending;
        SetRoute(approvalRouteId, approvalStepNo);
    }

    /// <summary>Routing assigned exactly one eligible approver.</summary>
    public static RepairRequestApproval Assigned(
        Guid tenantId,
        Guid repairRequestId,
        Guid approvalRouteId,
        short approvalStepNo,
        Guid assignedApproverId,
        DateTime routedAt)
    {
        var approval = new RepairRequestApproval(tenantId, repairRequestId, approvalRouteId, approvalStepNo);
        approval.Assign(assignedApproverId, routedAt);
        return approval;
    }

    /// <summary>Routing resolved a route and step but could not assign an approver (APR-006 "missing -> routing failure").</summary>
    public static RepairRequestApproval AssignmentFailed(
        Guid tenantId,
        Guid repairRequestId,
        Guid approvalRouteId,
        short approvalStepNo,
        string routingFailureCode)
    {
        var approval = new RepairRequestApproval(tenantId, repairRequestId, approvalRouteId, approvalStepNo);
        approval.Fail(routingFailureCode);
        return approval;
    }

    /// <summary>APR-001. Assigned on insert (sequential GUID).</summary>
    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    /// <summary>APR-003.</summary>
    public Guid RepairRequestId { get; private set; }

    /// <summary>APR-004. Always set.</summary>
    public Guid ApprovalRouteId { get; private set; }

    /// <summary>APR-005. Always set; unique per request.</summary>
    public short ApprovalStepNo { get; private set; }

    /// <summary>APR-006. Null only together with a routing failure code.</summary>
    public Guid? AssignedApproverId { get; private set; }

    /// <summary>APR-007.</summary>
    public ApprovalStatus Status { get; private set; }

    /// <summary>APR-008. Required for Reject/Return in later tickets.</summary>
    public string? DecisionReason { get; private set; }

    /// <summary>APR-009. System time of the successful assignment.</summary>
    public DateTime? RoutedAt { get; private set; }

    /// <summary>APR-010.</summary>
    public DateTime? DecidedAt { get; private set; }

    /// <summary>APR-011. Set while the step has no assigned approver.</summary>
    public string? RoutingFailureCode { get; private set; }

    public byte[] RowVersion { get; private set; } = [];

    public bool IsAssignedPending => Status == ApprovalStatus.Pending && AssignedApproverId is not null;

    /// <summary>Admin retry (DEC-PRE-S1-007R-07) succeeded on a step that previously failed assignment.</summary>
    public void AssignOnRetry(Guid approvalRouteId, short approvalStepNo, Guid assignedApproverId, DateTime routedAt)
    {
        EnsureUnassignedPending();
        SetRoute(approvalRouteId, approvalStepNo);
        Assign(assignedApproverId, routedAt);
    }

    /// <summary>Admin retry resolved a route and step again but still could not assign an approver.</summary>
    public void FailOnRetry(Guid approvalRouteId, short approvalStepNo, string routingFailureCode)
    {
        EnsureUnassignedPending();
        SetRoute(approvalRouteId, approvalStepNo);
        Fail(routingFailureCode);
    }

    /// <summary>ST-RR-004 decision of the assigned approver (APR-007 APPROVED, APR-010 decided_at). A decided step is final.</summary>
    public void Approve(DateTime decidedAt)
    {
        EnsureDecidable();
        DecidedAt = DomainGuard.Utc(decidedAt, nameof(decidedAt));
        Status = ApprovalStatus.Approved;
    }

    /// <summary>ST-RR-005 decision with the required reason (APR-007 REJECTED, APR-008 decision_reason, APR-010 decided_at).</summary>
    public void Reject(string reason, DateTime decidedAt)
    {
        EnsureDecidable();
        DecisionReason = DomainGuard.RequiredText(reason, DecisionReasonMaxLength, nameof(reason));
        DecidedAt = DomainGuard.Utc(decidedAt, nameof(decidedAt));
        Status = ApprovalStatus.Rejected;
    }

    private void EnsureDecidable()
    {
        if (!IsAssignedPending)
        {
            throw new DomainRuleViolationException("Only a pending approval step with an assigned approver can be decided.");
        }
    }

    /// <summary>
    /// A later routing attempt ended in a route-level failure (no route or no valid step): this unassigned failure row no
    /// longer describes the current routing state and is removed (DEC-PRE-S1-007R-09, route-level failure has no row). Its
    /// history stays in the append-only routing audit. Only a PENDING row without an assigned approver and with a
    /// routing failure qualifies; an assigned or decided approval is never removable.
    /// </summary>
    public void EnsureDiscardableRoutingFailure()
    {
        if (Status != ApprovalStatus.Pending || AssignedApproverId is not null || RoutingFailureCode is null)
        {
            throw new DomainRuleViolationException("Only an unassigned pending approval step with a routing failure can be removed.");
        }
    }

    private void SetRoute(Guid approvalRouteId, short approvalStepNo)
    {
        ApprovalRouteId = DomainGuard.NotEmpty(approvalRouteId, nameof(approvalRouteId));
        ApprovalStepNo = approvalStepNo >= ApprovalRouteStep.FirstStepNo
            ? approvalStepNo
            : throw new ArgumentOutOfRangeException(nameof(approvalStepNo));
    }

    private void Assign(Guid assignedApproverId, DateTime routedAt)
    {
        AssignedApproverId = DomainGuard.NotEmpty(assignedApproverId, nameof(assignedApproverId));
        RoutedAt = DomainGuard.Utc(routedAt, nameof(routedAt));
        RoutingFailureCode = null;
    }

    private void Fail(string routingFailureCode)
    {
        var code = DomainGuard.RequiredText(routingFailureCode, RoutingFailureCodes.MaxLength, nameof(routingFailureCode));
        if (!RoutingFailureCodes.ApproverLevel.Contains(code))
        {
            throw new ArgumentException("Only approver-level routing failures are recorded on an approval row.", nameof(routingFailureCode));
        }

        AssignedApproverId = null;
        RoutedAt = null;
        RoutingFailureCode = code;
    }

    private void EnsureUnassignedPending()
    {
        if (Status != ApprovalStatus.Pending || AssignedApproverId is not null)
        {
            throw new DomainRuleViolationException("Only a pending approval step without an assigned approver can be routed again.");
        }
    }
}
