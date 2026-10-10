using RepairRequest.Application.Security;

namespace RepairRequest.Application.Audit;

/// <summary>
/// Persistence port for the Work Order timeline (UC-WO-002; AUD-API-001; `docs/15`). Read-only. Every ordering and every
/// keyset comparison is executed by SQL Server; implementations never sort, compare Guids or page in memory.
/// </summary>
public interface IAuditTimelineStore
{
    /// <summary>
    /// The Work Order's timeline sources, derived on the server through <see cref="IDataScope.WorkOrders"/>
    /// (tenant, Site, REQUESTER ownership), or null when the Work Order does not exist or is outside the caller's scope.
    /// </summary>
    Task<AuditTimelineSources?> ResolveWorkOrderSourcesAsync(CurrentUser user, Guid workOrderId, CancellationToken cancellationToken);

    /// <summary>
    /// Reads up to <paramref name="take"/> rows strictly after <paramref name="after"/> in
    /// <c>occurred_at DESC, audit_history_id DESC</c> order (two-step shape, docs/15 §5 / §11): the page is chosen from
    /// index-only columns, then only those rows are joined back by primary key for the remaining columns.
    /// </summary>
    Task<IReadOnlyList<AuditTimelineRow>> ReadPageAsync(
        AuditTimelineSources sources, TimelinePosition? after, int take, CancellationToken cancellationToken);

    /// <summary>The subset of <paramref name="actorIds"/> that are existing users of <paramref name="tenantId"/> (ids only).</summary>
    Task<IReadOnlySet<Guid>> GetExistingUserIdsAsync(Guid tenantId, IReadOnlyCollection<Guid> actorIds, CancellationToken cancellationToken);
}
