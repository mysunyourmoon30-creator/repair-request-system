using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using RepairRequest.Application.Approvals;
using RepairRequest.Application.Common;
using RepairRequest.Application.MasterData;
using RepairRequest.Application.RepairRequests;
using RepairRequest.Application.Security;
using RepairRequest.Domain.Approvals;
using RepairRequest.Domain.Auditing;
using RepairRequest.Domain.MasterData;
using RepairRequest.Domain.Security;
using RepairRequest.Infrastructure.Persistence;

namespace RepairRequest.Infrastructure.Approvals;

/// <summary>
/// EF Core implementation of the approval route configuration port. Every caller-facing read starts from the caller's
/// tenant and yields nothing without tenant-wide configuration scope, inside the same SQL statement. Lists are projected
/// and paged server-side; the one-active-route check uses the filtered unique indexes' key.
/// </summary>
internal sealed class ApprovalRouteStore : IApprovalRouteStore
{
    private const int UniqueIndexViolation = 2601;
    private const int UniqueConstraintViolation = 2627;
    private const int DeadlockVictim = 1205;

    private readonly RepairRequestDbContext _db;

    public ApprovalRouteStore(RepairRequestDbContext db)
    {
        _db = db;
    }

    public async Task<PagedResult<ApprovalRouteDto>> ListAsync(CurrentUser user, MasterDataListQuery query, CancellationToken cancellationToken)
    {
        var source = Routes(user).AsNoTracking();
        if (query.Status is { } status)
        {
            var active = status == MasterDataStatus.Active;
            source = source.Where(route => route.IsActive == active);
        }

        var paging = query.Paging;
        var totalCount = await source.CountAsync(cancellationToken);
        var skip = (long)(paging.Page - 1) * paging.PageSize;

        IReadOnlyList<ApprovalRouteDto> items = skip >= totalCount
            ? []
            : await Project(source
                    .OrderBy(route => route.RequestCategoryCode)
                    .ThenBy(route => route.SiteId)
                    .ThenBy(route => route.Id))
                .Skip((int)skip)
                .Take(paging.PageSize)
                .ToListAsync(cancellationToken);

        return new PagedResult<ApprovalRouteDto>(items, paging.Page, paging.PageSize, totalCount);
    }

    public Task<ApprovalRouteDto?> GetAsync(CurrentUser user, Guid approvalRouteId, CancellationToken cancellationToken) =>
        Project(Routes(user).AsNoTracking().Where(route => route.Id == approvalRouteId))
            .SingleOrDefaultAsync(cancellationToken);

    public Task<ApprovalRoute?> FindAsync(CurrentUser user, Guid approvalRouteId, CancellationToken cancellationToken) =>
        Routes(user).SingleOrDefaultAsync(route => route.Id == approvalRouteId, cancellationToken);

    public Task<ApprovalRouteStep?> FindFirstStepAsync(Guid tenantId, Guid approvalRouteId, CancellationToken cancellationToken) =>
        _db.ApprovalRouteSteps
            .AsNoTracking()
            .SingleOrDefaultAsync(
                step => step.TenantId == tenantId && step.ApprovalRouteId == approvalRouteId && step.StepNo == ApprovalRouteStep.FirstStepNo,
                cancellationToken);

