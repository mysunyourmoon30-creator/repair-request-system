using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using RepairRequest.Application.Common;
using RepairRequest.Application.Security;
using RepairRequest.Application.WorkOrders;
using RepairRequest.Domain.Auditing;
using RepairRequest.Domain.Files;
using RepairRequest.Domain.WorkOrders;
using RepairRequest.Infrastructure.Persistence;

namespace RepairRequest.Infrastructure.WorkOrders;

/// <summary>EF Core implementation of the Corrective Action Submit Plan / Approve Plan (`docs/13` §4.21; Ticket 6) port.</summary>
internal sealed class CorrectiveActionStore : ICorrectiveActionStore
{
    private const int DeadlockVictim = 1205;

    private readonly RepairRequestDbContext _db;
    private readonly IDataScope _scope;

    public CorrectiveActionStore(RepairRequestDbContext db, IDataScope scope)
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

    public async Task<(WorkOrder? WorkOrder, CorrectiveAction? CorrectiveAction)> LoadInScopeAsync(CurrentUser user, Guid correctiveActionId, CancellationToken cancellationToken)
    {
        var scopedWorkOrderIds = _scope.WorkOrders(user).Select(workOrder => workOrder.Id);

        // Resource-specific, non-leaking: the Corrective Action must exist AND its own Work Order must be within
        // the caller's scope — a wrong tenant or a Corrective Action on an out-of-scope Work Order both miss here.
        var correctiveAction = await _db.CorrectiveActions
            .SingleOrDefaultAsync(action => action.Id == correctiveActionId && scopedWorkOrderIds.Contains(action.WorkOrderId), cancellationToken);
        if (correctiveAction is null)
        {
            return (null, null);
        }

        var workOrder = await _db.WorkOrders.SingleOrDefaultAsync(workOrder => workOrder.Id == correctiveAction.WorkOrderId, cancellationToken);
        return (workOrder, correctiveAction);
    }

    public Task<bool> IsPlanFileUsableAsync(Guid tenantId, Guid fileAssetId, CancellationToken cancellationToken) =>
        _db.FileAssets.AnyAsync(file => file.Id == fileAssetId && file.TenantId == tenantId && file.MalwareScanStatus == MalwareScanStatus.Clean, cancellationToken);

    public void AddAudit(AuditHistory audit) => _db.AuditHistory.Add(audit);

    public async Task<WorkOrderSaveOutcome> SaveChangesAsync(WorkOrder workOrder, byte[] expectedRowVersion, CancellationToken cancellationToken)
    {
        // The UPDATE applies only WHERE row_version = the client's If-Match token (the Work Order's own) — the
        // Corrective Action row is saved in the same SaveChangesAsync call, tracked by the same context.
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

    public async Task<CorrectiveActionForScheduleRework?> LoadForScheduleReworkAsync(CurrentUser user, Guid correctiveActionId, CancellationToken cancellationToken)
    {
        var scopedWorkOrderIds = _scope.WorkOrders(user).Select(workOrder => workOrder.Id);

        var correctiveAction = await _db.CorrectiveActions
            .SingleOrDefaultAsync(action => action.Id == correctiveActionId && scopedWorkOrderIds.Contains(action.WorkOrderId), cancellationToken);
        if (correctiveAction is null)
        {
            return null;
        }

        var workOrder = await _db.WorkOrders.SingleOrDefaultAsync(workOrder => workOrder.Id == correctiveAction.WorkOrderId, cancellationToken);
        if (workOrder is null)
        {
            return null;
        }

        var siteId = await _db.RepairRequests
            .Where(request => request.Id == workOrder.RepairRequestId)
            .Select(request => request.SiteId)
            .SingleOrDefaultAsync(cancellationToken);

        return new CorrectiveActionForScheduleRework(workOrder, correctiveAction, siteId);
    }

    public Task<bool> IsTechnicianEligibleAsync(Guid tenantId, Guid technicianId, Guid siteId, CancellationToken cancellationToken) =>
        TechnicianEligibility.IsEligibleAsync(_db, tenantId, technicianId, siteId, cancellationToken);

    public void Add(ServiceVisit visit) => _db.ServiceVisits.Add(visit);

    public async Task<WorkOrderSaveOutcome> SaveScheduleReworkAsync(CorrectiveAction correctiveAction, byte[] expectedRowVersion, CancellationToken cancellationToken)
    {
        // Unlike SaveChangesAsync above, this targets the Corrective Action's own RowVersion — Schedule Rework
        // genuinely mutates this row (CorrectiveServiceVisitId: null -> a real id), so this is a normal,
        // EF-triggered optimistic-concurrency UPDATE with a real WHERE row_version = @original clause; two
        // genuinely concurrent Schedule Rework calls on the same Corrective Action can both pass the in-memory
        // state guard, but only the first commit's UPDATE can match this WHERE clause — the second throws
        // DbUpdateConcurrencyException, and with it, its newly-added Service Visit row is rolled back too (same
        // transaction, same SaveChangesAsync call) — `docs/13` §4.22.
        _db.Entry(correctiveAction).Property(item => item.RowVersion).OriginalValue = expectedRowVersion;

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

    private static int? SqlErrorNumber(Exception exception) =>
        exception switch
        {
            SqlException sql => sql.Number,
            { InnerException: SqlException inner } => inner.Number,
            _ => null
        };
}
