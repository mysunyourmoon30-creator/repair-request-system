using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using RepairRequest.Application.Attachments;
using RepairRequest.Application.RepairRequests;
using RepairRequest.Domain.Auditing;
using RepairRequest.Domain.Files;
using RepairRequest.Domain.RepairRequests;
using RepairRequest.Infrastructure.Persistence;
using RepairRequestAggregate = RepairRequest.Domain.RepairRequests.RepairRequest;

namespace RepairRequest.Infrastructure.RepairRequests;

/// <summary>
/// EF Core implementation of the Submit port.
/// <list type="bullet">
/// <item>CLEAN photo: one EXISTS over request_attachment (repair_request_id index) joined to file_asset (primary key).</item>
/// <item>Duplicates: one COUNT range seek on IX_repair_request_duplicate_check; rows are never loaded.</item>
/// <item>Submit: one READ COMMITTED transaction that first takes the transaction-owned application lock of the duplicate key
/// (<see cref="RepairRequestDuplicateLock"/>) and counts duplicates inside it, so concurrent Submits of different matching
/// Drafts serialize and the later one always sees the committed earlier one. Only then one MERGE WITH (HOLDLOCK) on the
/// request_no_counter primary key allocates the Request No: a rolled-back Submit consumes no number and no MAX + 1 scan ever
/// runs. Lock order is always duplicate-key lock, then counter row, then repair_request row.</item>
/// </list>
/// </summary>
internal sealed class RepairRequestSubmitStore : IRepairRequestSubmitStore
{
    private const int DeadlockVictim = 1205;

    private readonly RepairRequestDbContext _db;
    private readonly RepairRequestDuplicateLockOptions _lockOptions;

    public RepairRequestSubmitStore(RepairRequestDbContext db, RepairRequestDuplicateLockOptions lockOptions)
    {
        _db = db;
        _lockOptions = lockOptions;
    }

    public Task<bool> HasCleanPhotoAsync(Guid tenantId, Guid repairRequestId, CancellationToken cancellationToken) =>
        (from attachment in _db.RepairRequestAttachments
         join file in _db.FileAssets on attachment.FileAssetId equals file.Id
         where attachment.RepairRequestId == repairRequestId
               && file.TenantId == tenantId
               && file.MalwareScanStatus == MalwareScanStatus.Clean
               && (file.MimeType == AttachmentFileRules.JpegMimeType || file.MimeType == AttachmentFileRules.PngMimeType)
         select attachment.Id)
        .AnyAsync(cancellationToken);

    public Task<int> CountDuplicatesAsync(DuplicateQuery query, CancellationToken cancellationToken)
    {
        var tenantId = query.TenantId;
        var siteId = query.SiteId;
        var categoryCode = query.RequestCategoryCode;
        var requestId = query.RepairRequestId;
        var windowStart = (DateTime?)query.WindowStart;

        // D-12 active set. CONVERTED counts unconditionally until Work Orders exist (RR-DEC-001 NB-2).
        var candidates = _db.RepairRequests.Where(request =>
            request.TenantId == tenantId
            && request.SiteId == siteId
            && request.RequestCategoryCode == categoryCode
            && request.SubmittedAt >= windowStart
            && request.LocationId == null
            && request.Id != requestId
            && (request.Status == RepairRequestStatus.Submitted
                || request.Status == RepairRequestStatus.UnderReview
                || request.Status == RepairRequestStatus.Approved
                || request.Status == RepairRequestStatus.Converted));

        // Branch in C#: an empty Equipment matches only requests without Equipment; null is never a wildcard.
        if (query.EquipmentId is { } equipmentId)
        {
            candidates = candidates.Where(request => request.EquipmentId == equipmentId);
        }
        else
        {
            candidates = candidates.Where(request => request.EquipmentId == null);
        }

        return candidates.CountAsync(cancellationToken);
    }

