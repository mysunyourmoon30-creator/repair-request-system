using Microsoft.EntityFrameworkCore;
using RepairRequest.Application.Audit;
using RepairRequest.Application.RepairRequests;
using RepairRequest.Application.Security;
using RepairRequest.Application.WorkOrders;
using RepairRequest.Infrastructure.Persistence;

namespace RepairRequest.Infrastructure.Audit;

/// <summary>
/// EF Core implementation of the Work Order timeline read port (UC-WO-002; AUD-API-001; `docs/15` §4–§5, §11–§12).
/// Read-only, `AsNoTracking`, projection only.
///
/// The page query is the Phase 1A "two-step" shape and nothing else: (1) the page is chosen from index-only columns
/// (<c>audit_history_id</c>, <c>occurred_at</c>) with <c>TOP(take)</c> in <c>occurred_at DESC, audit_history_id DESC</c>
/// order, using the keyset predicate; (2) only those rows are joined back to the table by its clustered primary key for
/// the remaining columns. A plain query that looks up the payload of every candidate row is forbidden (it cost 6,640
/// logical reads for a 2,000-event Work Order against 577 for this shape). Ordering and the keyset comparison are SQL
/// Server's own — never done in memory.
///
/// The Visit, Work Session and Corrective Action id sets are each passed as ONE JSON parameter and parsed by SQL Server
/// with <c>OPENJSON (… WITH (… uniqueidentifier '$'))</c> (`EF.Parameter`), so the command's parameter count does not
/// grow with the number of child ids (SQL Server allows 2,100 parameters per command). Entity types are server constants.
/// </summary>
internal sealed class AuditTimelineStore : IAuditTimelineStore
{
    private static readonly string[] CodesWithDetails = AuditTimelineDetails.CodesWithDetails;

    private readonly RepairRequestDbContext _db;
    private readonly IDataScope _scope;

    public AuditTimelineStore(RepairRequestDbContext db, IDataScope scope)
    {
        _db = db;
        _scope = scope;
    }

    public async Task<AuditTimelineSources?> ResolveWorkOrderSourcesAsync(CurrentUser user, Guid workOrderId, CancellationToken cancellationToken)
    {
        var workOrder = await _scope.WorkOrders(user).AsNoTracking()
            .Where(item => item.Id == workOrderId)
            .Select(item => new { item.Id, item.TenantId, item.RepairRequestId })
            .SingleOrDefaultAsync(cancellationToken);
        if (workOrder is null)
        {
            return null;
        }

        var tenantId = workOrder.TenantId;
        var id = workOrder.Id;

        var visitIds = await _db.ServiceVisits.AsNoTracking()
            .Where(visit => visit.TenantId == tenantId && visit.WorkOrderId == id)
            .Select(visit => visit.Id)
            .ToListAsync(cancellationToken);

        var sessionIds = await (
                from session in _db.WorkSessions.AsNoTracking()
                join visit in _db.ServiceVisits.AsNoTracking() on session.ServiceVisitId equals visit.Id
                where session.TenantId == tenantId && visit.TenantId == tenantId && visit.WorkOrderId == id
                select session.Id)
            .ToListAsync(cancellationToken);

        var correctiveActionIds = await _db.CorrectiveActions.AsNoTracking()
            .Where(action => action.TenantId == tenantId && action.WorkOrderId == id)
            .Select(action => action.Id)
            .ToListAsync(cancellationToken);

        return new AuditTimelineSources(tenantId, id, workOrder.RepairRequestId, visitIds, sessionIds, correctiveActionIds);
    }

    public async Task<IReadOnlyList<AuditTimelineRow>> ReadPageAsync(
        AuditTimelineSources sources, TimelinePosition? after, int take, CancellationToken cancellationToken)
    {
        if (take < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(take), "At least one row must be requested.");
        }

        var tenantId = sources.TenantId;
        var workOrderId = sources.WorkOrderId;
        var repairRequestId = sources.RepairRequestId;
        var visitIds = sources.VisitIds.ToList();
        var sessionIds = sources.SessionIds.ToList();
        var correctiveActionIds = sources.CorrectiveActionIds.ToList();

