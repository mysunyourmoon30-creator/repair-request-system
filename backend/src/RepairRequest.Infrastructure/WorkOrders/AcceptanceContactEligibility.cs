using Microsoft.EntityFrameworkCore;
using RepairRequest.Domain.Security;
using RepairRequest.Infrastructure.Identity;
using RepairRequest.Infrastructure.Persistence;

namespace RepairRequest.Infrastructure.WorkOrders;

/// <summary>
/// Shared Acceptance Contact eligibility query (`docs/13` §4.16 Decision 1): an ApplicationUser of the tenant,
/// holding REQUESTER, Site-scoped to the given Site — mirrors <see cref="TechnicianEligibility"/> exactly, for
/// the same role/tenant/site shape, but for REQUESTER instead of TECHNICIAN. The Active-user portion (CAC-009)
/// remains formally deferred per DEC-PRE-S1-007-04; only existence, tenant, Site scope and role are checked.
/// </summary>
internal static class AcceptanceContactEligibility
{
    public static IQueryable<ApplicationUser> EligibleUsers(RepairRequestDbContext db, Guid tenantId, Guid siteId) =>
        db.Users.Where(user =>
            user.TenantId == tenantId
            && db.UserSiteScopes.Any(scope => scope.TenantId == tenantId && scope.UserId == user.Id && scope.SiteId == siteId)
            && db.UserRoles.Any(userRole =>
                userRole.UserId == user.Id
                && db.Roles.Any(role => role.Id == userRole.RoleId && role.Name == RoleCodes.Requester)));

    public static Task<bool> IsEligibleAsync(RepairRequestDbContext db, Guid tenantId, Guid userId, Guid siteId, CancellationToken cancellationToken) =>
        EligibleUsers(db, tenantId, siteId).AnyAsync(user => user.Id == userId, cancellationToken);
}
