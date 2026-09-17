using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using RepairRequest.Application.Approvals;
using RepairRequest.Application.Common;
using RepairRequest.Application.RepairRequests;
using RepairRequest.Application.Security;
using RepairRequest.Domain.Approvals;
using RepairRequest.Domain.Auditing;
using RepairRequest.Infrastructure.Persistence;
using RepairRequestAggregate = RepairRequest.Domain.RepairRequests.RepairRequest;

namespace RepairRequest.Infrastructure.Approvals;

/// <summary>
/// EF Core implementation of the Approve/Reject decision port (S1-008).
/// <list type="bullet">
/// <item>Scope first: an EXISTS through <see cref="IDataScope.RepairRequests"/>, so an out-of-scope id never takes a lock.</item>
/// <item>Serialization: the request's review-workflow application lock (<see cref="RepairRequestRoutingLock"/>, shared with
/// routing) with a bounded wait; then the scoped row is loaded, so competing decisions see the committed winner and fail
/// the row-version check. A lock that is not granted in time writes nothing and becomes a concurrency conflict (409).</item>
/// <item>Backstops: the repair_request UPDATE applies only WHERE row_version = If-Match, and the approval row has its own
/// row version.</item>
/// <item>Bounded: primary-key and UNIQUE(request, step) seeks only; no graphs, attachments or lookups.</item>
/// </list>
/// </summary>
internal sealed class RepairRequestDecisionStore : IRepairRequestDecisionStore
{
    private const int DeadlockVictim = 1205;
    private const int LockRequestTimeout = 1222;

    private readonly RepairRequestDbContext _db;
    private readonly IDataScope _scope;
    private readonly RepairRequestRoutingLockOptions _lockOptions;

    public RepairRequestDecisionStore(RepairRequestDbContext db, IDataScope scope, RepairRequestRoutingLockOptions lockOptions)
    {
        _db = db;
        _scope = scope;
        _lockOptions = lockOptions;
    }

    /// <summary>Lock contention only: the review-workflow key was not granted within the configured wait.</summary>
    private sealed class DecisionLockUnavailableException : Exception;

    public async Task<CommandResult<T>> RunInTransactionAsync<T>(Func<Task<CommandResult<T>>> command, CancellationToken cancellationToken)
    {
        try
        {
            // Disposing without commit rolls back and releases the transaction-owned lock.
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
        catch (DecisionLockUnavailableException)
        {
            _db.ChangeTracker.Clear();
            return CommandError.ConcurrencyConflict;
        }
        catch (Exception exception) when (SqlErrorNumber(exception) is DeadlockVictim or LockRequestTimeout)
        {
            _db.ChangeTracker.Clear();
            return CommandError.ConcurrencyConflict;
        }
    }

    public async Task<RepairRequestAggregate?> LockForDecisionAsync(CurrentUser user, Guid repairRequestId, CancellationToken cancellationToken)
    {
        if (!await _scope.RepairRequests(user).AnyAsync(request => request.Id == repairRequestId, cancellationToken))
        {
            return null;
        }

        if (!await RepairRequestRoutingLock.TryAcquireAsync(_db, repairRequestId, _lockOptions, cancellationToken))
        {
            throw new DecisionLockUnavailableException();
        }

        return await _scope.RepairRequests(user).SingleOrDefaultAsync(request => request.Id == repairRequestId, cancellationToken);
    }

    public Task<RepairRequestApproval?> FindStepApprovalAsync(Guid tenantId, Guid repairRequestId, short stepNo, CancellationToken cancellationToken) =>
        _db.RepairRequestApprovals
            .Where(approval => approval.RepairRequestId == repairRequestId && approval.ApprovalStepNo == stepNo && approval.TenantId == tenantId)
            .OrderByDescending(approval => approval.ApprovalCycleNo)
            .FirstOrDefaultAsync(cancellationToken);

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
        catch (DbUpdateException exception) when (SqlErrorNumber(exception) is DeadlockVictim)
        {
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
