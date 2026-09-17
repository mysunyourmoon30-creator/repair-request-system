using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using RepairRequest.Application.Common;
using RepairRequest.Application.RepairRequests;
using RepairRequest.Application.Security;
using RepairRequest.Domain.Auditing;
using RepairRequest.Infrastructure.Approvals;
using RepairRequest.Infrastructure.Persistence;
using RepairRequestAggregate = RepairRequest.Domain.RepairRequests.RepairRequest;

namespace RepairRequest.Infrastructure.RepairRequests;

/// <summary>
/// EF Core implementation of the Cancel port (S1-009).
/// <list type="bullet">
/// <item>Owner and scope first: an EXISTS through <see cref="IDataScope.RepairRequests"/> with created_by = caller, so a
/// request the caller does not own never takes a lock.</item>
/// <item>Serialization: the request's review-workflow application lock (<see cref="RepairRequestRoutingLock"/>, shared with
/// routing and Approve/Reject) with a bounded wait; then the owned row is loaded, so a competing command sees the committed
/// winner and fails the row-version check. A lock that is not granted in time writes nothing (409).</item>
/// <item>Backstop: the repair_request UPDATE applies only WHERE row_version = If-Match.</item>
/// <item>Bounded: primary-key seeks only; approval rows, attachments and lookups are never loaded.</item>
/// </list>
/// </summary>
internal sealed class RepairRequestCancelStore : IRepairRequestCancelStore
{
    private const int DeadlockVictim = 1205;
    private const int LockRequestTimeout = 1222;

    private readonly RepairRequestDbContext _db;
    private readonly IDataScope _scope;
    private readonly RepairRequestRoutingLockOptions _lockOptions;

    public RepairRequestCancelStore(RepairRequestDbContext db, IDataScope scope, RepairRequestRoutingLockOptions lockOptions)
    {
        _db = db;
        _scope = scope;
        _lockOptions = lockOptions;
    }

    /// <summary>Lock contention only: the review-workflow key was not granted within the configured wait.</summary>
    private sealed class CancelLockUnavailableException : Exception;

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
        catch (CancelLockUnavailableException)
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

    public async Task<RepairRequestAggregate?> LockOwnForCancelAsync(CurrentUser user, Guid repairRequestId, CancellationToken cancellationToken)
    {
        var userId = user.UserId;
        if (!await _scope.RepairRequests(user).AnyAsync(request => request.Id == repairRequestId && request.CreatedBy == userId, cancellationToken))
        {
            return null;
        }

        if (!await RepairRequestRoutingLock.TryAcquireAsync(_db, repairRequestId, _lockOptions, cancellationToken))
        {
            throw new CancelLockUnavailableException();
        }

        return await _scope.RepairRequests(user)
            .SingleOrDefaultAsync(request => request.Id == repairRequestId && request.CreatedBy == userId, cancellationToken);
    }

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
