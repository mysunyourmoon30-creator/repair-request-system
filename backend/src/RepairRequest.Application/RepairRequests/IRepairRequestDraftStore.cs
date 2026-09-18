using RepairRequest.Application.Common;
using RepairRequest.Application.Security;
using RepairRequest.Domain.Auditing;
using RepairRequestAggregate = RepairRequest.Domain.RepairRequests.RepairRequest;

namespace RepairRequest.Application.RepairRequests;

public enum RepairRequestSaveOutcome
{
    Saved,

    /// <summary>The row version no longer matched; nothing was written.</summary>
    ConcurrencyConflict
}

/// <summary>
/// Focused persistence port for the Repair Request Draft use cases (RR-ARCH-001 section 5.2). Every lookup is
/// restricted by the S1-003 <see cref="IDataScope"/>, so out-of-scope and nonexistent ids are indistinguishable.
/// </summary>
public interface IRepairRequestDraftStore
{
    /// <summary>Detail within the caller's approved Repair Request scope (S1-005 decision E3).</summary>
    Task<RepairRequestDraftDto?> GetAsync(CurrentUser user, Guid repairRequestId, CancellationToken cancellationToken);

    /// <summary>List within the caller's Repair Request scope (S2-002), same scope as <see cref="GetAsync"/>.</summary>
    Task<PagedResult<RepairRequestDraftDto>> ListAsync(CurrentUser user, RepairRequestListQuery query, CancellationToken cancellationToken);

    /// <summary>Tracked load of a request the caller owns (created_by) and can see; otherwise null.</summary>
    Task<RepairRequestAggregate?> FindOwnAsync(CurrentUser user, Guid repairRequestId, CancellationToken cancellationToken);

    /// <summary>
    /// Site status within the caller's business Site scope, Equipment of that Site, tenant Category/Priority lookups and
    /// request-contact eligibility, in one query (see <see cref="DraftSelection"/>).
    /// </summary>
    Task<DraftSelection> GetDraftSelectionAsync(CurrentUser user, DraftSelectionQuery query, CancellationToken cancellationToken);

    void Add(RepairRequestAggregate repairRequest);

    void AddAudit(AuditHistory audit);

    /// <summary>
    /// Saves pending changes in one database transaction. When <paramref name="expectedRowVersion"/> is given, the
    /// update only applies while the request's row version still equals that token.
    /// </summary>
    Task<RepairRequestSaveOutcome> SaveChangesAsync(RepairRequestAggregate repairRequest, byte[]? expectedRowVersion, CancellationToken cancellationToken);
}
