using RepairRequest.Domain.Common;

namespace RepairRequest.Domain.WorkEvidence;

/// <summary>
/// An evidence file uploaded for the next Work Summary submission round of a Work Order (`docs/13` §4.28; baseline EVD-001/004/013
/// before binding). It is anchored to the Service Visit the Technician will submit the summary for: that Visit is the immutable
/// identity of the round, because each Work Summary is one row per Visit. The record never changes: the file's scan status lives on
/// the <c>file_asset</c>, and binding creates a separate immutable <see cref="WorkSummaryEvidence"/> row with the same identifier.
/// </summary>
public sealed class WorkEvidenceUpload
{
    private WorkEvidenceUpload()
    {
    }

    private WorkEvidenceUpload(Guid tenantId, Guid workOrderId, Guid serviceVisitId, Guid fileAssetId, WorkEvidenceType evidenceType, DateTime createdAt)
    {
        Id = Guid.CreateVersion7();
        TenantId = DomainGuard.NotEmpty(tenantId, nameof(tenantId));
        WorkOrderId = DomainGuard.NotEmpty(workOrderId, nameof(workOrderId));
        ServiceVisitId = DomainGuard.NotEmpty(serviceVisitId, nameof(serviceVisitId));
        FileAssetId = DomainGuard.NotEmpty(fileAssetId, nameof(fileAssetId));
        if (!Enum.IsDefined(evidenceType))
        {
            throw new ArgumentOutOfRangeException(nameof(evidenceType));
        }

        EvidenceType = evidenceType;
        CreatedAt = DomainGuard.Utc(createdAt, nameof(createdAt));
    }

    /// <summary>Stable evidence identity (EVD-001): the same id is kept when the upload is bound to a Work Summary.</summary>
    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid WorkOrderId { get; private set; }

    /// <summary>The submission round: the Visit whose Work Summary this evidence is prepared for.</summary>
    public Guid ServiceVisitId { get; private set; }

    public Guid FileAssetId { get; private set; }

    /// <summary>EVD-004: closed three-value catalog.</summary>
    public WorkEvidenceType EvidenceType { get; private set; }

    /// <summary>UTC; server-derived; immutable.</summary>
    public DateTime CreatedAt { get; private set; }

    /// <summary>Database-assigned, strictly increasing upload order (never client input); decides slot order when uploads share a timestamp.</summary>
    public long UploadSequence { get; private set; }

    public static WorkEvidenceUpload Create(Guid tenantId, Guid workOrderId, Guid serviceVisitId, Guid fileAssetId, WorkEvidenceType evidenceType, DateTime createdAt) =>
        new(tenantId, workOrderId, serviceVisitId, fileAssetId, evidenceType, createdAt);
}
