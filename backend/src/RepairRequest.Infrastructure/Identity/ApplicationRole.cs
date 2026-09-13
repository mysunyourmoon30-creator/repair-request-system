using Microsoft.AspNetCore.Identity;

namespace RepairRequest.Infrastructure.Identity;

/// <summary>
/// Identity role used for role/permission authorization (DEC-PS1-004).
/// Role catalog seeding is not part of S1-001.
/// </summary>
public class ApplicationRole : IdentityRole<Guid>
{
}
