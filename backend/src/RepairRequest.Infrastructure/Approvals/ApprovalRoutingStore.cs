using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using RepairRequest.Application.Approvals;
using RepairRequest.Application.Common;
using RepairRequest.Application.RepairRequests;
using RepairRequest.Application.Security;
using RepairRequest.Domain.Approvals;
using RepairRequest.Domain.Auditing;
using RepairRequest.Domain.RepairRequests;
using RepairRequest.Domain.Security;
using RepairRequest.Infrastructure.Persistence;
using RepairRequestAggregate = RepairRequest.Domain.RepairRequests.RepairRequest;

namespace RepairRequest.Infrastructure.Approvals;

/// <summary>
/// EF Core implementation of the routing port.
/// <list type="bullet">
/// <item>Serialization: a transaction-owned application lock on the request's routing key with a bounded wait
/// (<see cref="RepairRequestRoutingLock"/>), then UPDLOCK + HOLDLOCK on the repair_request primary key for the whole
/// attempt, so post-Submit routing and concurrent Admin Retries of one request run one after another. Other requests are
/// never blocked. A lock that is not granted within the wait limit writes nothing and becomes a concurrency conflict (409),
/// never an uncontrolled failure. Lock order is always routing key, request row, then approval row.</item>
/// <item>Route lookup: tenant + Category + is_active filter served by the filtered unique route indexes; steps by the
/// approval_route_step primary key. No graphs are loaded.</item>
/// <item>Eligibility: EXISTS / TOP(n) over user_site_scope keys and the Identity user-role key; the creator is excluded in
/// SQL.</item>
/// <item>Double assignment: UNIQUE(repair_request_id, approval_step_no) is the database backstop.</item>
/// </list>
/// </summary>
internal sealed class ApprovalRoutingStore : IApprovalRoutingStore
{
    private const int UniqueIndexViolation = 2601;
    private const int UniqueConstraintViolation = 2627;
    private const int DeadlockVictim = 1205;
    private const int LockRequestTimeout = 1222;

    private readonly RepairRequestDbContext _db;
    private readonly IDataScope _scope;
    private readonly RepairRequestRoutingLockOptions _lockOptions;

    public ApprovalRoutingStore(RepairRequestDbContext db, IDataScope scope, RepairRequestRoutingLockOptions lockOptions)
    {
        _db = db;
        _scope = scope;
        _lockOptions = lockOptions;
    }

    /// <summary>Lock contention only: the routing key was not granted within the configured wait. Never a database failure.</summary>
    private sealed class RoutingLockUnavailableException : Exception;

    public async Task<CommandResult<T>> RunInTransactionAsync<T>(Func<Task<CommandResult<T>>> command, CancellationToken cancellationToken)
    {
        try
        {
            // Disposing without commit rolls back, including after the server has already aborted a deadlock victim.
            await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

            var result = await command();
            if (result.Succeeded)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            return result;
        }
        catch (RoutingLockUnavailableException)
        {
            // Bounded wait exceeded: nothing was written, and the caller may simply retry (DEC-PRE-S1-007R-07).
            _db.ChangeTracker.Clear();
            return CommandError.ConcurrencyConflict;
        }
        catch (Exception exception) when (SqlErrorNumber(exception) is DeadlockVictim or LockRequestTimeout)
        {
            _db.ChangeTracker.Clear();
            return CommandError.ConcurrencyConflict;
        }
    }

    public async Task<RepairRequestAggregate?> LockRequestAsync(Guid tenantId, Guid repairRequestId, CancellationToken cancellationToken)
    {
        await AcquireRoutingLockAsync(repairRequestId, cancellationToken);

        var locked = await _db.Database.SqlQuery<int>($"""
            SELECT 1 AS [Value]
            FROM [repair_request] WITH (UPDLOCK, HOLDLOCK, ROWLOCK)
            WHERE [repair_request_id] = {repairRequestId} AND [tenant_id] = {tenantId}
            """).ToListAsync(cancellationToken);

        if (locked.Count == 0)
        {
            return null;
        }

        // A request tracked by this unit of work (the Submit that was just committed) is refreshed from the locked row, so
        // its state and row version are the current database values.
        var tracked = _db.RepairRequests.Local.FirstOrDefault(request => request.Id == repairRequestId && request.TenantId == tenantId);
        if (tracked is not null)
        {
            await _db.Entry(tracked).ReloadAsync(cancellationToken);
            return tracked;
        }

        return await _db.RepairRequests.SingleOrDefaultAsync(
            request => request.Id == repairRequestId && request.TenantId == tenantId,
            cancellationToken);
    }

