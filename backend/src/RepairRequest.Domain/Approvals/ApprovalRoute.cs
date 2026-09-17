using RepairRequest.Domain.Common;

namespace RepairRequest.Domain.Approvals;

/// <summary>
/// Approval routing master header (RR-DD-001 ARC-001..004, ARC-008; RR-DBD-001 approval_route; ERD-DR-02). A route maps a
/// tenant Category, optionally narrowed to one Site (null = tenant default), to its ordered approver steps
/// (<see cref="ApprovalRouteStep"/>). At most one ACTIVE route may exist per tenant + Category + Site and per tenant +
/// Category default (DEC-PRE-S1-007R-06); that rule is checked by the Application layer and enforced by filtered unique
/// indexes. Routes are configuration only: they never grant Approve/Reject by themselves.
/// </summary>
public sealed class ApprovalRoute
{
    public const int CategoryCodeMaxLength = 30;

    /// <summary>EF Core materialization.</summary>
    private ApprovalRoute()
    {
        RequestCategoryCode = null!;
    }

    private ApprovalRoute(Guid tenantId, string requestCategoryCode, Guid? siteId)
    {
        TenantId = DomainGuard.NotEmpty(tenantId, nameof(tenantId));
        RequestCategoryCode = DomainGuard.RequiredText(requestCategoryCode, CategoryCodeMaxLength, nameof(requestCategoryCode));

        if (siteId is { } site)
        {
            DomainGuard.NotEmpty(site, nameof(siteId));
        }

        SiteId = siteId;
        IsActive = true;
    }

    /// <summary>A new ACTIVE route. Its single step is added with <see cref="ApprovalRouteStep.CreateFirstStep"/>.</summary>
    public static ApprovalRoute Create(Guid tenantId, string requestCategoryCode, Guid? siteId) =>
        new(tenantId, requestCategoryCode, siteId);

    /// <summary>ARC-001. Assigned on insert (sequential GUID).</summary>
    public Guid Id { get; private set; }

    /// <summary>ARC-002. Server-derived tenant scope; immutable.</summary>
    public Guid TenantId { get; private set; }

    /// <summary>ARC-003. Canonical tenant Category code the route applies to.</summary>
    public string RequestCategoryCode { get; private set; }

    /// <summary>ARC-004. Specific Site, or null for the tenant default.</summary>
    public Guid? SiteId { get; private set; }

    /// <summary>ARC-008. Inactive routes are never selected.</summary>
    public bool IsActive { get; private set; }

    public byte[] RowVersion { get; private set; } = [];

    /// <summary>INACTIVE -> ACTIVE. The one-active-route-per-key rule is an Application-layer check plus a unique index.</summary>
    public void Activate()
    {
        if (IsActive)
        {
            throw new DomainRuleViolationException("The approval route is already active.");
        }

        IsActive = true;
    }

    /// <summary>ACTIVE -> INACTIVE. ARC-008 is a flag only; no deactivation reason is defined by the baseline.</summary>
    public void Deactivate()
    {
        if (!IsActive)
        {
            throw new DomainRuleViolationException("The approval route is already inactive.");
        }

        IsActive = false;
    }
}
