using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using RepairRequest.Application.Common;
using RepairRequest.Application.RepairRequests;
using RepairRequest.Application.Security;
using RepairRequest.Domain.Auditing;
using RepairRequest.Domain.WorkOrders;
using RepairRequest.Infrastructure.Persistence;
using RepairRequest.Infrastructure.WorkOrders;
using RepairRequestAggregate = RepairRequest.Domain.RepairRequests.RepairRequest;

namespace RepairRequest.Infrastructure.RepairRequests;

/// <summary>
/// EF Core implementation of the Convert port (S2-002).
/// <list type="bullet">
/// <item>Scope first: <see cref="IDataScope.RepairRequests"/> already restricts a Coordinator to their assigned
/// Sites, so an out-of-scope or nonexistent request never proceeds. Unlike Cancel, Convert has no ownership check
/// and no shared review-workflow lock — the Repair Request's own optimistic-concurrency RowVersion, combined with
/// the database's unique constraint on <c>work_order.repair_request_id</c> (WO-004), is already sufficient to
/// serialize two concurrent Converts of the same request: whichever commits first wins, and the loser fails either
/// the RowVersion check or, in the rarer window where both read the same RowVersion, the unique constraint at
/// save time — both surface as the same 409, and both roll back everything.</item>
/// <item>Backstop: the repair_request UPDATE applies only WHERE row_version = If-Match.</item>
/// </list>
/// </summary>
internal sealed class RepairRequestConvertStore : IRepairRequestConvertStore
{
    private const int UniqueIndexViolation = 2601;
    private const int UniqueConstraintViolation = 2627;
    private const int DeadlockVictim = 1205;

    private readonly RepairRequestDbContext _db;
    private readonly IDataScope _scope;

    public RepairRequestConvertStore(RepairRequestDbContext db, IDataScope scope)
    {
        _db = db;
        _scope = scope;
    }

    public async Task<CommandResult<T>> RunInTransactionAsync<T>(Func<Task<CommandResult<T>>> command, CancellationToken cancellationToken)
    {
        try
        {
            // Disposing without commit rolls back the number allocation, the Work Order insert, the transition and
            // the audit together.
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

    public Task<RepairRequestAggregate?> LoadForConvertAsync(CurrentUser user, Guid repairRequestId, CancellationToken cancellationToken) =>
        _scope.RepairRequests(user).SingleOrDefaultAsync(request => request.Id == repairRequestId, cancellationToken);

    public async Task<int> AllocateWorkOrderNumberAsync(Guid tenantId, int workOrderYear, CancellationToken cancellationToken)
    {
        var year = checked((short)workOrderYear);

        var values = await _db.Database.SqlQuery<int>($"""
            MERGE [work_order_no_counter] WITH (HOLDLOCK) AS [target]
            USING (VALUES ({tenantId}, {year})) AS [source] ([tenant_id], [work_order_year])
                ON [target].[tenant_id] = [source].[tenant_id] AND [target].[work_order_year] = [source].[work_order_year]
            WHEN MATCHED THEN
                UPDATE SET [last_value] = [target].[last_value] + 1
            WHEN NOT MATCHED THEN
                INSERT ([tenant_id], [work_order_year], [last_value]) VALUES ([source].[tenant_id], [source].[work_order_year], 1)
            OUTPUT [inserted].[last_value] AS [Value];
            """).ToListAsync(cancellationToken);

        return values.Single();
    }

    public void Add(WorkOrder workOrder) => _db.WorkOrders.Add(workOrder);

    public void AddAudit(AuditHistory audit) => _db.AuditHistory.Add(audit);

    public async Task<RepairRequestSaveOutcome> SaveChangesAsync(
        RepairRequestAggregate request,
        byte[] expectedRowVersion,
        CancellationToken cancellationToken)
    {
        // The UPDATE applies only WHERE row_version = the client's If-Match token.
        _db.Entry(request).Property(item => item.RowVersion).OriginalValue = expectedRowVersion;

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return RepairRequestSaveOutcome.Saved;
        }
        catch (DbUpdateConcurrencyException)
        {
            _db.ChangeTracker.Clear();
            return RepairRequestSaveOutcome.ConcurrencyConflict;
        }
        catch (DbUpdateException exception) when (SqlErrorNumber(exception) is UniqueIndexViolation or UniqueConstraintViolation or DeadlockVictim)
        {
            // WO-004: a second Work Order for the same request, or a Work Order No. collision, is the same
            // "already changed underneath you" situation as a stale RowVersion.
            _db.ChangeTracker.Clear();
            return RepairRequestSaveOutcome.ConcurrencyConflict;
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
