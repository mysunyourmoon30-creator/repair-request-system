using RepairRequest.Application.Common;
using RepairRequest.Application.Security;
using RepairRequest.Domain.Auditing;
using RepairRequest.Domain.WorkOrders;

namespace RepairRequest.Application.WorkOrders;

/// <summary>
/// Persistence port for Corrective Action Submit Plan (ST-CA-002; CA-API-001) and Approve Plan (ST-CA-003;
/// CA-API-002) — `docs/13` §4.21; Ticket 6. Both actions load the Corrective Action together with its Work Order
/// (tracked) and save both in one transaction, guarded by the Work Order's own RowVersion — see the interface's
/// own remarks on why no `corrective_action` concurrency token exists.
/// </summary>
public interface ICorrectiveActionStore
{
    Task<CommandResult<T>> RunInTransactionAsync<T>(Func<Task<CommandResult<T>>> command, CancellationToken cancellationToken);

    /// <summary>
    /// The Corrective Action and its Work Order (both tracked), or (null, null) unless the Corrective Action
    /// exists and its Work Order is within the caller's <see cref="IDataScope.WorkOrders"/> scope — non-leaking:
    /// a wrong tenant, a Corrective Action that does not belong to any Work Order the caller can see, or a
    /// nonexistent id all produce the same (null, null).
    /// </summary>
    Task<(WorkOrder? WorkOrder, CorrectiveAction? CorrectiveAction)> LoadInScopeAsync(CurrentUser user, Guid correctiveActionId, CancellationToken cancellationToken);

    /// <summary>
    /// True only when the plan file exists, belongs to <paramref name="tenantId"/> and has passed the malware
    /// scan (<c>MalwareScanStatus.Clean</c>). `UC-WO-023`'s own Preconditions require *"Plan text + CLEAN
    /// evidence"* (`docs/04`), and its Validation/Authorization line names *"file scan"* explicitly — this is not
    /// a guessed rule. Mirrors the same single EXISTS-style check already used for the Repair Request's own
    /// CLEAN-photo Submit gate (<see cref="RepairRequest.Application.RepairRequests.IRepairRequestSubmitStore.HasCleanPhotoAsync"/>).
    /// </summary>
    Task<bool> IsPlanFileUsableAsync(Guid tenantId, Guid fileAssetId, CancellationToken cancellationToken);

    void AddAudit(AuditHistory audit);

    /// <summary>Saves the Work Order (and, via the same tracked context, the Corrective Action), guarded by <paramref name="expectedRowVersion"/> — the Work Order's own token.</summary>
    Task<WorkOrderSaveOutcome> SaveChangesAsync(WorkOrder workOrder, byte[] expectedRowVersion, CancellationToken cancellationToken);
}
