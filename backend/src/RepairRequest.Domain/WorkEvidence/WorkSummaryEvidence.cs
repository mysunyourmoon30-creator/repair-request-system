using RepairRequest.Domain.Common;

namespace RepairRequest.Domain.WorkEvidence;

/// <summary>
/// The immutable evidence snapshot of one submitted Work Summary (baseline <c>work_summary_evidence</c>, EVD-001/003/004/013):
/// the CLEAN <see cref="WorkEvidenceUpload"/> items bound at Submit (<c>docs/16</c> OD1). Rows are only ever inserted, in the same
/// transaction as the Work Summary; nothing updates or deletes them, so later uploads or scan results cannot change a submitted
/// round. <see cref="SlotNo"/> (1..10, unique per Work Summary) makes the maximum of ten a database-enforced rule.
/// </summary>
public sealed class WorkSummaryEvidence
{
    private WorkSummaryEvidence()
    {
    }

    private WorkSummaryEvidence(WorkEvidenceUpload upload, Guid workSummaryId, int slotNo, DateTime boundAt)
    {
        ArgumentNullException.ThrowIfNull(upload);
        if (slotNo is < 1 or > WorkEvidenceRules.MaxPerSubmission)
        {
            throw new ArgumentOutOfRangeException(nameof(slotNo), "A Work Summary binds at most ten evidence items.");
        }

        Id = DomainGuard.NotEmpty(upload.Id, nameof(upload));
        TenantId = upload.TenantId;
        WorkOrderId = upload.WorkOrderId;
        ServiceVisitId = upload.ServiceVisitId;
        WorkSummaryId = DomainGuard.NotEmpty(workSummaryId, nameof(workSummaryId));
        EvidenceType = upload.EvidenceType;
        FileAssetId = upload.FileAssetId;
        SlotNo = slotNo;
        BoundAt = DomainGuard.Utc(boundAt, nameof(boundAt));
    }

    /// <summary>EVD-001: equal to the bound upload's id.</summary>
    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    /// <summary>Copied from the bound upload; the database proves it equals the Work Summary's Work Order and the upload's.</summary>
    public Guid WorkOrderId { get; private set; }

    /// <summary>Copied from the bound upload; the database proves it equals the Work Summary's Visit (the immutable round identity).</summary>
    public Guid ServiceVisitId { get; private set; }

    /// <summary>EVD-003.</summary>
    public Guid WorkSummaryId { get; private set; }

    /// <summary>EVD-004, copied from the upload so the snapshot is self-contained.</summary>
    public WorkEvidenceType EvidenceType { get; private set; }

    /// <summary>EVD-013: the CLEAN file at the time of binding.</summary>
    public Guid FileAssetId { get; private set; }

    /// <summary>1..10, unique per Work Summary; order of upload.</summary>
    public int SlotNo { get; private set; }

    /// <summary>UTC time of the Submit that froze the set.</summary>
    public DateTime BoundAt { get; private set; }

    /// <summary>
    /// Freezes <paramref name="upload"/> into the round of <paramref name="workSummaryId"/>. The caller guarantees the file is CLEAN
    /// (the Application guard reads it under the Work Order evidence lock); scan status is not part of this aggregate.
    /// </summary>
    public static WorkSummaryEvidence Bind(WorkEvidenceUpload upload, Guid workSummaryId, int slotNo, DateTime boundAt) =>
        new(upload, workSummaryId, slotNo, boundAt);
}
