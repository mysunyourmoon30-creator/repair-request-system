using RepairRequest.Application.Common;
using RepairRequest.Application.Security;
using RepairRequest.Domain.Auditing;
using RepairRequest.Domain.WorkOrders;

namespace RepairRequest.Application.WorkOrders;

/// <summary>
/// A Corrective Action loaded for Schedule Rework, with its Work Order and the Work Order's Repair Request's Site
/// (for the technician eligibility check) — `docs/13` §4.22.
/// </summary>
public sealed record CorrectiveActionForScheduleRework(WorkOrder WorkOrder, CorrectiveAction CorrectiveAction, Guid? SiteId);

/// <summary>
/// Persistence port for Corrective Action Submit Plan (ST-CA-002; CA-API-001), Approve Plan (ST-CA-003;
/// CA-API-002) — `docs/13` §4.21; Ticket 6 — and Schedule Rework (CA-API-003; `docs/13` §4.22). Submit/Approve
/// Plan load the Corrective Action together with its Work Order (tracked) and save both in one transaction,
/// guarded by the Work Order's own RowVersion — see the interface's own remarks on why no `corrective_action`
/// concurrency token existed before Schedule Rework needed one.
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

    /// <summary>
    /// Loads the Corrective Action for Schedule Rework, or null unless it exists and its Work Order is within the
    /// caller's <see cref="IDataScope.WorkOrders"/> scope — same non-leaking shape as <see cref="LoadInScopeAsync"/>.
    /// </summary>
    Task<CorrectiveActionForScheduleRework?> LoadForScheduleReworkAsync(CurrentUser user, Guid correctiveActionId, CancellationToken cancellationToken);

    Task<bool> IsTechnicianEligibleAsync(Guid tenantId, Guid technicianId, Guid siteId, CancellationToken cancellationToken);

    void Add(ServiceVisit visit);

    /// <summary>
    /// Saves the Corrective Action (and, via the same tracked context, the new Service Visit), guarded by
    /// <paramref name="expectedRowVersion"/> — the Corrective Action's own token this time (`docs/13` §4.22), not
    /// the Work Order's, since Schedule Rework changes neither the Work Order's Status nor its RowVersion.
    /// </summary>
    Task<WorkOrderSaveOutcome> SaveScheduleReworkAsync(CorrectiveAction correctiveAction, byte[] expectedRowVersion, CancellationToken cancellationToken);
}