    public async Task<RepairRequestSubmitResult> SubmitAsync(
        RepairRequestAggregate request,
        byte[] expectedRowVersion,
        DuplicateQuery duplicateQuery,
        int requestYear,
        bool hasContinuationReason,
        Func<int, int, AuditHistory> applySubmit,
        CancellationToken cancellationToken)
    {
        try
        {
            // Disposing without commit rolls back the sequence increment, the transition and the audit together and
            // releases the duplicate-key lock.
            await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

            if (!await TryLockDuplicateKeyAsync(duplicateQuery, cancellationToken))
            {
                return new RepairRequestSubmitResult(RepairRequestSubmitStatus.ConcurrencyConflict, 0);
            }

            // Counted while holding the key: a competing matching Submit has either committed (and is counted) or has not
            // started its critical section yet (and will count this one).
            var duplicateCount = await CountDuplicatesAsync(duplicateQuery, cancellationToken);
            if (duplicateCount > 0 && !hasContinuationReason)
            {
                return new RepairRequestSubmitResult(RepairRequestSubmitStatus.DuplicateWarning, duplicateCount);
            }

            var sequence = await AllocateSequenceAsync(request.TenantId, requestYear, cancellationToken);
            _db.AuditHistory.Add(applySubmit(sequence, duplicateCount));

            // The UPDATE applies only WHERE row_version = the client's If-Match token.
            _db.Entry(request).Property(item => item.RowVersion).OriginalValue = expectedRowVersion;
            await _db.SaveChangesAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return new RepairRequestSubmitResult(RepairRequestSubmitStatus.Saved, duplicateCount);
        }
        catch (DbUpdateConcurrencyException)
        {
            _db.ChangeTracker.Clear();
            return new RepairRequestSubmitResult(RepairRequestSubmitStatus.ConcurrencyConflict, 0);
        }
        catch (Exception exception) when (SqlErrorNumber(exception) is DeadlockVictim)
        {
            _db.ChangeTracker.Clear();
            return new RepairRequestSubmitResult(RepairRequestSubmitStatus.ConcurrencyConflict, 0);
        }
        catch
        {
            _db.ChangeTracker.Clear();
            throw;
        }
    }

    /// <summary>
    /// Exclusive, transaction-owned application lock on the duplicate key. Returns false when it is not granted within the
    /// configured timeout or the request is chosen as a deadlock victim (sp_getapplock result &lt; 0); the caller then
    /// returns a deterministic 409 without retrying.
    /// </summary>
    private async Task<bool> TryLockDuplicateKeyAsync(DuplicateQuery query, CancellationToken cancellationToken)
    {
        var resource = RepairRequestDuplicateLock.Resource(query);
        var timeout = _lockOptions.TimeoutMilliseconds;

        var results = await _db.Database.SqlQuery<int>($"""
            DECLARE @lock_result int;
            EXEC @lock_result = sp_getapplock @Resource = {resource}, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = {timeout};
            SELECT @lock_result AS [Value];
            """).ToListAsync(cancellationToken);

        return results.Single() >= 0;
    }

    private async Task<int> AllocateSequenceAsync(Guid tenantId, int requestYear, CancellationToken cancellationToken)
    {
        var year = checked((short)requestYear);

        var values = await _db.Database.SqlQuery<int>($"""
            MERGE [request_no_counter] WITH (HOLDLOCK) AS [target]
            USING (VALUES ({tenantId}, {year})) AS [source] ([tenant_id], [request_year])
                ON [target].[tenant_id] = [source].[tenant_id] AND [target].[request_year] = [source].[request_year]
            WHEN MATCHED THEN
                UPDATE SET [last_value] = [target].[last_value] + 1
            WHEN NOT MATCHED THEN
                INSERT ([tenant_id], [request_year], [last_value]) VALUES ([source].[tenant_id], [source].[request_year], 1)
            OUTPUT [inserted].[last_value] AS [Value];
            """).ToListAsync(cancellationToken);

        return values.Single();
    }

    private static int? SqlErrorNumber(Exception exception) =>
        exception switch
        {
            SqlException sql => sql.Number,
            { InnerException: SqlException inner } => inner.Number,
            _ => null
        };
}
