using System.Text.Json;
using RepairRequest.Application.Common;
using RepairRequest.Application.RepairRequests;
using RepairRequest.Domain.Auditing;
using RepairRequest.Domain.Files;
using RepairRequest.Domain.RepairRequests;

namespace RepairRequest.Application.Attachments;

/// <summary>
/// Audit records for attachment upload, download ("File/access audit", TC-RR-002; RR-DBD-001 section 7) and scan results
/// ("file scan events as applicable", UC-RR-001). Values never include file content, storage keys, tokens or headers.
/// </summary>
public static class AttachmentAudit
{
    public const string FileAssetEntityType = "FILE_ASSET";
    public const string AttachmentAddedAction = "REPAIR_REQUEST_ATTACHMENT_ADDED";
    public const string AttachmentDownloadedAction = "REPAIR_REQUEST_ATTACHMENT_DOWNLOADED";
    public const string ScanCleanAction = "FILE_ASSET_SCAN_CLEAN";
    public const string ScanFailedAction = "FILE_ASSET_SCAN_FAILED";

    public static AuditHistory Added(CommandContext context, RepairRequestAttachment attachment, FileAsset file, DateTime occurredAt) =>
        new(
            file.TenantId,
            RepairRequestAudit.EntityType,
            attachment.RepairRequestId,
            AttachmentAddedAction,
            fromState: null,
            toState: null,
            oldValueJson: null,
            newValueJson: JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["attachmentId"] = attachment.Id,
                ["fileAssetId"] = file.Id,
                ["fileName"] = file.FileName,
                ["mimeType"] = file.MimeType,
                ["sizeBytes"] = file.SizeBytes,
                ["contentHash"] = file.ContentHash,
                ["malwareScanStatus"] = MalwareScanStatusCodes.ToCode(file.MalwareScanStatus)
            }),
            reason: null,
            context.User.UserId,
            occurredAt,
            context.CorrelationId);

    public static AuditHistory Downloaded(CommandContext context, AttachmentDownloadInfo file, DateTime occurredAt) =>
        new(
            file.TenantId,
            RepairRequestAudit.EntityType,
            file.RepairRequestId,
            AttachmentDownloadedAction,
            fromState: null,
            toState: null,
            oldValueJson: null,
            newValueJson: JsonSerializer.Serialize(new Dictionary<string, object?> { ["fileAssetId"] = file.FileAssetId }),
            reason: null,
            context.User.UserId,
            occurredAt,
            context.CorrelationId);

    public static AuditHistory ScanCompleted(FileAsset scannedFile, DateTime occurredAt, Guid correlationId) =>
        new(
            scannedFile.TenantId,
            FileAssetEntityType,
            scannedFile.Id,
            scannedFile.MalwareScanStatus == MalwareScanStatus.Clean ? ScanCleanAction : ScanFailedAction,
            fromState: MalwareScanStatusCodes.ToCode(MalwareScanStatus.Pending),
            toState: MalwareScanStatusCodes.ToCode(scannedFile.MalwareScanStatus),
            oldValueJson: null,
            newValueJson: null,
            reason: null,
            SystemActors.MalwareScanner,
            occurredAt,
            correlationId);
}
