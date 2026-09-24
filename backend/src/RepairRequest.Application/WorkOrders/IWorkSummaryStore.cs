using RepairRequest.Application.Common;
using RepairRequest.Application.Security;
using RepairRequest.Domain.Auditing;
using RepairRequest.Domain.WorkOrders;

namespace RepairRequest.Application.WorkOrders;

/// <summary>
/// A Work Order loaded for Submit Work Summary (S4; ST-WO-003), together with the caller's own checked-out
/// Service Visit it describes — null when the caller has a Visit assigned on this Work Order but none of their
/// own sessions on it has reached CHECKED_OUT yet (a 409 STATE_CONFLICT, not a 404: the caller does have a
/// legitimate claim on this Work Order, just not yet in the right state).
/// </summary>
public sealed record WorkOrderForSummarySubmit(WorkOrder WorkOrder, ServiceVisit? Visit);

/// <summary>
/// Persistence port for Work Summary Submit (Technician; ST-WO-003), Submit for Acceptance (Team Lead/Supervisor;
/// ST-WO-004) and the Work Summary read, per `docs/13` §4.15. Follows the same one-transaction, RowVersion-guarded
/// shape as <see cref="IWorkSessionStore"/> — the Work Order (the resource named in both endpoints' URLs) carries
/// the client's If-Match token for both actions.
/// </summary>
public interface IWorkSummaryStore
{
    Task<CommandResult<T>> RunInTransactionAsync<T>(Func<Task<CommandResult<T>>> command, CancellationToken cancellationToken);

    /// <summary>
    /// The Work Order for Submit (tracked), scoped to the caller: null (404) when it does not exist, is outside
    /// the caller's tenant, or the caller has never had any of their own Work Sessions on any Visit of it
    /// (own-scope, mirrors <see cref="IDataScope.OwnWorkSessions"/> across every session status, not just
    /// CHECKED_OUT — ownership and state are checked separately, the same convention every other command in this
    /// codebase uses). When the Work Order is found but none of the caller's own sessions on it has reached
    /// CHECKED_OUT yet, the record's Visit is null (a 409 STATE_CONFLICT case, not 404).
    /// </summary>
    Task<WorkOrderForSummarySubmit?> LoadForSubmitAsync(CurrentUser user, Guid workOrderId, CancellationToken cancellationToken);

    /// <summary>
    /// The Work Order for Submit for Acceptance (tracked), within <see cref="IDataScope.WorkOrders"/> site-wide
    /// scope (Team Lead/Supervisor); null when nonexistent or out of scope.
    /// </summary>
    Task<WorkOrder?> LoadForAcceptanceSubmitAsync(CurrentUser user, Guid workOrderId, CancellationToken cancellationToken);

    void Add(WorkSummary summary);

    void AddAudit(AuditHistory audit);

    /// <summary>Saves the Work Order, guarded by <paramref name="expectedRowVersion"/> (the client's If-Match, the Work Order's own token), and any tracked new Work Summary together.</summary>
    Task<WorkOrderSaveOutcome> SaveChangesAsync(WorkOrder workOrder, byte[] expectedRowVersion, CancellationToken cancellationToken);

    /// <summary>
    /// The Work Order's current Work Summary, scoped to the caller: for a Technician, only when they are the
    /// assigned Technician of the Visit the summary describes; for Team Lead/Supervisor, within
    /// <see cref="IDataScope.WorkOrders"/> site-wide scope. Null when none exists yet or the caller is out of
    /// scope (identical, non-leaking response either way).
    /// </summary>
    Task<WorkSummaryDto?> GetWorkSummaryAsync(CurrentUser user, Guid workOrderId, CancellationToken cancellationToken);

    /// <summary>
    /// Validates <paramref name="acceptanceContactId"/> as an eligible Acceptance Contact (`docs/13` §4.16
    /// Decision 1: an existing REQUESTER of <paramref name="tenantId"/>, Site-scoped to the Work Order's Site,
    /// resolved through <paramref name="repairRequestId"/>) and, if eligible, builds the server-derived snapshot
    /// (display name + email) to persist. Null when the Work Order's Repair Request has no Site, or the
    /// candidate is not eligible — the caller never bypasses this with a client-supplied snapshot.
    /// </summary>
    Task<string?> ResolveAcceptanceContactSnapshotAsync(Guid tenantId, Guid repairRequestId, Guid acceptanceContactId, CancellationToken cancellationToken);

    /// <summary>
    /// Every REQUESTER eligible to be designated as this Work Order's Acceptance Contact (`docs/13` §4.16,
    /// technical lookup `ACC-API-ADD-001`) — Team Lead/Supervisor only, within <see cref="IDataScope.WorkOrders"/>
    /// site-wide scope for the Work Order itself. Empty when the Work Order is out of scope, nonexistent, or its
    /// Repair Request has no Site.
    /// </summary>
    Task<IReadOnlyList<EligibleAcceptanceContactDto>> ListEligibleAcceptanceContactsAsync(CurrentUser user, Guid workOrderId, CancellationToken cancellationToken);
}
