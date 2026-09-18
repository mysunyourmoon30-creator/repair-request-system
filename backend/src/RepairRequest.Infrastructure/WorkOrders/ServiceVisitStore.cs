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
/// EF Core implementation of the Service Visit action port (S2-003; ST-SV-004..009). Every lookup joins through
/// <see cref="IDataScope.WorkOrders"/>, so an out-of-scope or nonexistent Visit's Work Order never proceeds — same
/// scope correlation <c>WorkOrderStore</c> uses for reads.
/// </summary>
internal sealed class ServiceVisitStore : IServiceVisitStore
{
    private const int UniqueIndexViolation = 2601;
    private const int UniqueConstraintViolation = 2627;
    private const int DeadlockVictim = 1205;

    private readonly RepairRequestDbContext _db;
    private readonly IDataScope _scope;

    public ServiceVisitStore(RepairRequestDbContext db, IDataScope scope)
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

    public async Task<ServiceVisitForManage?> LoadForManageAsync(CurrentUser user, Guid serviceVisitId, CancellationToken cancellationToken)
    {
        var scopedWorkOrders = _scope.WorkOrders(user);

        var row = await (
            from visit in _db.ServiceVisits
            join workOrder in scopedWorkOrders on visit.WorkOrderId equals workOrder.Id
            where visit.Id == serviceVisitId
            select new { Visit = visit, WorkOrderId = workOrder.Id, workOrder.TenantId, workOrder.RepairRequestId })
            .SingleOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            return null;
        }

        var siteId = await _db.RepairRequests
            .Where(request => request.Id == row.RepairRequestId)
            .Select(request => request.SiteId)
            .SingleOrDefaultAsync(cancellationToken);

        return new ServiceVisitForManage(row.Visit, row.TenantId, row.WorkOrderId, siteId);
    }

    public Task<bool> IsTechnicianEligibleAsync(Guid tenantId, Guid technicianId, Guid siteId, CancellationToken cancellationToken) =>
        TechnicianEligibility.IsEligibleAsync(_db, tenantId, technicianId, siteId, cancellationToken);

    public void Add(ServiceVisit visit) => _db.ServiceVisits.Add(visit);

    public void AddAudit(AuditHistory audit) => _db.AuditHistory.Add(audit);

    public async Task<WorkOrderSaveOutcome> SaveChangesAsync(ServiceVisit visit, byte[] expectedRowVersion, CancellationToken cancellationToken)
    {
        // The UPDATE applies only WHERE row_version = the client's If-Match token (the Visit's own, not the Work Order's).
        _db.Entry(visit).Property(item => item.RowVersion).OriginalValue = expectedRowVersion;

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
