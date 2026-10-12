using RepairRequest.Application.Attachments;
using RepairRequest.Application.Common;
using RepairRequest.Application.Security;
using RepairRequest.Domain.Files;
using RepairRequest.Domain.WorkEvidence;
using RepairRequest.Domain.WorkOrders;

namespace RepairRequest.Application.WorkEvidence;

/// <summary>
/// Work Order evidence (`docs/13` §4.28; `docs/16` D3/D4, OD1/OD2/OD3): the dedicated upload (WEV-API-001), metadata list
/// (WEV-API-002) and authorized CLEAN-only content download (WEV-API-003). It never reuses FILE-API-001/002, but applies the same
/// file policy (PDF/JPG/JPEG/PNG, at most 10 MB, extension + declared type + signature agree, sanitized display name, private opaque
/// storage key) and the same compensation: no committed metadata points at a missing binary, and no binary outlives failed metadata. The only
/// business bound is ten <em>bound</em> items per Work Summary, applied at Submit; uploading itself carries no per-round count, because
/// there is no delete or withdraw in V1 and a count of failed uploads could otherwise block a Visit for good.
/// <list type="bullet">
/// <item>Upload — only a Technician with an own Work Session on the Work Order (404 otherwise), only while the Work Order is IN_PROGRESS
/// and the Visit of their latest session has no Work Summary yet (409), then field validation (422: closed evidence type, file).
/// Everything is serialized with Work Summary Submit by the per-Work-Order evidence lock.</item>
/// <item>The file starts PENDING; the existing scan service decides CLEAN or FAILED. A PENDING or FAILED file is never downloadable.</item>
/// <item>Download — re-checks audience, scope, binding and CLEAN status on every request. A Team Lead, Supervisor or Acceptance
/// Contact receives only evidence bound to a submitted Work Summary; the assigned Technician also their own CLEAN, not yet bound items.</item>
/// </list>
/// </summary>
public sealed class WorkEvidenceService
{
    private readonly IWorkEvidenceStore _store;
    private readonly IFileStorage _storage;
    private readonly TimeProvider _clock;

    public WorkEvidenceService(IWorkEvidenceStore store, IFileStorage storage, TimeProvider clock)
    {
        _store = store;
        _storage = storage;
        _clock = clock;
    }

    public Task<(ReportAudience Audience, IReadOnlyList<WorkEvidenceItemDto> Items)?> ListAsync(
        CurrentUser user, Guid workOrderId, CancellationToken cancellationToken) =>
        _store.ListAsync(user, workOrderId, cancellationToken);

    public async Task<CommandResult<WorkEvidenceItemDto>> UploadAsync(
        CommandContext context, Guid workOrderId, string? evidenceType, WorkEvidenceFile upload, CancellationToken cancellationToken)
    {
        var target = await _store.FindUploadTargetAsync(context.User, workOrderId, cancellationToken);
        if (target is null)
        {
            return CommandError.NotFound;
        }

        var stateError = StateError(target);
        if (stateError is not null)
        {
            return stateError;
        }

        var errors = new Dictionary<string, string[]>();
        if (!WorkEvidenceTypeCodes.TryParse(evidenceType, out var type))
        {
            errors[WorkEvidenceFields.EvidenceType] = [$"evidenceType must be one of: {string.Join(", ", WorkEvidenceTypeCodes.All)}."];
        }

        var fileName = AttachmentFileRules.SanitizeFileName(upload.FileName);
        var fileType = fileName is null ? null : AttachmentFileRules.TypeFor(fileName, upload.ContentType);
        if (fileName is null || fileType is null)
        {
            errors[WorkEvidenceFields.File] = ["Only PDF, JPG, JPEG or PNG files with a matching content type are accepted."];
        }
        else if (upload.Length <= 0)
        {
            errors[WorkEvidenceFields.File] = ["The file is empty."];
        }
        else if (upload.Length > AttachmentFileRules.MaxSizeBytes)
        {
            errors[WorkEvidenceFields.File] = ["The file must not exceed 10 MB."];
        }

        if (errors.Count > 0)
        {
            return CommandError.Validation(errors);
        }

        await using var source = upload.OpenReadStream();
        if (!await AttachmentFileRules.HasSignatureAsync(source, fileType!, cancellationToken))
        {
            return CommandError.Validation(WorkEvidenceFields.File, "The file content does not match its file type.");
        }

        var storageKey = AttachmentFileRules.NewStorageKey(context.User.TenantId);
        await using var hashing = new HashingReadStream(source, AttachmentFileRules.MaxSizeBytes);
        try
        {
            await _storage.SaveAsync(storageKey, hashing, cancellationToken);
        }
        catch (InvalidDataException)
        {
            return CommandError.Validation(WorkEvidenceFields.File, "The file must not exceed 10 MB.");
        }

        try
        {
            var result = await _store.RunInTransactionAsync(
                () => PersistAsync(context, workOrderId, type, fileName!, fileType!, hashing, storageKey, cancellationToken),
                cancellationToken);

            if (!result.Succeeded)
            {
                await _storage.DeleteAsync(storageKey, CancellationToken.None);
            }

            return result;
        }
        catch
        {
            // No committed metadata may point at a binary, and no binary should outlive failed metadata.
            await _storage.DeleteAsync(storageKey, CancellationToken.None);
            throw;
        }
    }

