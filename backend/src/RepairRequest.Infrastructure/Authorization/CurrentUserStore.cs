using Microsoft.EntityFrameworkCore;
using RepairRequest.Application.Security;
using RepairRequest.Infrastructure.Persistence;

namespace RepairRequest.Infrastructure.Authorization;

/// <summary>
/// Resolves tenant and role codes for a user from the Identity store in a single SQL command
/// (AspNetUsers primary key + AspNetUserRoles key seek). Role claims in the access token are not trusted.
/// </summary>
internal sealed class CurrentUserStore : ICurrentUserStore
{
    private readonly RepairRequestDbContext _db;

    public CurrentUserStore(RepairRequestDbContext db)
    {
        _db = db;
    }

    public async Task<CurrentUser?> FindAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (userId == Guid.Empty)
        {
            return null;
        }

        var resolved = await _db.Users
            .AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => new
            {
                user.TenantId,
                RoleCodes = _db.UserRoles
                    .Where(userRole => userRole.UserId == user.Id)
                    .Join(_db.Roles, userRole => userRole.RoleId, role => role.Id, (_, role) => role.Name!)
                    .ToList()
            })
            .SingleOrDefaultAsync(cancellationToken);

        return resolved is null ? null : new CurrentUser(userId, resolved.TenantId, resolved.RoleCodes);
    }
}
