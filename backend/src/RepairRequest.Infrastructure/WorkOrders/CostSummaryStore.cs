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

/// <summary>EF Core implementation of the Cost Summary Prepare port (CST-API-001; `docs/13` §4.18).</summary>
internal sealed class CostSummaryStore : ICostSummaryStore
{
    private const int DeadlockVictim = 1205;
    private const int UniqueIndexViolation = 2601;
    private const int UniqueConstraintViolation = 2627;

    private readonly RepairRequestDbContext _db;
    private readonly IDataScope _scope;

    public CostSummaryStore(RepairRequestDbContext db, IDataScope scope)
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

    public Task<WorkOrder?> LoadWorkOrderForPrepareAsync(CurrentUser user, Guid workOrderId, CancellationToken cancellationToken) =>
        _scope.WorkOrders(user).SingleOrDefaultAsync(workOrder => workOrder.Id == workOrderId, cancellationToken);

    public Task<CostSummary?> GetTrackedCostSummaryAsync(Guid workOrderId, CancellationToken cancellationToken) =>
        _db.CostSummaries.SingleOrDefaultAsync(summary => summary.WorkOrderId == workOrderId, cancellationToken);

    public void Add(CostSummary costSummary) => _db.CostSummaries.Add(costSummary);

    public void AddAudit(AuditHistory audit) => _db.AuditHistory.Add(audit);

    public async Task<WorkOrderSaveOutcome> SaveNewAsync(WorkOrder workOrder, byte[] expectedRowVersion, CancellationToken cancellationToken)
    {
        // First-time Prepare: the only entity that could possibly have changed since the caller's last read is
        // the Work Order itself (nothing about Prepare's own state existed to race against yet) — so the
        // compare-and-swap targets the Work Order's own RowVersion, even though Prepare never writes to it.
        _db.Entry(workOrder).Property(item => item.RowVersion).OriginalValue = expectedRowVersion;

        return await SaveAsync(cancellationToken);
    }

    public async Task<WorkOrderSaveOutcome> SaveUpdateAsync(CostSummary costSummary, byte[] expectedRowVersion, CancellationToken cancellationToken)
    {
        // Re-Prepare (edit): the Work Order's own RowVersion never advances during Prepare, so it cannot detect
        // a lost update across repeated edits — the compare-and-swap must target the Cost Summary's own
        // RowVersion (CST-011) instead, the ETag returned by the previous successful Prepare response.
        _db.Entry(costSummary).Property(item => item.RowVersion).OriginalValue = expectedRowVersion;

        return await SaveAsync(cancellationToken);
    }

    private async Task<WorkOrderSaveOutcome> SaveAsync(CancellationToken cancellationToken)
    {
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
            // Two concurrent first-time Prepare calls on the same Work Order can both pass the
            // GetTrackedCostSummaryAsync "does one exist yet" check before either commits; the loser hits
            // UQ_cost_summary_work_order_id — the same race class fixed for customer_acceptance/corrective_action
            // in the previous ticket, mapped the same way.
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
