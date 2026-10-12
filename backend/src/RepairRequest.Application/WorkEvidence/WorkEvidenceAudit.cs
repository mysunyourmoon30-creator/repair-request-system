using System.Text.Json;
using RepairRequest.Application.Attachments;
using RepairRequest.Application.Common;
using RepairRequest.Domain.Auditing;
using RepairRequest.Domain.Files;
using RepairRequest.Domain.WorkEvidence;

namespace RepairRequest.Application.WorkEvidence;

/// <summary>
/// Audit records of Work Order evidence (`docs/13` §4.28): upload, binding to a submitted Work Summary and authorized download. They use
/// their own entity type (<see cref="EntityType"/>) and are therefore not part of the Work Order timeline (AUD-API-001). Scan results
/// are audited by the existing file scan service on the <c>FILE_ASSET</c> entity. Values carry identifiers and codes only — never file
/// content, storage references, content hashes or tokens.
/// </summary>
public static class WorkEvidenceAudit
{
    public const string EntityType = "WORK_EVIDENCE";
    public const string UploadedAction = "WORK_EVIDENCE_UPLOADED";
    public const string BoundAction = "WORK_EVIDENCE_BOUND";
    public const string DownloadedAction = "WORK_EVIDENCE_DOWNLOADED";

    public static AuditHistory Uploaded(CommandContext context, WorkEvidenceUpload upload, FileAsset file, DateTime occurredAt) =>
        new(
            upload.TenantId,
            EntityType,
            upload.Id,
            UploadedAction,
            fromState: null,
            toState: null,
            oldValueJson: null,
            newValueJson: JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["workOrderId"] = upload.WorkOrderId,
                ["serviceVisitId"] = upload.ServiceVisitId,
                ["evidenceType"] = WorkEvidenceTypeCodes.ToCode(upload.EvidenceType),
                ["fileAssetId"] = file.Id,
                ["fileName"] = file.FileName,
                ["mimeType"] = file.MimeType,
                ["sizeBytes"] = file.SizeBytes,
                ["malwareScanStatus"] = MalwareScanStatusCodes.ToCode(file.MalwareScanStatus)
            }),
            reason: null,
            context.User.UserId,
            occurredAt,
            context.CorrelationId);

    public static AuditHistory Bound(CommandContext context, WorkSummaryEvidence binding, Guid workOrderId, Guid serviceVisitId, DateTime occurredAt) =>
        new(
            binding.TenantId,
            EntityType,
            binding.Id,
            BoundAction,
            fromState: null,
            toState: null,
            oldValueJson: null,
            newValueJson: JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["workOrderId"] = workOrderId,
                ["serviceVisitId"] = serviceVisitId,
                ["workSummaryId"] = binding.WorkSummaryId,
                ["evidenceType"] = WorkEvidenceTypeCodes.ToCode(binding.EvidenceType),
                ["fileAssetId"] = binding.FileAssetId,
                ["slotNo"] = binding.SlotNo
            }),
            reason: null,
            context.User.UserId,
            occurredAt,
            context.CorrelationId);

    public static AuditHistory Downloaded(CommandContext context, WorkEvidenceDownloadInfo file, DateTime occurredAt) =>
        new(
            file.TenantId,
            EntityType,
            file.EvidenceId,
            DownloadedAction,
            fromState: null,
            toState: null,
            oldValueJson: null,
            newValueJson: JsonSerializer.Serialize(new Dictionary<string, object?> { ["workOrderId"] = file.WorkOrderId }),
            reason: null,
            context.User.UserId,
            occurredAt,
            context.CorrelationId);
}
