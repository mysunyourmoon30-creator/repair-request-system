using RepairRequest.Application.Common;
using RepairRequest.Application.Security;
using RepairRequest.Domain.Files;
using RepairRequest.Domain.RepairRequests;

namespace RepairRequest.Application.Attachments;

/// <summary>
/// Repair Request attachment upload (FILE-API-001) and authorized download (FILE-API-002) — UC-RR-001, TC-RR-002,
/// RR-ARCH-001 section 11. Upload: owner, DRAFT only, allowlisted and signature-checked content, private opaque storage,
/// FileAsset PENDING, audit. Download: Repair Request scope, CLEAN only, audit. Role capability is enforced by the API
/// policies before these run.
/// </summary>
public sealed class RepairRequestAttachmentService
{
    private readonly IAttachmentStore _store;
    private readonly IFileStorage _storage;
    private readonly TimeProvider _clock;

    public RepairRequestAttachmentService(IAttachmentStore store, IFileStorage storage, TimeProvider clock)
    {
        _store = store;
        _storage = storage;
        _clock = clock;
    }

    /// <summary>A bounded page of attachment metadata; null when the Repair Request is not within the caller's scope (404).</summary>
    public Task<PagedResult<AttachmentDto>?> ListAsync(CurrentUser user, Guid repairRequestId, PageRequest paging, CancellationToken cancellationToken) =>
        _store.ListAsync(user, repairRequestId, paging, cancellationToken);

    public async Task<CommandResult<AttachmentDto>> UploadAsync(
        CommandContext context,
        Guid repairRequestId,
        AttachmentUpload upload,
        CancellationToken cancellationToken)
    {
        // Ownership and business scope are part of the lookup: another user's or an out-of-scope request is a 404.
        var status = await _store.GetOwnRequestStatusAsync(context.User, repairRequestId, cancellationToken);
        if (status is null)
        {
            return CommandError.NotFound;
        }

        if (status != RepairRequestStatus.Draft)
        {
            return CommandError.StateConflict("Attachments can only be added while the Repair Request is DRAFT.");
        }

        var fileName = AttachmentFileRules.SanitizeFileName(upload.FileName);
        var type = fileName is null ? null : AttachmentFileRules.TypeFor(fileName, upload.ContentType);
        if (fileName is null || type is null)
        {
            return Invalid("Only PDF, JPG, JPEG or PNG files with a matching content type are accepted.");
        }

        if (upload.Length <= 0)
        {
            return Invalid("The file is empty.");
        }

        if (upload.Length > AttachmentFileRules.MaxSizeBytes)
        {
            return Invalid("The file must not exceed 10 MB.");
        }

        await using var source = upload.OpenReadStream();
        if (!await AttachmentFileRules.HasSignatureAsync(source, type, cancellationToken))
        {
            return Invalid("The file content does not match its file type.");
        }

        var storageKey = AttachmentFileRules.NewStorageKey(context.User.TenantId);
        await using var hashing = new HashingReadStream(source, AttachmentFileRules.MaxSizeBytes);
        try
        {
            await _storage.SaveAsync(storageKey, hashing, cancellationToken);
        }
        catch (InvalidDataException)
        {
            return Invalid("The file must not exceed 10 MB.");
        }

        try
        {
            var now = UtcNow();
            var fileAsset = new FileAsset(
                context.User.TenantId,
                fileName,
                type.CanonicalMimeType,
                hashing.BytesRead,
                hashing.HashHex(),
                storageKey,
                context.User.UserId,
                now);
            _store.AddFileAsset(fileAsset);

            var attachment = new RepairRequestAttachment(repairRequestId, fileAsset.Id, AttachmentFileRules.AccessScopeCode);
            _store.AddAttachment(attachment);
            _store.AddAudit(AttachmentAudit.Added(context, attachment, fileAsset, now));

            await _store.SaveChangesAsync(cancellationToken);

            return CommandResult<AttachmentDto>.Success(new AttachmentDto(
                attachment.Id, fileAsset.Id, fileAsset.FileName, fileAsset.MimeType, fileAsset.SizeBytes, fileAsset.MalwareScanStatus, fileAsset.UploadedAt));
        }
        catch
        {
            // No committed metadata may point at a binary, and no binary should outlive failed metadata.
            await _storage.DeleteAsync(storageKey, CancellationToken.None);
            throw;
        }
    }

    public async Task<CommandResult<AttachmentDownload>> DownloadAsync(
        CommandContext context,
        Guid fileAssetId,
        CancellationToken cancellationToken)
    {
        var file = await _store.FindDownloadAsync(context.User, fileAssetId, cancellationToken);
        if (file is null)
        {
            return CommandError.NotFound;
        }

        // Only CLEAN files are served (RR-ARCH-001 section 11); PENDING and FAILED are not usable.
        if (file.MalwareScanStatus != MalwareScanStatus.Clean)
        {
            return CommandError.StateConflict("The file is not available because malware scanning has not marked it CLEAN.");
        }

        var content = await _storage.OpenReadAsync(file.StorageReference, cancellationToken);
        try
        {
            _store.AddAudit(AttachmentAudit.Downloaded(context, file, UtcNow()));
            await _store.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            await content.DisposeAsync();
            throw;
        }

        return CommandResult<AttachmentDownload>.Success(new AttachmentDownload(content, file.FileName, file.MimeType, file.SizeBytes));
    }

    private static CommandError Invalid(string message) => CommandError.Validation(AttachmentFileRules.FileField, message);

    private DateTime UtcNow()
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        return now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMillisecond));
    }
}
