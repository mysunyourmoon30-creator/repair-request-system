using RepairRequest.Application.Common;
using RepairRequest.Application.Security;
using RepairRequest.Domain.Auditing;
using RepairRequest.Domain.WorkOrders;

namespace RepairRequest.Application.WorkOrders;

/// <summary>A Service Visit loaded for a visit-scoped action, with its tenant and its Work Order's Repair Request's Site.</summary>
public sealed record ServiceVisitForManage(ServiceVisit Visit, Guid TenantId, Guid WorkOrderId, Guid? SiteId);

/// <summary>
/// Persistence port for the Service Visit actions (S2-003; ST-SV-004..009): Reschedule, Reassign, Cancel, Mark
/// Missed, Decide Missed. Every lookup is restricted by <see cref="IDataScope.WorkOrders"/>, correlated through
/// the Visit's <c>work_order_id</c>.
/// </summary>
public interface IServiceVisitStore
{
    Task<CommandResult<T>> RunInTransactionAsync<T>(Func<Task<CommandResult<T>>> command, CancellationToken cancellationToken);

    Task<ServiceVisitForManage?> LoadForManageAsync(CurrentUser user, Guid serviceVisitId, CancellationToken cancellationToken);

    Task<bool> IsTechnicianEligibleAsync(Guid tenantId, Guid technicianId, Guid siteId, CancellationToken cancellationToken);

    void Add(ServiceVisit visit);

    void AddAudit(AuditHistory audit);

    Task<WorkOrderSaveOutcome> SaveChangesAsync(ServiceVisit visit, byte[] expectedRowVersion, CancellationToken cancellationToken);
}
