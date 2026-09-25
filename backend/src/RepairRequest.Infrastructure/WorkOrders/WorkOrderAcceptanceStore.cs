using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using RepairRequest.Application.Common;
using RepairRequest.Application.Security;
using RepairRequest.Application.WorkOrders;
using RepairRequest.Domain.Auditing;
using RepairRequest.Domain.WorkOrders;
using RepairRequest.Infrastructure.Persistence;

namespace RepairRequest.Infrastructure.WorkOrders;

/// <summary>EF Core implementation of the Customer Accept (ST-WO-005; UC-WO-021; `docs/13` §4.16) and Customer Reject (ST-WO-007; UC-WO-022; `docs/13` §4.17) port.</summary>
internal sealed class WorkOrderAcceptanceStore : IWorkOrderAcceptanceStore
{
    private const int DeadlockVictim = 1205;
    private const int UniqueIndexViolation = 2601;
    private const int UniqueConstraintViolation = 2627;

    private readonly RepairRequestDbContext _db;

    public WorkOrderAcceptanceStore(RepairRequestDbContext db)
    {
        _db = db;
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

    public Task<WorkOrder?> LoadForAcceptAsync(CurrentUser user, Guid workOrderId, CancellationToken cancellationToken)
    {
        var tenantId = user.TenantId;
        var userId = user.UserId;

        // Resource-specific: the caller must be exactly AcceptanceContactId, not merely hold a role — plus the
        // same current-Site-scope defense-in-depth re-check every other Technician/Requester-scoped query in
        // this codebase applies (a revoked Site scope hides the Work Order even from the exact contact).
        return _db.WorkOrders
            .Where(workOrder =>
                workOrder.TenantId == tenantId
                && workOrder.Id == workOrderId
                && workOrder.AcceptanceContactId == userId
                && _db.RepairRequests.Any(request =>
                    request.Id == workOrder.RepairRequestId
                    && request.SiteId != null
                    && _db.UserSiteScopes.Any(scope =>
                        scope.TenantId == tenantId && scope.UserId == userId && scope.SiteId == request.SiteId)))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public void AddAudit(AuditHistory audit) => _db.AuditHistory.Add(audit);

    public Task<int> NextAcceptanceRoundNoAsync(Guid workOrderId, CancellationToken cancellationToken) =>
        NextOrdinalAsync(_db.CustomerAcceptances.Where(acceptance => acceptance.WorkOrderId == workOrderId), cancellationToken);

    public void Add(CustomerAcceptance acceptance) => _db.CustomerAcceptances.Add(acceptance);

    public Task<int> NextCorrectiveActionCycleNoAsync(Guid workOrderId, CancellationToken cancellationToken) =>
        NextOrdinalAsync(_db.CorrectiveActions.Where(action => action.WorkOrderId == workOrderId), cancellationToken);

    public void Add(CorrectiveAction correctiveAction) => _db.CorrectiveActions.Add(correctiveAction);

    private static async Task<int> NextOrdinalAsync<T>(IQueryable<T> existingRowsForWorkOrder, CancellationToken cancellationToken) =>
        await existingRowsForWorkOrder.CountAsync(cancellationToken) + 1;

    public async Task<WorkOrderSaveOutcome> SaveChangesAsync(WorkOrder workOrder, byte[] expectedRowVersion, CancellationToken cancellationToken)
    {
        // The UPDATE applies only WHERE row_version = the client's If-Match token (the Work Order's own).
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
        catch (DbUpdateException exception) when (SqlErrorNumber(exception) is UniqueIndexViolation or UniqueConstraintViolation)
        {
            // Two concurrent Accept/Reject calls on the same Work Order can both read the same "next round/cycle
            // number" (NextAcceptanceRoundNoAsync/NextCorrectiveActionCycleNoAsync) before either commits — the
            // RowVersion compare-and-swap above only guards the Work Order row itself, not customer_acceptance's
            // own UQ(work_order_id, round_no) / corrective_action's UQ(work_order_id, cycle_no). The loser hits
            // this unique-index violation, which is exactly a concurrency conflict on this resource, not a
            // separate failure mode — same 409 CONCURRENCY_CONFLICT outcome as a stale RowVersion.
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
