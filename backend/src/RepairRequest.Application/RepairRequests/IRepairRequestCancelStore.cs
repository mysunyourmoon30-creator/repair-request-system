using RepairRequest.Application.Common;
using RepairRequest.Application.Security;
using RepairRequest.Domain.Auditing;
using RepairRequestAggregate = RepairRequest.Domain.RepairRequests.RepairRequest;

namespace RepairRequest.Application.RepairRequests;

/// <summary>
/// Persistence port for ST-RR-007 Cancel (S1-009). A cancel runs inside <see cref="RunInTransactionAsync{T}"/>:
/// <see cref="LockOwnForCancelAsync"/> confirms the caller owns the request within the S1-003 Repair Request scope, takes the
/// request's review-workflow lock with a bounded wait (shared with routing and Approve/Reject) and loads the current row, so
/// Cancel serializes with those commands. Nothing is committed unless the command succeeds.
/// </summary>
public interface IRepairRequestCancelStore
{
    /// <summary>
    /// One READ COMMITTED transaction; commits only a successful result. A lock that is not granted in time, or a deadlock,
    /// becomes a concurrency conflict; other database failures propagate.
    /// </summary>
    Task<CommandResult<T>> RunInTransactionAsync<T>(Func<Task<CommandResult<T>>> command, CancellationToken cancellationToken);

    /// <summary>Tracked, locked load of a request the caller created and can see; null when absent, not owned or out of scope.</summary>
    Task<RepairRequestAggregate?> LockOwnForCancelAsync(CurrentUser user, Guid repairRequestId, CancellationToken cancellationToken);

    void AddAudit(AuditHistory audit);

    /// <summary>Saves the cancel; the update applies only while the request's row version equals <paramref name="expectedRowVersion"/>.</summary>
    Task<RepairRequestSaveOutcome> SaveChangesAsync(RepairRequestAggregate request, byte[] expectedRowVersion, CancellationToken cancellationToken);
}