    private async Task<CommandResult<WorkEvidenceItemDto>> PersistAsync(
        CommandContext context,
        Guid workOrderId,
        WorkEvidenceType type,
        string fileName,
        AttachmentFileType fileType,
        HashingReadStream hashing,
        string storageKey,
        CancellationToken cancellationToken)
    {
        // Serialized with Work Summary Submit: a Submit either commits first (the round is closed below) or sees this upload.
        if (!await _store.TryLockWorkOrderAsync(workOrderId, cancellationToken))
        {
            return CommandError.ConcurrencyConflict;
        }

        var target = await _store.FindUploadTargetAsync(context.User, workOrderId, cancellationToken);
        if (target is null)
        {
            return CommandError.NotFound;
        }

        var stateError = StateError(target);
        if (stateError is not null)
        {
            return stateError;
        }

        var now = UtcNow();
        var fileAsset = new FileAsset(
            context.User.TenantId, fileName, fileType.CanonicalMimeType, hashing.BytesRead, hashing.HashHex(), storageKey, context.User.UserId, now);
        _store.AddFileAsset(fileAsset);

        var evidence = WorkEvidenceUpload.Create(target.TenantId, workOrderId, target.ServiceVisitId, fileAsset.Id, type, now);
        _store.AddUpload(evidence);
        _store.AddAudit(WorkEvidenceAudit.Uploaded(context, evidence, fileAsset, now));

        await _store.SaveChangesAsync(cancellationToken);

        return CommandResult<WorkEvidenceItemDto>.Success(new WorkEvidenceItemDto(
            evidence.Id,
            evidence.ServiceVisitId,
            evidence.EvidenceType,
            fileAsset.FileName,
            fileAsset.MimeType,
            fileAsset.SizeBytes,
            fileAsset.MalwareScanStatus,
            Bound: false,
            WorkSummaryId: null,
            SlotNo: null,
            fileAsset.UploadedAt));
    }

    public async Task<CommandResult<WorkEvidenceDownload>> DownloadAsync(
        CommandContext context, Guid workOrderId, Guid evidenceId, CancellationToken cancellationToken)
    {
        var file = await _store.FindDownloadAsync(context.User, workOrderId, evidenceId, cancellationToken);
        if (file is null)
        {
            return CommandError.NotFound;
        }

        // Only the assigned Technician may open their own prepared (not yet bound) items; everyone else only what a submitted
        // Work Summary froze. Not visible is indistinguishable from missing.
        if (!file.Bound && file.Audience != ReportAudience.Technician)
        {
            return CommandError.NotFound;
        }

        // Only CLEAN files are served (OD2/OD3); PENDING and FAILED are never usable as evidence content.
        if (file.ScanStatus != MalwareScanStatus.Clean)
        {
            return CommandError.StateConflict("The evidence file is not available because malware scanning has not marked it CLEAN.");
        }

        var content = await _storage.OpenReadAsync(file.StorageReference, cancellationToken);
        try
        {
            _store.AddAudit(WorkEvidenceAudit.Downloaded(context, file, UtcNow()));
            await _store.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            await content.DisposeAsync();
            throw;
        }

        return CommandResult<WorkEvidenceDownload>.Success(new WorkEvidenceDownload(content, file.FileName, file.MimeType, file.SizeBytes));
    }

    private static CommandError? StateError(WorkEvidenceUploadTarget target)
    {
        if (target.Status != WorkOrderStatus.InProgress)
        {
            return CommandError.StateConflict("Evidence can only be uploaded while the Work Order is IN_PROGRESS.");
        }

        return target.VisitHasWorkSummary
            ? CommandError.StateConflict("This Visit's Work Summary was already submitted; its evidence set is frozen.")
            : null;
    }

    private DateTime UtcNow()
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        return now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMillisecond));
    }
}