    /// <summary>
    /// Exclusive, transaction-owned application lock on the request's routing key. A result &lt; 0 means the configured wait
    /// elapsed or this session was chosen as a deadlock victim; the attempt then ends as a concurrency conflict without
    /// writing anything and without retrying. Routing attempts for other requests use other keys and never wait here.
    /// </summary>
    private async Task AcquireRoutingLockAsync(Guid repairRequestId, CancellationToken cancellationToken)
    {
        if (!await RepairRequestRoutingLock.TryAcquireAsync(_db, repairRequestId, _lockOptions, cancellationToken))
        {
            throw new RoutingLockUnavailableException();
        }
    }

    public Task<RepairRequestApproval?> FindStepApprovalAsync(Guid tenantId, Guid repairRequestId, short stepNo, CancellationToken cancellationToken) =>
        _db.RepairRequestApprovals.SingleOrDefaultAsync(
            approval => approval.RepairRequestId == repairRequestId && approval.ApprovalStepNo == stepNo && approval.TenantId == tenantId,
            cancellationToken);

    public async Task<IReadOnlyList<RouteCandidate>> ListActiveRoutesAsync(
        Guid tenantId,
        string requestCategoryCode,
        Guid siteId,
        CancellationToken cancellationToken)
    {
        var site = (Guid?)siteId;

        var routes = await _db.ApprovalRoutes
            .AsNoTracking()
            .Where(route =>
                route.TenantId == tenantId
                && route.RequestCategoryCode == requestCategoryCode
                && route.IsActive
                && (route.SiteId == site || route.SiteId == null))
            .Select(route => new
            {
                route.Id,
                route.SiteId,
                Steps = _db.ApprovalRouteSteps
                    .Where(step => step.TenantId == tenantId && step.ApprovalRouteId == route.Id)
                    .OrderBy(step => step.StepNo)
                    .Select(step => new RouteStepCandidate(step.StepNo, step.ApproverRoleCode, step.ApproverUserId))
                    .ToList()
            })
            .ToListAsync(cancellationToken);

        return routes
            .Select(route => new RouteCandidate(route.Id, route.SiteId is not null, route.Steps))
            .ToList();
    }

