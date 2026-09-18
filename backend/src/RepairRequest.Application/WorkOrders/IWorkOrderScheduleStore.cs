using RepairRequest.Application.Common;
using RepairRequest.Application.Security;
using RepairRequest.Domain.Auditing;
using RepairRequest.Domain.WorkOrders;

namespace RepairRequest.Application.WorkOrders;

public enum WorkOrderSaveOutcome
{
    Saved,

    /// <summary>The row version no longer matched, or a unique constraint collided; nothing was written.</summary>
    ConcurrencyConflict
}

/// <summary>A Work Order loaded for Schedule, with its Repair Request's Site (for the technician eligibility check).</summary>
public sealed record WorkOrderForSchedule(WorkOrder WorkOrder, Guid? SiteId);

/// <summary>Persistence port for Schedule (S2-003; ST-WO-001). Every lookup is restricted by <see cref="IDataScope.WorkOrders"/>.</summary>
public interface IWorkOrderScheduleStore
{
    Task<CommandResult<T>> RunInTransactionAsync<T>(Func<Task<CommandResult<T>>> command, CancellationToken cancellationToken);

    Task<WorkOrderForSchedule?> LoadForScheduleAsync(CurrentUser user, Guid workOrderId, CancellationToken cancellationToken);

    Task<bool> IsTechnicianEligibleAsync(Guid tenantId, Guid technicianId, Guid siteId, CancellationToken cancellationToken);

    void Add(ServiceVisit visit);

    void AddAudit(AuditHistory audit);

    Task<WorkOrderSaveOutcome> SaveChangesAsync(WorkOrder workOrder, byte[] expectedRowVersion, CancellationToken cancellationToken);
}
