using Microsoft.EntityFrameworkCore;
using RepairRequest.Application.Attachments;
using RepairRequest.Application.Common;
using RepairRequest.Application.Security;
using RepairRequest.Domain.Auditing;
using RepairRequest.Domain.Files;
using RepairRequest.Domain.RepairRequests;
using RepairRequest.Infrastructure.Persistence;

namespace RepairRequest.Infrastructure.Attachments;

/// <summary>
/// EF Core implementation of the attachment port. Every caller-facing query starts from the Repair Request business scope
/// (<see cref="IDataScope.RepairRequests"/>), so tenant/site/ownership scope is part of the same SQL statement; each
/// lookup is one bounded query using the repair_request primary key and the request_attachment foreign-key indexes.
/// </summary>
internal sealed class AttachmentStore : IAttachmentStore
{
    private readonly RepairRequestDbContext _db;
    private readonly IDataScope _scope;

    public AttachmentStore(RepairRequestDbContext db, IDataScope scope)
    {
        _db = db;
        _scope = scope;
    }

    public Task<RepairRequestStatus?> GetOwnRequestStatusAsync(CurrentUser user, Guid repairRequestId, CancellationToken cancellationToken)
    {
        var userId = user.UserId;
        return _scope.RepairRequests(user)
            .Where(request => request.Id == repairRequestId && request.CreatedBy == userId)
            .Select(request => (RepairRequestStatus?)request.Status)
            .SingleOrDefaultAsync(cancellationToken);
    }

    public Task<AttachmentDownloadInfo?> FindDownloadAsync(CurrentUser user, Guid fileAssetId, CancellationToken cancellationToken) =>
        (from attachment in _db.RepairRequestAttachments
         join request in _scope.RepairRequests(user) on attachment.RepairRequestId equals request.Id
         join file in _db.FileAssets on attachment.FileAssetId equals file.Id
         where attachment.FileAssetId == fileAssetId && file.TenantId == request.TenantId
         select new AttachmentDownloadInfo(
             request.Id,
             file.TenantId,
             file.Id,
             file.FileName,
             file.MimeType,
             file.SizeBytes,
             file.StorageReference,
             file.MalwareScanStatus))
        .AsNoTracking()
        .FirstOrDefaultAsync(cancellationToken);

    // Three bounded commands regardless of the number of attachments: scope check, count, one page. Ordering by the
    // sequential attachment id is deterministic, follows upload order and is served by the request_attachment
    // (repair_request_id) index, whose key includes the clustered attachment id, so no sort over all rows is needed.
    public async Task<PagedResult<AttachmentDto>?> ListAsync(
        CurrentUser user,
        Guid repairRequestId,
        PageRequest paging,
        CancellationToken cancellationToken)
    {
        if (!await _scope.RepairRequests(user).AnyAsync(request => request.Id == repairRequestId, cancellationToken))
        {
            return null;
        }

        var tenantId = user.TenantId;
        var attachments =
            from attachment in _db.RepairRequestAttachments
            join file in _db.FileAssets on attachment.FileAssetId equals file.Id
            where attachment.RepairRequestId == repairRequestId && file.TenantId == tenantId
            select new { attachment, file };

        var totalCount = await attachments.CountAsync(cancellationToken);
        var skip = (long)(paging.Page - 1) * paging.PageSize;
        if (skip >= totalCount)
        {
            return new PagedResult<AttachmentDto>([], paging.Page, paging.PageSize, totalCount);
        }

        var items = await attachments
            .OrderBy(row => row.attachment.Id)
            .Skip((int)skip)
            .Take(paging.PageSize)
            .Select(row => new AttachmentDto(
                row.attachment.Id,
                row.file.Id,
                row.file.FileName,
                row.file.MimeType,
                row.file.SizeBytes,
                row.file.MalwareScanStatus,
                row.file.UploadedAt))
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return new PagedResult<AttachmentDto>(items, paging.Page, paging.PageSize, totalCount);
    }

    public void AddFileAsset(FileAsset fileAsset) => _db.FileAssets.Add(fileAsset);

    public void AddAttachment(RepairRequestAttachment attachment) => _db.RepairRequestAttachments.Add(attachment);

    public void AddAudit(AuditHistory audit) => _db.AuditHistory.Add(audit);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => _db.SaveChangesAsync(cancellationToken);

    public Task<FileAsset?> FindPendingFileAsync(Guid fileAssetId, CancellationToken cancellationToken) =>
        _db.FileAssets
            .AsNoTracking()
            .SingleOrDefaultAsync(file => file.Id == fileAssetId && file.MalwareScanStatus == MalwareScanStatus.Pending, cancellationToken);

    public async Task<bool> TryCompleteScanAsync(FileAsset scannedFile, AuditHistory audit, CancellationToken cancellationToken)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        var result = scannedFile.MalwareScanStatus;
        var updated = await _db.FileAssets
            .Where(file => file.Id == scannedFile.Id && file.MalwareScanStatus == MalwareScanStatus.Pending)
            .ExecuteUpdateAsync(setters => setters.SetProperty(file => file.MalwareScanStatus, result), cancellationToken);

        if (updated != 1)
        {
            return false;
        }

        _db.AuditHistory.Add(audit);
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
