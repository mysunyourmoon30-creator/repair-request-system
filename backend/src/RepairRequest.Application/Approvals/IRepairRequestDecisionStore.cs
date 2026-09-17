using RepairRequest.Application.Common;
using RepairRequest.Application.RepairRequests;
using RepairRequest.Application.Security;
using RepairRequest.Domain.Approvals;
using RepairRequest.Domain.Auditing;
using RepairRequestAggregate = RepairRequest.Domain.RepairRequests.RepairRequest;

namespace RepairRequest.Application.Approvals;

/// <summary>
/// Persistence port for the ST-RR-004/005 Approve/Reject decisions (S1-008). A decision runs inside
/// <see cref="RunInTransactionAsync{T}"/>: <see cref="LockForDecisionAsync"/> confirms the request is in the caller's Repair
/// Request data scope, takes the request's review-workflow lock with a bounded wait (shared with S1-007R routing) and loads
/// the current row, so competing decisions and routing attempts of one request serialize. Nothing is committed unless the
/// command succeeds.
/// </summary>
public interface IRepairRequestDecisionStore
{
    /// <summary>
    /// One READ COMMITTED transaction; commits only a successful result. A lock that is not granted in time, or a deadlock,
    /// becomes a concurrency conflict; other database failures propagate.
    /// </summary>
    Task<CommandResult<T>> RunInTransactionAsync<T>(Func<Task<CommandResult<T>>> command, CancellationToken cancellationToken);

    /// <summary>Tracked, locked load within the caller's S1-003 Repair Request scope; null when absent or out of scope.</summary>
    Task<RepairRequestAggregate?> LockForDecisionAsync(CurrentUser user, Guid repairRequestId, CancellationToken cancellationToken);

    /// <summary>Tracked approval row of one step in the current (highest) approval cycle, or null (DEC-PRE-S1-010-01).</summary>
    Task<RepairRequestApproval?> FindStepApprovalAsync(Guid tenantId, Guid repairRequestId, short stepNo, CancellationToken cancellationToken);

    void AddAudit(AuditHistory audit);

    /// <summary>Saves the decision; the request update applies only while its row version equals <paramref name="expectedRowVersion"/>.</summary>
    Task<RepairRequestSaveOutcome> SaveChangesAsync(RepairRequestAggregate request, byte[] expectedRowVersion, CancellationToken cancellationToken);
}
