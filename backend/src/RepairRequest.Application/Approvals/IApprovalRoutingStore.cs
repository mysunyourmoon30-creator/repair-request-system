using RepairRequest.Application.Common;
using RepairRequest.Application.RepairRequests;
using RepairRequest.Application.Security;
using RepairRequest.Domain.Approvals;
using RepairRequest.Domain.Auditing;
using RepairRequestAggregate = RepairRequest.Domain.RepairRequests.RepairRequest;

namespace RepairRequest.Application.Approvals;

/// <summary>
/// Persistence port for ST-RR-003 routing (DEC-PRE-S1-007R-01/07/10). A routing attempt runs inside
/// <see cref="RunInTransactionAsync{T}"/>: <see cref="LockRequestAsync"/> takes an update lock on the request row so a
/// post-Submit routing and concurrent Admin Retries serialize per request, then bounded key lookups resolve the route
/// and approver. Nothing is committed unless the command succeeds.
/// </summary>
public interface IApprovalRoutingStore
{
    /// <summary>One READ COMMITTED transaction; commits only a successful result; a deadlock becomes a concurrency conflict.</summary>
    Task<CommandResult<T>> RunInTransactionAsync<T>(Func<Task<CommandResult<T>>> command, CancellationToken cancellationToken);

    /// <summary>Locks and loads (tracked, current database values) the request of <paramref name="tenantId"/>; null when absent.</summary>
    Task<RepairRequestAggregate?> LockRequestAsync(Guid tenantId, Guid repairRequestId, CancellationToken cancellationToken);

    /// <summary>Tracked approval row of one step, or null.</summary>
    Task<RepairRequestApproval?> FindStepApprovalAsync(Guid tenantId, Guid repairRequestId, short stepNo, CancellationToken cancellationToken);

    /// <summary>Active routes of the tenant for the Category at the Site or as tenant default, with their steps.</summary>
    Task<IReadOnlyList<RouteCandidate>> ListActiveRoutesAsync(
        Guid tenantId,
        string requestCategoryCode,
        Guid siteId,
        CancellationToken cancellationToken);

    /// <summary>The user holds APPROVER and has business Site scope (user_site_scope) on <paramref name="siteId"/>.</summary>
    Task<bool> IsEligibleApproverAsync(Guid tenantId, Guid userId, Guid siteId, CancellationToken cancellationToken);

    /// <summary>Up to <paramref name="take"/> eligible APPROVER users with Site scope, excluding <paramref name="excludedUserId"/>.</summary>
    Task<IReadOnlyList<Guid>> FindEligibleApproversAsync(
        Guid tenantId,
        Guid siteId,
        Guid excludedUserId,
        int take,
        CancellationToken cancellationToken);

    void AddApproval(RepairRequestApproval approval);

    /// <summary>Removes a superseded unassigned routing-failure row in the current unit of work.</summary>
    void RemoveApproval(RepairRequestApproval approval);

    void AddAudit(AuditHistory audit);

    /// <summary>Saves the attempt; the request update applies only while its row version equals <paramref name="expectedRowVersion"/>.</summary>
    Task<RepairRequestSaveOutcome> SaveChangesAsync(
        RepairRequestAggregate request,
        byte[] expectedRowVersion,
        CancellationToken cancellationToken);

    /// <summary>Drops every pending (unsaved) change after an unexpected failure.</summary>
    void DiscardPendingChanges();

    /// <summary>Tenant-scoped, paged routing issues for ADMINISTRATOR recovery (DEC-PRE-S1-007R-10).</summary>
    Task<PagedResult<RoutingIssueDto>> ListRoutingIssuesAsync(CurrentUser user, PageRequest paging, CancellationToken cancellationToken);
}
