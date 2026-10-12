using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using RepairRequest.Application.Common;
using RepairRequest.Application.Security;
using RepairRequest.Application.WorkEvidence;
using RepairRequest.Domain.Auditing;
using RepairRequest.Domain.Files;
using RepairRequest.Domain.WorkEvidence;
using RepairRequest.Infrastructure.Persistence;

namespace RepairRequest.Infrastructure.WorkEvidence;

/// <summary>
/// EF Core implementation of the Work Order evidence port (`docs/13` §4.28). It shares the scoped <c>DbContext</c> (and therefore the
/// transaction) of the Work Summary Submit command. Caller-facing queries derive the audience from existing scope rules first; each read is
/// one bounded query over the <c>IX_work_evidence_upload_work_order_visit</c> index and the file/binding primary keys, with no per-row query.
/// </summary>
internal sealed class WorkEvidenceStore : IWorkEvidenceStore
{
    private const int DeadlockVictim = 1205;

    private readonly RepairRequestDbContext _db;
    private readonly IDataScope _scope;

    public WorkEvidenceStore(RepairRequestDbContext db, IDataScope scope)
    {
        _db = db;
        _scope = scope;
    }

    public async Task<CommandResult<T>> RunInTransactionAsync<T>(Func<Task<CommandResult<T>>> command, CancellationToken cancellationToken)
    {
        try
        {
            // Disposing without commit rolls back and releases the transaction-owned evidence lock.
            await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

            var result = await command();
            if (result.Succeeded)
            {
                await transaction.CommitAsync(cancellationToken);
            }
            else
            {
                _db.ChangeTracker.Clear();
            }

            return result;
        }
        catch (Exception exception) when (SqlErrorNumber(exception) is DeadlockVictim)
        {
            _db.ChangeTracker.Clear();
            return CommandError.ConcurrencyConflict;
        }
    }

