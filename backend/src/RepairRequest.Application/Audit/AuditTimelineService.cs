using RepairRequest.Application.Common;
using RepairRequest.Application.Security;
using RepairRequest.Application.WorkOrders;

namespace RepairRequest.Application.Audit;

/// <summary>
/// The Work Order timeline read use case (UC-WO-002; AUD-API-001; `docs/15` v1.1 D1–D12). Read-only.
/// Role capability (<c>Audit.TimelineRead</c>) and the <c>pageSize</c> bounds are enforced by the API before this runs.
///
/// Validation order after the role check (docs/15 §5): cursor syntax → cursor HMAC/version/binding (tenant from the
/// authenticated context, normalized entity type, route Work Order id — <b>no database access</b>) → unsupported entity
/// type → scoped Work Order lookup. A malformed or inauthentic cursor is a generic <see cref="AuditTimelineOutcome.BadCursor"/>
/// decided before and regardless of whether the Work Order exists, so it discloses nothing; an unsupported entity type, a
/// missing Work Order and an out-of-scope Work Order are one identical <see cref="AuditTimelineOutcome.NotFound"/>.
/// </summary>
public sealed class AuditTimelineService
{
    /// <summary>D1/D7: the only entity type the timeline accepts in V1 (compared after invariant upper-casing).</summary>
    public const string SupportedEntityType = WorkOrderAudit.WorkOrderEntityType;

    private readonly IAuditTimelineStore _store;
    private readonly ITimelineCursorProtector _cursors;

    public AuditTimelineService(IAuditTimelineStore store, ITimelineCursorProtector cursors)
    {
        _store = store;
        _cursors = cursors;
    }

    public async Task<AuditTimelineResult> GetWorkOrderTimelineAsync(
        CurrentUser user, string entityType, Guid workOrderId, int pageSize, string? cursor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(entityType);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);

        var normalizedEntityType = entityType.ToUpperInvariant();

        // Steps 3–4: cursor syntax, then HMAC/version/binding — neither touches the database.
        TimelinePosition? after = null;
        if (cursor is not null)
        {
            if (!TimelineCursorSyntax.TryDecode(cursor, out var bytes)
                || !_cursors.TryUnprotect(user.TenantId, normalizedEntityType, workOrderId, bytes, out var position))
            {
                return AuditTimelineResult.BadCursor;
            }

            after = position;
        }

        // Step 5: unsupported entity type, then the scoped Work Order lookup (tenant, Site, REQUESTER ownership).
        if (!string.Equals(normalizedEntityType, SupportedEntityType, StringComparison.Ordinal))
        {
            return AuditTimelineResult.NotFound;
        }

        var sources = await _store.ResolveWorkOrderSourcesAsync(user, workOrderId, cancellationToken);
        if (sources is null)
        {
            return AuditTimelineResult.NotFound;
        }

        // One extra row proves there is another page; it is never returned.
        var rows = await _store.ReadPageAsync(sources, after, pageSize + 1, cancellationToken);
        var hasMore = rows.Count > pageSize;
        var pageRows = rows.Take(pageSize).ToList();

        var knownUsers = await _store.GetExistingUserIdsAsync(
            sources.TenantId,
            pageRows.Select(row => row.ActorId).Where(id => !IsSystemActor(id)).Distinct().ToList(),
            cancellationToken);

        var items = pageRows.Select(row => new AuditTimelineItemDto(
                row.AuditId,
                DateTime.SpecifyKind(row.OccurredAt, DateTimeKind.Utc),
                row.ActionCode,
                row.EntityType,
                row.EntityId,
                row.FromState,
                row.ToState,
                new AuditTimelineActorDto(row.ActorId, ClassifyActor(row.ActorId, knownUsers)),
                AuditTimelineDetails.Project(row.ActionCode, row.NewValueJson)))
            .ToList();

        string? nextCursor = null;
        if (hasMore)
        {
            var last = pageRows[^1];
            nextCursor = _cursors.Protect(user.TenantId, normalizedEntityType, workOrderId, new TimelinePosition(last.OccurredAt, last.AuditId));
        }

        return AuditTimelineResult.Ok(new AuditTimelinePageDto(items, nextCursor, hasMore));
    }

    private static bool IsSystemActor(Guid actorId) => actorId == SystemActors.MalwareScanner || actorId == SystemActors.ApprovalRouting;

    /// <summary>D5: SYSTEM for a system-actor constant, USER for an existing user of the same tenant, otherwise UNKNOWN.</summary>
    private static AuditActorKind ClassifyActor(Guid actorId, IReadOnlySet<Guid> knownUsers) =>
        IsSystemActor(actorId) ? AuditActorKind.System : knownUsers.Contains(actorId) ? AuditActorKind.User : AuditActorKind.Unknown;
}
