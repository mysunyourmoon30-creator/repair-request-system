namespace RepairRequest.Infrastructure.Identity;

/// <summary>
/// Site data scope assignment for a user (DEC-PS1-004). The user and the Site must
/// belong to the same Tenant; this is enforced by composite foreign keys so a scope
/// row can never grant cross-tenant access (D-11 / BR-16).
/// </summary>
public sealed class UserSiteScope
{
    private UserSiteScope()
    {
    }

    public UserSiteScope(Guid tenantId, Guid userId, Guid siteId)
    {
        TenantId = tenantId == Guid.Empty ? throw new ArgumentException("Tenant is required.", nameof(tenantId)) : tenantId;
        UserId = userId == Guid.Empty ? throw new ArgumentException("User is required.", nameof(userId)) : userId;
        SiteId = siteId == Guid.Empty ? throw new ArgumentException("Site is required.", nameof(siteId)) : siteId;
    }

    public Guid TenantId { get; private set; }

    public Guid UserId { get; private set; }

    public Guid SiteId { get; private set; }
}
