using RepairRequest.Application.Common;
using RepairRequest.Application.Security;
using RepairRequest.Domain.Auditing;
using RepairRequest.Domain.WorkOrders;

namespace RepairRequest.Application.WorkOrders;

/// <summary>A Service Visit loaded for Check-in, with its (tracked) parent Work Order.</summary>
public sealed record ServiceVisitForCheckIn(ServiceVisit Visit, WorkOrder WorkOrder);

/// <summary>
/// Persistence port for "My Visits" (read) and Check-in (S3-001; ST-WS-001). The read side is a focused
/// projection (mirrors <see cref="IWorkOrderStore"/>); the write side follows the same one-transaction,
/// RowVersion-guarded shape as <see cref="IWorkOrderScheduleStore"/> and <see cref="IServiceVisitStore"/>.
/// </summary>
public interface IWorkSessionStore
{
    Task<PagedResult<MyVisitSummaryDto>> ListMineAsync(CurrentUser user, MyVisitsQuery query, CancellationToken cancellationToken);

    Task<CommandResult<T>> RunInTransactionAsync<T>(Func<Task<CommandResult<T>>> command, CancellationToken cancellationToken);

    Task<ServiceVisitForCheckIn?> LoadForCheckInAsync(CurrentUser user, Guid serviceVisitId, CancellationToken cancellationToken);

    /// <summary>BR-05 "no active overlap": does this technician already hold a non-CHECKED_OUT Work Session anywhere?</summary>
    Task<bool> HasActiveSessionAsync(Guid tenantId, Guid technicianId, CancellationToken cancellationToken);

    void Add(WorkSession session);

    void AddAudit(AuditHistory audit);

    /// <summary>Saves the Work Order (tracked, no client token — its own RowVersion is EF's normal optimistic check), the
    /// Visit (guarded by <paramref name="expectedRowVersion"/>, the client's If-Match) and the new Work Session together.</summary>
    Task<WorkOrderSaveOutcome> SaveChangesAsync(ServiceVisit visit, byte[] expectedRowVersion, CancellationToken cancellationToken);

    // ---- S3-002/S3-003 Pause, Resume, current session (all restricted by IDataScope.OwnWorkSessions) ----

    /// <summary>The caller's own Work Session, tracked for update; null when nonexistent, someone else's, cross-tenant or out of Site scope.</summary>
    Task<WorkSession?> LoadOwnForUpdateAsync(CurrentUser user, Guid workSessionId, CancellationToken cancellationToken);

    void AddPause(WorkSessionPause pause);

    /// <summary>
    /// The session's currently open pause period (S3-003), tracked for update; null if none. A PAUSED session
    /// invariantly has exactly one (Pause and Resume are its only writers), so null here means a data integrity
    /// fault, not a normal outcome — the caller treats it defensively, not as a crash.
    /// </summary>
    Task<WorkSessionPause?> LoadOpenPauseAsync(Guid workSessionId, CancellationToken cancellationToken);

    /// <summary>Saves the session (guarded by <paramref name="expectedRowVersion"/>, the client's If-Match) and any tracked pause period together.</summary>
    Task<WorkOrderSaveOutcome> SaveChangesAsync(WorkSession session, byte[] expectedRowVersion, CancellationToken cancellationToken);

    Task<WorkSessionDto?> GetOwnSessionAsync(CurrentUser user, Guid workSessionId, CancellationToken cancellationToken);

    /// <summary>The caller's one non-CHECKED_OUT session (BR-05 allows at most one), or null.</summary>
    Task<WorkSessionDto?> GetCurrentAsync(CurrentUser user, CancellationToken cancellationToken);
}
