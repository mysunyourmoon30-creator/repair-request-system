using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using RepairRequest.Application.Common;
using RepairRequest.Application.Security;
using RepairRequest.Application.WorkOrders;
using RepairRequest.Domain.Auditing;
using RepairRequest.Domain.WorkOrders;
using RepairRequest.Infrastructure.Persistence;

namespace RepairRequest.Infrastructure.WorkOrders;

/// <summary>
/// EF Core implementation of the Work Summary Submit/Review ports (`docs/13` §4.15). Submit's own-scope reuses
/// <see cref="IDataScope.OwnWorkSessions"/> (mirrors <c>WorkSessionStore</c>); Submit-for-Acceptance and the
/// Team-Lead/Supervisor half of the read reuse <see cref="IDataScope.WorkOrders"/> unchanged (Decision 5).
/// </summary>
internal sealed class WorkSummaryStore : IWorkSummaryStore
{
    private const int DeadlockVictim = 1205;

    private readonly RepairRequestDbContext _db;
    private readonly IDataScope _scope;

    public WorkSummaryStore(RepairRequestDbContext db, IDataScope scope)
    {
        _db = db;
        _scope = scope;
    }

    public async Task<CommandResult<T>> RunInTransactionAsync<T>(Func<Task<CommandResult<T>>> command, CancellationToken cancellationToken)
    {
        try
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

            var result = await command();
            if (result.Succeeded)
            {
                await transaction.CommitAsync(cancellationToken);
            }
            else
            {
                _db.ChangeTracker.Clear();
            }

            return result;
        }
        catch (Exception exception) when (SqlErrorNumber(exception) is DeadlockVictim)
        {
            _db.ChangeTracker.Clear();
            return CommandError.ConcurrencyConflict;
        }
    }

    public async Task<WorkOrderForSummarySubmit?> LoadForSubmitAsync(CurrentUser user, Guid workOrderId, CancellationToken cancellationToken)
    {
        var tenantId = user.TenantId;

        var workOrder = await _db.WorkOrders
            .SingleOrDefaultAsync(item => item.TenantId == tenantId && item.Id == workOrderId, cancellationToken);
        if (workOrder is null)
        {
            return null;
        }

        // Every one of the caller's own sessions on a Visit of this Work Order, any status — ownership (404) and
        // "must be CHECKED_OUT" (409) are deliberately separate checks, the same convention every other command
        // in this codebase uses (scope first, state second). Ordered by CheckInAt (always present, unlike the
        // nullable CheckOutAt) so the *current* cycle's session sorts first regardless of whether it has been
        // checked out yet — `docs/13` §4.24: once the corrective cycle exists, the caller can have an older,
        // already-CHECKED_OUT session (the initial Visit's) alongside a newer, still-CHECKED_IN one (the
        // corrective Visit's); ordering by CheckOutAt would put the stale CHECKED_OUT row first (its non-null
        // timestamp sorts ahead of the active session's null one) and silently accept a Work Summary against the
        // wrong, already-summarized Visit instead of correctly reporting "not yet checked out" for the real one.
        var ownVisits = await (
            from session in _scope.OwnWorkSessions(user)
            join item in _db.ServiceVisits on session.ServiceVisitId equals item.Id
            where item.WorkOrderId == workOrderId
            orderby session.CheckInAt descending
            select new { session.Status, Visit = item })
            .ToListAsync(cancellationToken);

        if (ownVisits.Count == 0)
        {
            return null;
        }

        var latest = ownVisits[0];
        var checkedOutVisit = latest.Status == WorkSessionStatus.CheckedOut ? latest.Visit : null;
        return new WorkOrderForSummarySubmit(workOrder, checkedOutVisit);
    }

    public Task<WorkOrder?> LoadForAcceptanceSubmitAsync(CurrentUser user, Guid workOrderId, CancellationToken cancellationToken) =>
        _scope.WorkOrders(user).SingleOrDefaultAsync(workOrder => workOrder.Id == workOrderId, cancellationToken);

    public void Add(WorkSummary summary) => _db.WorkSummaries.Add(summary);

    public void AddAudit(AuditHistory audit) => _db.AuditHistory.Add(audit);

    public async Task<WorkOrderSaveOutcome> SaveChangesAsync(WorkOrder workOrder, byte[] expectedRowVersion, CancellationToken cancellationToken)
    {
        // The UPDATE applies only WHERE row_version = the client's If-Match token (the Work Order's own — it is
        // the resource named in both endpoints' URLs).
        _db.Entry(workOrder).Property(item => item.RowVersion).OriginalValue = expectedRowVersion;

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return WorkOrderSaveOutcome.Saved;
        }
        catch (DbUpdateConcurrencyException)
        {
            _db.ChangeTracker.Clear();
            return WorkOrderSaveOutcome.ConcurrencyConflict;
        }
        catch (DbUpdateException exception) when (SqlErrorNumber(exception) is DeadlockVictim)
        {
            _db.ChangeTracker.Clear();
            return WorkOrderSaveOutcome.ConcurrencyConflict;
        }
    }

    public async Task<WorkSummaryDto?> GetWorkSummaryAsync(CurrentUser user, Guid workOrderId, CancellationToken cancellationToken)
    {
        var summaries = _db.WorkSummaries.AsNoTracking().Where(summary => summary.WorkOrderId == workOrderId);

        IQueryable<WorkSummary> scoped;
        if (user.IsTechnician)
        {
            // Reuses IDataScope.OwnWorkSessions in full (tenant + technician ownership + current Site scope
            // defense-in-depth re-check) rather than a bare AssignedTechnicianId match, so a revoked Site scope
            // or a technician id that happens to match a different tenant's row cannot leak a Work Summary.
            var ownServiceVisitIds = _scope.OwnWorkSessions(user).Select(session => session.ServiceVisitId);
            scoped = summaries.Where(summary => ownServiceVisitIds.Contains(summary.ServiceVisitId));
        }
        else
        {
            var scopedWorkOrderIds = _scope.WorkOrders(user).Select(workOrder => workOrder.Id);
            scoped = summaries.Where(summary => scopedWorkOrderIds.Contains(summary.WorkOrderId));
        }

        // Latest row only: `WorkSummary` is keyed per-Visit (WSM-005 "unique per Visit revision"), not per-Work
        // Order — once the corrective cycle's own Submit Work Summary (`docs/13` §4.24) inserts a second row for
        // the same Work Order (a different ServiceVisitId), `SingleOrDefaultAsync` would throw
        // InvalidOperationException here. `Id` is a sequential GUID (assigned on insert, per this codebase's own
        // convention), so ordering by it descending is the same "latest row" pattern already used for
        // CustomerAcceptance/CorrectiveAction (ordered by round/cycle desc, Id desc as the tie-breaker).
        return await scoped
            .OrderByDescending(summary => summary.Id)
            .Select(summary => new WorkSummaryDto(
                summary.Id, summary.WorkOrderId, summary.ServiceVisitId, summary.RevisionNo, summary.SummaryText, summary.RepairOutcomeCode))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<string?> ResolveAcceptanceContactSnapshotAsync(Guid tenantId, Guid repairRequestId, Guid acceptanceContactId, CancellationToken cancellationToken)
    {
        var siteId = await _db.RepairRequests
            .Where(request => request.Id == repairRequestId)
            .Select(request => request.SiteId)
            .SingleOrDefaultAsync(cancellationToken);
        if (siteId is null)
        {
            return null;
        }

        var candidate = await AcceptanceContactEligibility.EligibleUsers(_db, tenantId, siteId.Value)
            .Where(user => user.Id == acceptanceContactId)
            .Select(user => new { user.UserName, user.Email })
            .SingleOrDefaultAsync(cancellationToken);

        return candidate is null
            ? null
            : JsonSerializer.Serialize(new Dictionary<string, object?> { ["displayName"] = candidate.UserName, ["email"] = candidate.Email });
    }

    public async Task<IReadOnlyList<EligibleAcceptanceContactDto>> ListEligibleAcceptanceContactsAsync(CurrentUser user, Guid workOrderId, CancellationToken cancellationToken)
    {
        var row = await (
            from workOrder in _scope.WorkOrders(user)
            join request in _db.RepairRequests on workOrder.RepairRequestId equals request.Id
            where workOrder.Id == workOrderId
            select request.SiteId)
            .SingleOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            return [];
        }

        return await AcceptanceContactEligibility.EligibleUsers(_db, user.TenantId, row.Value)
            .OrderBy(candidate => candidate.UserName)
            .Select(candidate => new EligibleAcceptanceContactDto(candidate.Id, candidate.UserName!, candidate.Email!))
            .ToListAsync(cancellationToken);
    }

    private static int? SqlErrorNumber(Exception exception) =>
        exception switch
        {
            SqlException sql => sql.Number,
            { InnerException: SqlException inner } => inner.Number,
            _ => null
        };
}
