using RepairRequest.Domain.Security;

namespace RepairRequest.Application.Security;

/// <summary>
/// The authenticated caller as resolved from the identity store for the current request.
/// Tenant and roles always come from the database, never from client input or token role
/// claims (D-11 / BR-16; RR-ARCH-001 section 10). Only approved role codes are recognised.
/// </summary>
public sealed class CurrentUser
{
    public CurrentUser(Guid userId, Guid tenantId, IEnumerable<string> roleCodes)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("User identifier is required.", nameof(userId));
        }

        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("Tenant identifier is required.", nameof(tenantId));
        }

        ArgumentNullException.ThrowIfNull(roleCodes);

        UserId = userId;
        TenantId = tenantId;
        Roles = roleCodes.Where(RoleCodes.All.Contains).ToHashSet(StringComparer.Ordinal);
    }

    public Guid UserId { get; }

    public Guid TenantId { get; }

    public IReadOnlySet<string> Roles { get; }

    public bool IsInRole(string roleCode) => Roles.Contains(roleCode);

    public bool IsInAnyRole(IEnumerable<string> roleCodes) => roleCodes.Any(Roles.Contains);

    /// <summary>
    /// S1-003 decision 2: ADMINISTRATOR configures master data across all Sites of its own tenant.
    /// It never grants cross-tenant access and never grants Repair Request data access by itself.
    /// </summary>
    public bool HasTenantWideConfigurationScope => IsInRole(RoleCodes.Administrator);

    /// <summary>Holds at least one business role (every approved role except ADMINISTRATOR).</summary>
    public bool HasBusinessRole => Roles.Any(role => role != RoleCodes.Administrator);

    /// <summary>
    /// Holds a business role that sees every Repair Request at its assigned Sites. The REQUESTER role
    /// alone is limited to its own data (RR-REQ-001 section 3).
    /// </summary>
    public bool HasSiteWideRequestScope =>
        Roles.Any(role => role != RoleCodes.Administrator && role != RoleCodes.Requester);

    public bool IsRequester => IsInRole(RoleCodes.Requester);
}
