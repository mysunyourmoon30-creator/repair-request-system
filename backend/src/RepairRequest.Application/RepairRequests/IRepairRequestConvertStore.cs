using RepairRequest.Application.Common;
using RepairRequest.Application.Security;
using RepairRequest.Domain.Auditing;
using RepairRequest.Domain.WorkOrders;
using RepairRequestAggregate = RepairRequest.Domain.RepairRequests.RepairRequest;

namespace RepairRequest.Application.RepairRequests;

/// <summary>
/// Persistence port for ST-RR-008 Convert (S2-002). A convert runs inside <see cref="RunInTransactionAsync{T}"/>:
/// <see cref="LoadForConvertAsync"/> restricts the lookup to the caller's Coordinator site scope
/// (<see cref="IDataScope.RepairRequests"/> already implements this; no separate ownership check applies, unlike
/// Cancel). The Work Order No. counter, the new Work Order row and the Repair Request's state transition all commit
/// together in one <see cref="SaveChangesAsync"/> call; nothing is written unless the command succeeds.
/// </summary>
public interface IRepairRequestConvertStore
{
    /// <summary>One READ COMMITTED transaction; commits only a successful result.</summary>
    Task<CommandResult<T>> RunInTransactionAsync<T>(Func<Task<CommandResult<T>>> command, CancellationToken cancellationToken);

    /// <summary>Tracked load of a request within the caller's Coordinator site scope; null when absent or out of scope.</summary>
    Task<RepairRequestAggregate?> LoadForConvertAsync(CurrentUser user, Guid repairRequestId, CancellationToken cancellationToken);

    /// <summary>Atomically allocates the next Work Order No. sequence for the tenant and UTC year.</summary>
    Task<int> AllocateWorkOrderNumberAsync(Guid tenantId, int workOrderYear, CancellationToken cancellationToken);

    void Add(WorkOrder workOrder);

    void AddAudit(AuditHistory audit);

    /// <summary>Saves the convert; the update applies only while the request's row version equals <paramref name="expectedRowVersion"/>.</summary>
    Task<RepairRequestSaveOutcome> SaveChangesAsync(RepairRequestAggregate request, byte[] expectedRowVersion, CancellationToken cancellationToken);
}
