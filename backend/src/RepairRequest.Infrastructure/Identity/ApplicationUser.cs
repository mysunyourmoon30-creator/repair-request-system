using Microsoft.AspNetCore.Identity;

namespace RepairRequest.Infrastructure.Identity;

/// <summary>
/// Application identity store user (DEC-PS1-004: ASP.NET Core Identity; external
/// IdP/OIDC superseded for MVP). Password hashing, lockout and security stamp are
/// provided by Identity. Company data scope is the server-derived tenant; site data
/// scope is held in <see cref="UserSiteScope"/>. Kept in Infrastructure so the
/// Domain layer stays free of identity framework dependencies (RR-ARCH-001 section 5.1).
/// </summary>
public class ApplicationUser : IdentityUser<Guid>
{
    /// <summary>Company data scope (D-11). Immutable once persisted.</summary>
    public Guid TenantId { get; set; }
}
