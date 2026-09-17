using RepairRequest.Domain.Common;
using RepairRequest.Domain.Security;

namespace RepairRequest.Domain.Approvals;

/// <summary>
/// One ordered approver step of an <see cref="ApprovalRoute"/> (RR-DD-001 ARC-005..007; RR-DBD-001 approval_route_step,
/// UNIQUE(route, step_no)). S1-007R supports a single step only (DEC-PRE-S1-007R-03). The approver role may only be
/// APPROVER (DEC-PRE-S1-007R-05); the optional specific approver must be an APPROVER of the same tenant. Steps are
/// immutable once created.
/// </summary>
public sealed class ApprovalRouteStep
{
    public const short FirstStepNo = 1;
    public const int RoleCodeMaxLength = 30;

    /// <summary>EF Core materialization.</summary>
    private ApprovalRouteStep()
    {
        ApproverRoleCode = null!;
    }

    private ApprovalRouteStep(ApprovalRoute route, short stepNo, string approverRoleCode, Guid? approverUserId)
    {
        ArgumentNullException.ThrowIfNull(route);

        TenantId = route.TenantId;
        ApprovalRouteId = DomainGuard.NotEmpty(route.Id, nameof(route));
        StepNo = stepNo >= FirstStepNo ? stepNo : throw new ArgumentOutOfRangeException(nameof(stepNo));

        if (!string.Equals(approverRoleCode, RoleCodes.Approver, StringComparison.Ordinal))
        {
            throw new ArgumentException("The approver role must be APPROVER.", nameof(approverRoleCode));
        }

        ApproverRoleCode = approverRoleCode;

        if (approverUserId is { } userId)
        {
            DomainGuard.NotEmpty(userId, nameof(approverUserId));
        }

        ApproverUserId = approverUserId;
    }

    /// <summary>The single step of a route. The route must already have its identifier (added to the unit of work).</summary>
    public static ApprovalRouteStep CreateFirstStep(ApprovalRoute route, string approverRoleCode, Guid? approverUserId) =>
        new(route, FirstStepNo, approverRoleCode, approverUserId);

    public Guid TenantId { get; private set; }

    public Guid ApprovalRouteId { get; private set; }

    /// <summary>ARC-005. Unique per route.</summary>
    public short StepNo { get; private set; }

    /// <summary>ARC-006. Authorized role; APPROVER only.</summary>
    public string ApproverRoleCode { get; private set; }

    /// <summary>ARC-007. Optional specific approver.</summary>
    public Guid? ApproverUserId { get; private set; }
}
