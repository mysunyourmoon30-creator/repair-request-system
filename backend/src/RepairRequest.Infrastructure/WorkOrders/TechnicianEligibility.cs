using Microsoft.EntityFrameworkCore;
using RepairRequest.Domain.Security;
using RepairRequest.Infrastructure.Persistence;

namespace RepairRequest.Infrastructure.WorkOrders;

/// <summary>
/// Shared technician-eligibility check for Schedule and the Service Visit actions (S2-003): an ApplicationUser of
/// the tenant, holding TECHNICIAN, Site-scoped to the given Site. "Team" has no equivalent check — no Team
/// master-data entity exists (see <see cref="RepairRequest.Domain.WorkOrders.ServiceVisit"/>'s own doc comment).
/// </summary>
internal static class TechnicianEligibility
{
    public static Task<bool> IsEligibleAsync(RepairRequestDbContext db, Guid tenantId, Guid technicianId, Guid siteId, CancellationToken cancellationToken) =>
        db.Users.AnyAsync(user =>
            user.TenantId == tenantId
            && user.Id == technicianId
            && db.UserSiteScopes.Any(scope => scope.TenantId == tenantId && scope.UserId == user.Id && scope.SiteId == siteId)
            && db.UserRoles.Any(userRole =>
                userRole.UserId == user.Id
                && db.Roles.Any(role => role.Id == userRole.RoleId && role.Name == RoleCodes.Technician)),
            cancellationToken);
}
