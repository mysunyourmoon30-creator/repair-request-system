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

/// <summary>EF Core implementation of the Work Order Close (ST-WO-006; WO-API-010; `docs/13` §4.20) port.</summary>
internal sealed class WorkOrderCloseStore : IWorkOrderCloseStore
{
    private const int DeadlockVictim = 1205;

    private readonly RepairRequestDbContext _db;
    private readonly IDataScope _scope;

    public WorkOrderCloseStore(RepairRequestDbContext db, IDataScope scope)
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

    public Task<WorkOrder?> LoadInScopeAsync(CurrentUser user, Guid workOrderId, CancellationToken cancellationToken) =>
        _scope.WorkOrders(user).SingleOrDefaultAsync(workOrder => workOrder.Id == workOrderId, cancellationToken);

    public async Task<WorkOrderCloseEvidence> GetCloseEvidenceAsync(Guid workOrderId, CancellationToken cancellationToken)
    {
        var hasWorkSummary = await _db.WorkSummaries.AsNoTracking()
            .AnyAsync(summary => summary.WorkOrderId == workOrderId, cancellationToken);

        // The decision of the HIGHEST round only — a Work Order whose earlier round was ACCEPT but whose latest is
        // REJECT (or whose only rounds are REJECT) must not pass (docs/13 §4.20 Q2). The unique index on
        // (work_order_id, round_no) is what prevents two rounds sharing a number; Id is an additional deterministic
        // tie-breaker for the ordering regardless.
        var latestDecision = await _db.CustomerAcceptances.AsNoTracking()
            .Where(acceptance => acceptance.WorkOrderId == workOrderId)
            .OrderByDescending(acceptance => acceptance.AcceptanceRoundNo)
            .ThenByDescending(acceptance => acceptance.Id)
            .Select(acceptance => (AcceptanceDecision?)acceptance.Decision)
            .FirstOrDefaultAsync(cancellationToken);

        var costSummary = await _db.CostSummaries.AsNoTracking()
            .Where(summary => summary.WorkOrderId == workOrderId)
            .Select(summary => new { summary.Id, summary.ReviewedAt })
            .SingleOrDefaultAsync(cancellationToken);

        return new WorkOrderCloseEvidence(hasWorkSummary, latestDecision, costSummary?.Id, costSummary?.ReviewedAt);
    }

    public void AddAudit(AuditHistory audit) => _db.AuditHistory.Add(audit);

    public async Task<WorkOrderSaveOutcome> SaveChangesAsync(WorkOrder workOrder, byte[] expectedRowVersion, CancellationToken cancellationToken)
    {
        // The UPDATE applies only WHERE row_version = the client's If-Match token (the Work Order's own): two
        // concurrent Close calls cannot both succeed, and neither can a Close racing any other Work Order change.
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

    private static int? SqlErrorNumber(Exception exception) =>
        exception switch
        {
            SqlException sql => sql.Number,
            { InnerException: SqlException inner } => inner.Number,
            _ => null
        };
}