    public async Task<WorkEvidenceUploadTarget?> FindUploadTargetAsync(CurrentUser user, Guid workOrderId, CancellationToken cancellationToken)
    {
        if (!user.IsTechnician)
        {
            return null;
        }

        var tenantId = user.TenantId;
        var workOrder = await _db.WorkOrders.AsNoTracking()
            .Where(item => item.TenantId == tenantId && item.Id == workOrderId)
            .Select(item => new { item.Id, item.TenantId, item.Status })
            .SingleOrDefaultAsync(cancellationToken);
        if (workOrder is null)
        {
            return null;
        }

        // The Visit Work Summary Submit would select: the Visit of the caller's own latest session (by check-in) on this Work Order.
        var visitId = await (
            from session in _scope.OwnWorkSessions(user)
            join visit in _db.ServiceVisits on session.ServiceVisitId equals visit.Id
            where visit.WorkOrderId == workOrderId
            orderby session.CheckInAt descending
            select (Guid?)visit.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (visitId is null)
        {
            return null;
        }

        var hasSummary = await _db.WorkSummaries.AsNoTracking().AnyAsync(summary => summary.ServiceVisitId == visitId, cancellationToken);
        return new WorkEvidenceUploadTarget(workOrder.TenantId, workOrder.Id, workOrder.Status, visitId.Value, hasSummary);
    }

    public Task<bool> TryLockWorkOrderAsync(Guid workOrderId, CancellationToken cancellationToken) =>
        WorkEvidenceLock.TryAcquireAsync(_db, workOrderId, cancellationToken);

    public async Task<IReadOnlyList<WorkEvidenceSelectionRow>> LoadSelectionAsync(
        Guid tenantId, Guid workOrderId, Guid serviceVisitId, IReadOnlyCollection<Guid> evidenceIds, CancellationToken cancellationToken)
    {
        // The ids are untrusted: the tenant, Work Order and Visit come from the already loaded, scope-checked Work Order and Visit, so an id
        // that lives anywhere else is simply not returned. One query over the primary key, at most ten ids.
        var ids = evidenceIds.ToList();
        var rows = await (
            from upload in _db.WorkEvidenceUploads
            join file in _db.FileAssets on upload.FileAssetId equals file.Id
            where upload.TenantId == tenantId
                  && upload.WorkOrderId == workOrderId
                  && upload.ServiceVisitId == serviceVisitId
                  && ids.Contains(upload.Id)
                  && file.TenantId == upload.TenantId
            select new
            {
                Upload = upload,
                file.MalwareScanStatus,
                Bound = _db.WorkSummaryEvidence.Any(binding => binding.Id == upload.Id)
            })
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return rows.Select(row => new WorkEvidenceSelectionRow(row.Upload, row.MalwareScanStatus, row.Bound)).ToList();
    }

    public void AddFileAsset(FileAsset fileAsset) => _db.FileAssets.Add(fileAsset);

    public void AddUpload(WorkEvidenceUpload upload) => _db.WorkEvidenceUploads.Add(upload);

    public void AddBinding(WorkSummaryEvidence binding) => _db.WorkSummaryEvidence.Add(binding);

    public void AddAudit(AuditHistory audit) => _db.AuditHistory.Add(audit);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => _db.SaveChangesAsync(cancellationToken);

    public Task<ReportAudience> ResolveAudienceAsync(CurrentUser user, Guid workOrderId, CancellationToken cancellationToken) =>
        ReportAudienceResolver.ResolveAsync(_db, _scope, user, workOrderId, cancellationToken);

    public async Task<(ReportAudience Audience, IReadOnlyList<WorkEvidenceItemDto> Items)?> ListAsync(
        CurrentUser user, Guid workOrderId, CancellationToken cancellationToken)
    {
        var audience = await ResolveAudienceAsync(user, workOrderId, cancellationToken);
        if (audience == ReportAudience.None)
        {
            return null;
        }

        var items = await (
            from upload in VisibleUploads(audience, user, workOrderId)
            join file in _db.FileAssets on upload.FileAssetId equals file.Id
            join binding in _db.WorkSummaryEvidence on upload.Id equals binding.Id into bindings
            from binding in bindings.DefaultIfEmpty()
            where file.TenantId == upload.TenantId
            orderby upload.UploadSequence
            select new WorkEvidenceItemDto(
                upload.Id,
                upload.ServiceVisitId,
                upload.EvidenceType,
                file.FileName,
                file.MimeType,
                file.SizeBytes,
                file.MalwareScanStatus,
                binding != null,
                binding != null ? binding.WorkSummaryId : null,
                binding != null ? binding.SlotNo : null,
                file.UploadedAt))
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return (audience, items);
    }

    public async Task<WorkEvidenceDownloadInfo?> FindDownloadAsync(CurrentUser user, Guid workOrderId, Guid evidenceId, CancellationToken cancellationToken)
    {
        var audience = await ResolveAudienceAsync(user, workOrderId, cancellationToken);
        if (audience == ReportAudience.None)
        {
            return null;
        }

        return await (
            from upload in VisibleUploads(audience, user, workOrderId)
            join file in _db.FileAssets on upload.FileAssetId equals file.Id
            where upload.Id == evidenceId && file.TenantId == upload.TenantId
            select new WorkEvidenceDownloadInfo(
                upload.TenantId,
                upload.WorkOrderId,
                upload.Id,
                audience,
                _db.WorkSummaryEvidence.Any(binding => binding.Id == upload.Id),
                file.FileName,
                file.MimeType,
                file.SizeBytes,
                file.StorageReference,
                file.MalwareScanStatus))
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>Uploads of one Work Order visible to the audience; the tenant filter is part of every query.</summary>
    private IQueryable<WorkEvidenceUpload> VisibleUploads(ReportAudience audience, CurrentUser user, Guid workOrderId)
    {
        var tenantId = user.TenantId;
        var uploads = _db.WorkEvidenceUploads.Where(upload => upload.WorkOrderId == workOrderId && upload.TenantId == tenantId);

        switch (audience)
        {
            case ReportAudience.Technician:
                // Own Visits only: prepared (not yet bound) and bound items.
                var ownVisitIds = _scope.AssignedServiceVisits(user).Select(visit => visit.Id);
                return uploads.Where(upload => ownVisitIds.Contains(upload.ServiceVisitId));

            case ReportAudience.AcceptanceContact:
                // The customer's contact sees only what a submitted Work Summary froze, and only while the file is CLEAN.
                return uploads.Where(upload =>
                    _db.WorkSummaryEvidence.Any(binding => binding.Id == upload.Id)
                    && _db.FileAssets.Any(file => file.Id == upload.FileAssetId && file.TenantId == upload.TenantId && file.MalwareScanStatus == MalwareScanStatus.Clean));

            default:
                return uploads;
        }
    }

    private static int? SqlErrorNumber(Exception exception) =>
        exception switch
        {
            SqlException sql => sql.Number,
            { InnerException: SqlException inner } => inner.Number,
            _ => null
        };
}