    // One statement of key seeks rooted on the caller's own user row (same pattern as the Draft selection check).
    public async Task<ApprovalRouteReferences> GetReferencesAsync(
        CurrentUser user,
        string requestCategoryCode,
        Guid? siteId,
        Guid? approverUserId,
        CancellationToken cancellationToken)
    {
        var tenantId = user.TenantId;
        var userId = user.UserId;
        var approverRole = RoleCodes.Approver;

        var row = await _db.Users
            .AsNoTracking()
            .Where(caller => caller.Id == userId && caller.TenantId == tenantId)
            .Select(caller => new
            {
                CategoryCode = _db.RequestCategories
                    .Where(category => category.TenantId == tenantId && category.Code == requestCategoryCode)
                    .Select(category => category.Code)
                    .FirstOrDefault(),
                CategoryStatus = _db.RequestCategories
                    .Where(category => category.TenantId == tenantId && category.Code == requestCategoryCode)
                    .Select(category => (MasterDataStatus?)category.Status)
                    .FirstOrDefault(),
                SiteStatus = _db.Sites
                    .Where(site => site.TenantId == tenantId && site.Id == siteId)
                    .Select(site => (MasterDataStatus?)site.Status)
                    .FirstOrDefault(),
                ApproverIsApprover = _db.Users.Any(approver =>
                    approver.TenantId == tenantId
                    && approver.Id == approverUserId
                    && _db.UserRoles.Any(userRole =>
                        userRole.UserId == approver.Id
                        && _db.Roles.Any(role => role.Id == userRole.RoleId && role.Name == approverRole)))
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            return new ApprovalRouteReferences(null, null, false);
        }

        return new ApprovalRouteReferences(
            row.CategoryCode is null ? null : new LookupSelection(row.CategoryCode, row.CategoryStatus!.Value),
            row.SiteStatus,
            row.ApproverIsApprover);
    }

    public Task<bool> OtherActiveRouteExistsAsync(
        Guid tenantId,
        string requestCategoryCode,
        Guid? siteId,
        Guid excludeRouteId,
        CancellationToken cancellationToken)
    {
        var routes = _db.ApprovalRoutes.Where(route =>
            route.TenantId == tenantId
            && route.RequestCategoryCode == requestCategoryCode
            && route.IsActive
            && route.Id != excludeRouteId);

        // Branch in C#: a Site route never conflicts with the tenant default, and null is never a wildcard.
        routes = siteId is { } site
            ? routes.Where(route => route.SiteId == site)
            : routes.Where(route => route.SiteId == null);

        return routes.AnyAsync(cancellationToken);
    }

    public void Add(ApprovalRoute route) => _db.ApprovalRoutes.Add(route);

    public void AddStep(ApprovalRouteStep step) => _db.ApprovalRouteSteps.Add(step);

    public void AddAudit(AuditHistory audit) => _db.AuditHistory.Add(audit);

    public async Task<ApprovalRouteSaveOutcome> SaveChangesAsync(ApprovalRoute route, byte[]? expectedRowVersion, CancellationToken cancellationToken)
    {
        if (expectedRowVersion is not null)
        {
            // The UPDATE applies only WHERE row_version = the client's If-Match token.
            _db.Entry(route).Property(nameof(ApprovalRoute.RowVersion)).OriginalValue = expectedRowVersion;
        }

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return ApprovalRouteSaveOutcome.Saved;
        }
        catch (DbUpdateConcurrencyException)
        {
            _db.ChangeTracker.Clear();
            return ApprovalRouteSaveOutcome.ConcurrencyConflict;
        }
        catch (DbUpdateException exception) when (SqlErrorNumber(exception) is UniqueIndexViolation or UniqueConstraintViolation)
        {
            _db.ChangeTracker.Clear();
            return ApprovalRouteSaveOutcome.DuplicateActiveRoute;
        }
        catch (DbUpdateException exception) when (SqlErrorNumber(exception) is DeadlockVictim)
        {
            _db.ChangeTracker.Clear();
            return ApprovalRouteSaveOutcome.ConcurrencyConflict;
        }
    }

    private IQueryable<ApprovalRoute> Routes(CurrentUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var tenantId = user.TenantId;

        var routes = _db.ApprovalRoutes.Where(route => route.TenantId == tenantId);
        return user.HasTenantWideConfigurationScope ? routes : routes.Where(_ => false);
    }

    private IQueryable<ApprovalRouteDto> Project(IQueryable<ApprovalRoute> routes) =>
        from route in routes
        join step in _db.ApprovalRouteSteps
            on new { route.TenantId, RouteId = route.Id, StepNo = ApprovalRouteStep.FirstStepNo }
            equals new { step.TenantId, RouteId = step.ApprovalRouteId, step.StepNo }
        select new ApprovalRouteDto(
            route.Id,
            route.RequestCategoryCode,
            route.SiteId,
            route.IsActive ? MasterDataStatus.Active : MasterDataStatus.Inactive,
            step.StepNo,
            step.ApproverRoleCode,
            step.ApproverUserId,
            route.RowVersion);

    private static int? SqlErrorNumber(Exception exception) =>
        exception switch
        {
            SqlException sql => sql.Number,
            { InnerException: SqlException inner } => inner.Number,
            _ => null
        };
}
