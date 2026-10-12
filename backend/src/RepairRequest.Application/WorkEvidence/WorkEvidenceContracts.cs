using RepairRequest.Domain.Files;
using RepairRequest.Domain.WorkEvidence;
using RepairRequest.Domain.WorkOrders;

namespace RepairRequest.Application.WorkEvidence;

/// <summary>
/// Who the caller is for one Work Order's evidence and Service Report (`docs/16` §4.1, OD5). Resolved on the server from existing
/// rules only: an in-scope Team Lead or Supervisor (<c>IDataScope.WorkOrders</c>), a Technician assigned to a Visit of the Work Order
/// (<c>IDataScope.AssignedServiceVisits</c>), or exactly the designated Acceptance Contact. A Requester who is not that contact, and
/// every other role, resolve to <see cref="None"/> — the role alone grants nothing.
/// </summary>
public enum ReportAudience
{
    None,
    Technician,
    TeamLeadSupervisor,
    AcceptanceContact
}

/// <summary>An uploaded evidence file as received by the API. Name, content type and length are client-reported and untrusted.</summary>
public sealed record WorkEvidenceFile(string? FileName, string? ContentType, long Length, Func<Stream> OpenReadStream);

/// <summary>
/// Where the caller's next Work Summary round stands: the Work Order status, the Service Visit the Submit would describe (the caller's
/// latest own session's Visit, exactly as Submit selects it) and whether that Visit already has a Work Summary.
/// </summary>
public sealed record WorkEvidenceUploadTarget(Guid TenantId, Guid WorkOrderId, WorkOrderStatus Status, Guid ServiceVisitId, bool VisitHasWorkSummary);

/// <summary>
/// One upload looked up for a Submit's <c>evidenceIds</c>: the upload (only found inside the caller's tenant, Work Order and current Visit),
/// the persisted scan status of its file, and whether it is already bound to a Work Summary.
/// </summary>
public sealed record WorkEvidenceSelectionRow(WorkEvidenceUpload Upload, MalwareScanStatus ScanStatus, bool Bound);

/// <summary>Evidence metadata. Never carries a storage reference, content hash or uploader identity.</summary>
public sealed record WorkEvidenceItemDto(
    Guid EvidenceId,
    Guid ServiceVisitId,
    WorkEvidenceType EvidenceType,
    string FileName,
    string MimeType,
    long SizeBytes,
    MalwareScanStatus ScanStatus,
    bool Bound,
    Guid? WorkSummaryId,
    int? SlotNo,
    DateTime UploadedAt);

/// <summary>Scoped lookup data of one evidence item for an authorized download.</summary>
public sealed record WorkEvidenceDownloadInfo(
    Guid TenantId,
    Guid WorkOrderId,
    Guid EvidenceId,
    ReportAudience Audience,
    bool Bound,
    string FileName,
    string MimeType,
    long SizeBytes,
    string StorageReference,
    MalwareScanStatus ScanStatus);

/// <summary>A CLEAN file ready to stream. The caller owns and disposes <see cref="Content"/>.</summary>
public sealed record WorkEvidenceDownload(Stream Content, string FileName, string MimeType, long SizeBytes);

public static class WorkEvidenceFields
{
    public const string File = "file";
    public const string EvidenceType = "evidenceType";

    /// <summary>Validation field of the Work Summary Submit evidence selection (the request property name).</summary>
    public const string EvidenceIds = "evidenceIds";
}