    public Task<bool> IsEligibleApproverAsync(Guid tenantId, Guid userId, Guid siteId, CancellationToken cancellationToken)
    {
        var approverRole = RoleCodes.Approver;

        return _db.UserSiteScopes.AnyAsync(
            scope => scope.TenantId == tenantId
                     && scope.UserId == userId
                     && scope.SiteId == siteId
                     && _db.UserRoles.Any(userRole =>
                         userRole.UserId == scope.UserId
                         && _db.Roles.Any(role => role.Id == userRole.RoleId && role.Name == approverRole)),
            cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> FindEligibleApproversAsync(
        Guid tenantId,
        Guid siteId,
        Guid excludedUserId,
        int take,
        CancellationToken cancellationToken)
    {
        var approverRole = RoleCodes.Approver;

        return await _db.UserSiteScopes
            .AsNoTracking()
            .Where(scope =>
                scope.TenantId == tenantId
                && scope.SiteId == siteId
                && scope.UserId != excludedUserId
                && _db.UserRoles.Any(userRole =>
                    userRole.UserId == scope.UserId
                    && _db.Roles.Any(role => role.Id == userRole.RoleId && role.Name == approverRole)))
            .OrderBy(scope => scope.UserId)
            .Select(scope => scope.UserId)
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    public void AddApproval(RepairRequestApproval approval) => _db.RepairRequestApprovals.Add(approval);

    public void RemoveApproval(RepairRequestApproval approval) => _db.RepairRequestApprovals.Remove(approval);

    public void AddAudit(AuditHistory audit) => _db.AuditHistory.Add(audit);

    public async Task<RepairRequestSaveOutcome> SaveChangesAsync(
        RepairRequestAggregate request,
        byte[] expectedRowVersion,
        CancellationToken cancellationToken)
    {
        // The UPDATE (only on success) applies only WHERE row_version = the expected token.
        _db.Entry(request).Property(item => item.RowVersion).OriginalValue = expectedRowVersion;

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return RepairRequestSaveOutcome.Saved;
        }
        catch (DbUpdateConcurrencyException)
        {
            _db.ChangeTracker.Clear();
            return RepairRequestSaveOutcome.ConcurrencyConflict;
        }
        catch (DbUpdateException exception) when (SqlErrorNumber(exception) is UniqueIndexViolation or UniqueConstraintViolation or DeadlockVictim)
        {
            _db.ChangeTracker.Clear();
            return RepairRequestSaveOutcome.ConcurrencyConflict;
        }
    }

    public void DiscardPendingChanges() => _db.ChangeTracker.Clear();

    public async Task<PagedResult<RoutingIssueDto>> ListRoutingIssuesAsync(CurrentUser user, PageRequest paging, CancellationToken cancellationToken)
    {
        var tenantId = user.TenantId;
        var entityType = RepairRequestAudit.EntityType;
        var failedAction = RepairRequestRoutingAudit.RoutingFailedAction;

        // SUBMITTED requests without an assigned PENDING approval: never routed (legacy) or routing failed.
        var source = _scope.RoutingRecoveryRequests(user)
            .AsNoTracking()
            .Where(request =>
                request.Status == RepairRequestStatus.Submitted
                && !_db.RepairRequestApprovals.Any(approval =>
                    approval.TenantId == tenantId
                    && approval.RepairRequestId == request.Id
                    && approval.AssignedApproverId != null
                    && approval.Status == ApprovalStatus.Pending));

        var totalCount = await source.CountAsync(cancellationToken);
        var skip = (long)(paging.Page - 1) * paging.PageSize;
        if (skip >= totalCount)
        {
            return new PagedResult<RoutingIssueDto>([], paging.Page, paging.PageSize, totalCount);
        }

        var rows = await source
            .OrderBy(request => request.SubmittedAt)
            .ThenBy(request => request.Id)
            .Skip((int)skip)
            .Take(paging.PageSize)
            .Select(request => new
            {
                request.Id,
                request.RequestNo,
                request.SiteId,
                request.RequestCategoryCode,
                request.SubmittedAt,
                request.RowVersion,

                // Latest ROUTING_FAILED audit of this request (audit timeline index); null when never routed.
                LastFailureJson = _db.AuditHistory
                    .Where(audit =>
                        audit.TenantId == tenantId
                        && audit.EntityType == entityType
                        && audit.EntityId == request.Id
                        && audit.ActionCode == failedAction)
                    .OrderByDescending(audit => audit.OccurredAt)
                    .ThenByDescending(audit => audit.Id)
                    .Select(audit => audit.NewValueJson)
                    .FirstOrDefault()
            })
            .ToListAsync(cancellationToken);

        var items = rows
            .Select(row => new RoutingIssueDto(
                row.Id,
                row.RequestNo,
                row.SiteId,
                row.RequestCategoryCode,
                row.SubmittedAt,
                RepairRequestRoutingAudit.ReadFailureCode(row.LastFailureJson),
                row.RowVersion))
            .ToList();

        return new PagedResult<RoutingIssueDto>(items, paging.Page, paging.PageSize, totalCount);
    }

    private static int? SqlErrorNumber(Exception exception) =>
        exception switch
        {
            SqlException sql => sql.Number,
            { InnerException: SqlException inner } => inner.Number,
            _ => null
        };
}
