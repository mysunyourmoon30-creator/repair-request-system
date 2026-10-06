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

/// <summary>EF Core implementation of the Work Order Cancel (ST-WO-011; WO-API-011; `docs/13` §4.25) port.</summary>
internal sealed class WorkOrderCancelStore : IWorkOrderCancelStore
{
    private const int DeadlockVictim = 1205;

    private readonly RepairRequestDbContext _db;
    private readonly IDataScope _scope;

    public WorkOrderCancelStore(RepairRequestDbContext db, IDataScope scope)
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

    public async Task<IReadOnlyList<ServiceVisit>> GetServiceVisitsAsync(Guid workOrderId, CancellationToken cancellationToken) =>
        await _db.ServiceVisits.Where(visit => visit.WorkOrderId == workOrderId).ToListAsync(cancellationToken);

    public void AddAudit(AuditHistory audit) => _db.AuditHistory.Add(audit);

    public async Task<WorkOrderSaveOutcome> SaveChangesAsync(WorkOrder workOrder, byte[] expectedRowVersion, CancellationToken cancellationToken)
    {
        // The UPDATE applies only WHERE row_version = the client's If-Match token (the Work Order's own): two
        // concurrent Cancel calls cannot both succeed, and neither can a Cancel racing any other Work Order change.
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
