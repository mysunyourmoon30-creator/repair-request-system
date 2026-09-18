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

/// <summary>
/// EF Core implementation of the Schedule port (S2-003). Scope first: <see cref="IDataScope.WorkOrders"/> already
/// restricts a Coordinator to their assigned Sites (correlated through the Work Order's Repair Request), so an
/// out-of-scope or nonexistent Work Order never proceeds. The Work Order's own RowVersion, combined with the
/// database's own constraints, is sufficient to serialize concurrent Schedule attempts on the same Work Order —
/// same reasoning as <c>RepairRequestConvertStore</c>.
/// </summary>
internal sealed class WorkOrderScheduleStore : IWorkOrderScheduleStore
{
    private const int UniqueIndexViolation = 2601;
    private const int UniqueConstraintViolation = 2627;
    private const int DeadlockVictim = 1205;

    private readonly RepairRequestDbContext _db;
    private readonly IDataScope _scope;

    public WorkOrderScheduleStore(RepairRequestDbContext db, IDataScope scope)
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

    public async Task<WorkOrderForSchedule?> LoadForScheduleAsync(CurrentUser user, Guid workOrderId, CancellationToken cancellationToken)
    {
        var workOrder = await _scope.WorkOrders(user).SingleOrDefaultAsync(item => item.Id == workOrderId, cancellationToken);
        if (workOrder is null)
        {
            return null;
        }

        var siteId = await _db.RepairRequests
            .Where(request => request.Id == workOrder.RepairRequestId)
            .Select(request => request.SiteId)
            .SingleOrDefaultAsync(cancellationToken);

        return new WorkOrderForSchedule(workOrder, siteId);
    }

    public Task<bool> IsTechnicianEligibleAsync(Guid tenantId, Guid technicianId, Guid siteId, CancellationToken cancellationToken) =>
        TechnicianEligibility.IsEligibleAsync(_db, tenantId, technicianId, siteId, cancellationToken);

    public void Add(ServiceVisit visit) => _db.ServiceVisits.Add(visit);

    public void AddAudit(AuditHistory audit) => _db.AuditHistory.Add(audit);

    public async Task<WorkOrderSaveOutcome> SaveChangesAsync(WorkOrder workOrder, byte[] expectedRowVersion, CancellationToken cancellationToken)
    {
        // The UPDATE applies only WHERE row_version = the client's If-Match token.
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
        catch (DbUpdateException exception) when (SqlErrorNumber(exception) is UniqueIndexViolation or UniqueConstraintViolation or DeadlockVictim)
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