        const string workOrderType = WorkOrderAudit.WorkOrderEntityType;
        const string visitType = WorkOrderAudit.ServiceVisitEntityType;
        const string sessionType = WorkOrderAudit.WorkSessionEntityType;
        const string correctiveActionType = WorkOrderAudit.CorrectiveActionEntityType;
        const string repairRequestType = RepairRequestAudit.EntityType;
        const string convertedAction = RepairRequestAudit.ConvertedAction;

        // Step 1 — candidate rows, index-only columns. The source predicate is split in two UNION ALL branches on purpose:
        // the main OR (Work Order + the three OPENJSON id sets) references only index columns, while the Repair Request
        // branch filters on action_code, which is NOT in IX_audit_history_timeline. Keeping action_code inside the main OR
        // made SQL Server's OR-expansion add exclusion predicates over action_code and look up EVERY candidate row
        // (310/800/480/410 lookups on a 2,000-event Work Order, found by the Phase 1B gate). EF.Parameter(list) = one JSON
        // parameter + OPENJSON per id set.
        var main = _db.AuditHistory.AsNoTracking().Where(audit =>
            audit.TenantId == tenantId
            && ((audit.EntityType == workOrderType && audit.EntityId == workOrderId)
                || (audit.EntityType == visitType && EF.Parameter(visitIds).Contains(audit.EntityId))
                || (audit.EntityType == sessionType && EF.Parameter(sessionIds).Contains(audit.EntityId))
                || (audit.EntityType == correctiveActionType && EF.Parameter(correctiveActionIds).Contains(audit.EntityId))));

        var converted = _db.AuditHistory.AsNoTracking().Where(audit =>
            audit.TenantId == tenantId
            && audit.EntityType == repairRequestType
            && audit.EntityId == repairRequestId
            && audit.ActionCode == convertedAction);

        if (after is { } position)
        {
            var occurredAt = position.OccurredAt;
            var auditId = position.AuditId;

            // The tuple comparison, evaluated by SQL Server (uniqueidentifier ordering). The redundant sargable
            // `occurred_at <= @ts` lets the optimizer range-seek on the index's fourth key column.
            main = main.Where(audit =>
                audit.OccurredAt <= occurredAt
                && (audit.OccurredAt < occurredAt || (audit.OccurredAt == occurredAt && audit.Id.CompareTo(auditId) < 0)));
            converted = converted.Where(audit =>
                audit.OccurredAt <= occurredAt
                && (audit.OccurredAt < occurredAt || (audit.OccurredAt == occurredAt && audit.Id.CompareTo(auditId) < 0)));
        }

        var page = main.Select(audit => new { audit.Id, audit.OccurredAt })
            .Concat(converted.Select(audit => new { audit.Id, audit.OccurredAt }))
            .OrderByDescending(candidate => candidate.OccurredAt).ThenByDescending(candidate => candidate.Id)
            .Take(take);

        // Step 2 — join back by clustered primary key for the page's rows only; payload only for allowlisted codes.
        var rows = await (
                from candidate in page
                join audit in _db.AuditHistory.AsNoTracking() on candidate.Id equals audit.Id
                orderby candidate.OccurredAt descending, candidate.Id descending
                select new AuditTimelineRow(
                    audit.Id,
                    audit.OccurredAt,
                    audit.EntityType,
                    audit.EntityId,
                    audit.ActionCode,
                    audit.FromState,
                    audit.ToState,
                    audit.ActorId,
                    CodesWithDetails.Contains(audit.ActionCode) ? audit.NewValueJson : null))
            .ToListAsync(cancellationToken);

        return rows;
    }

    public async Task<IReadOnlySet<Guid>> GetExistingUserIdsAsync(Guid tenantId, IReadOnlyCollection<Guid> actorIds, CancellationToken cancellationToken)
    {
        if (actorIds.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var ids = actorIds.ToList();

        // Existence only, restricted to the caller's tenant: nothing else about the user is read.
        var found = await _db.Users.AsNoTracking()
            .Where(user => user.TenantId == tenantId && EF.Parameter(ids).Contains(user.Id))
            .Select(user => user.Id)
            .ToListAsync(cancellationToken);

        return found.ToHashSet();
    }
}
