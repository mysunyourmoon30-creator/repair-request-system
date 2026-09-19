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
}
