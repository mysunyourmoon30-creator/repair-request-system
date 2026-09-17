using RepairRequest.Domain.Auditing;
using RepairRequestAggregate = RepairRequest.Domain.RepairRequests.RepairRequest;

namespace RepairRequest.Application.RepairRequests;

/// <summary>
/// BR-14 duplicate lookup (D-12 active set; DEC-PRE-S1-007-09): same tenant, Site and Category, the same Equipment or
/// likewise no Equipment (null is never a wildcard), no Location, submitted at or after <see cref="WindowStart"/>, excluding
/// the request being submitted.
/// </summary>
public sealed record DuplicateQuery(
    Guid TenantId,
    Guid RepairRequestId,
    Guid SiteId,
    string RequestCategoryCode,
    Guid? EquipmentId,
    DateTime WindowStart);

public enum RepairRequestSubmitStatus
{
    Saved,
    ConcurrencyConflict,
    DuplicateWarning
}

/// <summary>Outcome of the atomic Submit; <see cref="DuplicateCount"/> is the count observed inside the transaction.</summary>
public sealed record RepairRequestSubmitResult(RepairRequestSubmitStatus Status, int DuplicateCount);

/// <summary>
/// Persistence port for ST-RR-002 Submit. Queries are bounded (EXISTS / COUNT on indexed paths) and never load rows
/// (RR-ARCH-001 section 15). The tracked aggregate comes from <see cref="IRepairRequestDraftStore.FindOwnAsync"/> in the
/// same unit of work.
/// </summary>
public interface IRepairRequestSubmitStore
{
    /// <summary>
    /// True when at least one attachment of the request is a CLEAN JPG/JPEG/PNG (DEC-PRE-S1-007-06). PENDING and FAILED
    /// files never count.
    /// </summary>
    Task<bool> HasCleanPhotoAsync(Guid tenantId, Guid repairRequestId, CancellationToken cancellationToken);

    /// <summary>Number of matching active requests; identifiers are never returned (DEC-PRE-S1-007-10).</summary>
    Task<int> CountDuplicatesAsync(DuplicateQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// One atomic transaction, serialized per BR-14 duplicate key so that concurrent Submits of different matching Drafts
    /// cannot both miss each other:
    /// <list type="number">
    /// <item>takes an exclusive lock on the duplicate key of <paramref name="duplicateQuery"/>, held until commit/rollback;</item>
    /// <item>counts duplicates inside that lock; a match without <paramref name="hasContinuationReason"/> returns
    /// <see cref="RepairRequestSubmitStatus.DuplicateWarning"/> and writes nothing;</item>
    /// <item>allocates the next per-tenant, per-year Request No sequence when <paramref name="requestYear"/> is set (a first
    /// Submit), or nothing for a resubmission, which keeps its Request No (null; DEC-PRE-S1-010-02); lets
    /// <paramref name="applySubmit"/> (sequence or null, duplicate count) apply the transition and build its audit record,
    /// and saves the request only while its row version still equals <paramref name="expectedRowVersion"/>.</item>
    /// </list>
    /// Any failure, lock timeout or conflict rolls everything back, including the sequence increment, so a failed Submit
    /// never consumes a number (DEC-PRE-S1-007-09/-11).
    /// </summary>
    Task<RepairRequestSubmitResult> SubmitAsync(
        RepairRequestAggregate request,
        byte[] expectedRowVersion,
        DuplicateQuery duplicateQuery,
        int? requestYear,
        bool hasContinuationReason,
        Func<int?, int, AuditHistory> applySubmit,
        CancellationToken cancellationToken);
}
