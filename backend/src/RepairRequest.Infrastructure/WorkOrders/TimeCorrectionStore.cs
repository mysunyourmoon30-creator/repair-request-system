using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using RepairRequest.Application.Common;
using RepairRequest.Application.Security;
using RepairRequest.Application.WorkOrders;
using RepairRequest.Domain.Auditing;
using RepairRequest.Domain.WorkOrders;
using RepairRequest.Infrastructure.Persistence;

namespace RepairRequest.Infrastructure.WorkOrders;

/// <summary>
/// EF Core implementation of the Time Correction port (`docs/17`). Reads start from the Time Correction scopes of
/// <see cref="IDataScope"/>, so tenant, Site and role scope are part of the same SQL statement.
/// <para>
/// Locking. A command that can change or rely on time facts first takes an <c>UPDLOCK, HOLDLOCK</c> on the Work Order row and
/// reads its status under that lock. Close and Cancel both finish by UPDATE-ing that row, so the two orders are decided by the
/// database: an Approve holding the lock makes Close/Cancel wait and then succeed (the correction committed first); a Close or
/// Cancel that already holds the row makes the Approve wait and then read CLOSED/CANCELLED and refuse. Approve then takes the same
/// lock on the Work Session row, its pause rows and its Visit row — always in this order, so Approve, Pause, Resume and
/// Check-out serialize on the session. Every lock lives in the command's transaction, so it is held until the apply, the audit rows
/// and the decision commit (or roll back) together.
/// </para>
/// </summary>
internal sealed class TimeCorrectionStore : ITimeCorrectionStore
{
    private const int DeadlockVictim = 1205;
    private const int UniqueIndexViolation = 2601;
    private const int UniqueConstraintViolation = 2627;
    private const string PendingIndexPrefix = "UQ_time_correction_pending_";

    private readonly RepairRequestDbContext _db;
    private readonly IDataScope _scope;

    public TimeCorrectionStore(RepairRequestDbContext db, IDataScope scope)
    {
        _db = db;
        _scope = scope;
    }

    public async Task<CommandResult<T>> RunInTransactionAsync<T>(Func<Task<CommandResult<T>>> command, CancellationToken cancellationToken)
    {
        try
        {
            // Disposing without commit rolls back and releases every lock taken inside.
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

    public async Task<TimeCorrectionRequestContext?> LoadForRequestAsync(CurrentUser user, Guid workSessionId, CancellationToken cancellationToken)
    {
        var visible = await _scope.WorkSessionsForTimeCorrectionRequest(user).AsNoTracking()
            .Where(session => session.Id == workSessionId)
            .Select(session => (Guid?)session.ServiceVisitId)
            .SingleOrDefaultAsync(cancellationToken);
        if (visible is null)
        {
            return null;
        }

        var workOrderId = await _db.ServiceVisits.AsNoTracking()
            .Where(visit => visit.Id == visible.Value)
            .Select(visit => visit.WorkOrderId)
            .SingleAsync(cancellationToken);
        var status = await LockWorkOrderAsync(workOrderId, cancellationToken);

        // Read after the lock, so the Work Order status and the session facts the Request validates against are current.
        var session = await _db.WorkSessions.AsNoTracking().SingleAsync(item => item.Id == workSessionId, cancellationToken);
        var pauses = await _db.WorkSessionPauses.AsNoTracking()
            .Where(pause => pause.WorkSessionId == workSessionId)
            .OrderBy(pause => pause.PausedAt).ThenBy(pause => pause.Id)
            .ToListAsync(cancellationToken);

        return new TimeCorrectionRequestContext(session, pauses, status);
    }

    public void Add(TimeCorrection correction) => _db.TimeCorrections.Add(correction);

    public void AddAudit(AuditHistory audit) => _db.AuditHistory.Add(audit);

    public async Task<TimeCorrectionInsertOutcome> SaveNewAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return TimeCorrectionInsertOutcome.Saved;
        }
        catch (DbUpdateException exception) when (IsPendingDuplicate(exception))
        {
            // The caller ends the command as a conflict; the rolled back transaction discards the staged correction and audit row.
            return TimeCorrectionInsertOutcome.DuplicatePending;
        }
    }

    public Task<TimeCorrection?> LoadForRejectAsync(CurrentUser user, Guid timeCorrectionId, CancellationToken cancellationToken) =>
        _scope.TimeCorrectionsForDecision(user).SingleOrDefaultAsync(correction => correction.Id == timeCorrectionId, cancellationToken);

    public async Task<TimeCorrectionApprovalContext?> LoadForApproveAsync(CurrentUser user, Guid timeCorrectionId, CancellationToken cancellationToken)
    {
        var correction = await _scope.TimeCorrectionsForDecision(user).SingleOrDefaultAsync(item => item.Id == timeCorrectionId, cancellationToken);
        if (correction is null)
        {
            return null;
        }

        var keys = await (
            from sessionRow in _db.WorkSessions.AsNoTracking()
            join visitRow in _db.ServiceVisits.AsNoTracking() on sessionRow.ServiceVisitId equals visitRow.Id
            where sessionRow.Id == correction.WorkSessionId
            select new { VisitId = visitRow.Id, visitRow.WorkOrderId })
            .SingleAsync(cancellationToken);

        // Lock order: Work Order, Work Session, its pause rows, its Visit. Everything is read after its lock is held.
        var status = await LockWorkOrderAsync(keys.WorkOrderId, cancellationToken);

        // The correction was read before the locks: re-read it so a decision committed meanwhile is seen (row version, status).
        await _db.Entry(correction).ReloadAsync(cancellationToken);

        var workSessionId = correction.WorkSessionId;
        var session = await _db.WorkSessions
            .FromSqlInterpolated($"SELECT * FROM [work_session] WITH (UPDLOCK, HOLDLOCK) WHERE [work_session_id] = {workSessionId}")
            .SingleAsync(cancellationToken);
        var pauses = await _db.WorkSessionPauses
            .FromSqlInterpolated($"SELECT * FROM [work_session_pause] WITH (UPDLOCK, HOLDLOCK) WHERE [work_session_id] = {workSessionId}")
            .ToListAsync(cancellationToken);
        var visitId = keys.VisitId;
        var visit = await _db.ServiceVisits
            .FromSqlInterpolated($"SELECT * FROM [service_visit] WITH (UPDLOCK, HOLDLOCK) WHERE [service_visit_id] = {visitId}")
            .SingleAsync(cancellationToken);
        var sessionCount = await _db.WorkSessions.AsNoTracking().CountAsync(item => item.ServiceVisitId == visitId, cancellationToken);

        return new TimeCorrectionApprovalContext(correction, session, pauses.OrderBy(pause => pause.PausedAt).ThenBy(pause => pause.Id).ToList(), visit, sessionCount, status);
    }

    public Task<WorkOrderSaveOutcome> SaveRejectionAsync(TimeCorrection correction, byte[] expectedRowVersion, CancellationToken cancellationToken)
    {
        _db.Entry(correction).Property(item => item.RowVersion).OriginalValue = expectedRowVersion;
        return SaveAsync(cancellationToken);
    }

    public Task<WorkOrderSaveOutcome> SaveApprovalAsync(
        TimeCorrection correction, WorkSession session, byte[] expectedRowVersion, CancellationToken cancellationToken)
    {
        _db.Entry(correction).Property(item => item.RowVersion).OriginalValue = expectedRowVersion;

        // Always write the session row — even when only a pause row changed — so its row version moves: a Pause, Resume or Check-out
        // holding the earlier token then conflicts (409) instead of silently overwriting facts that were just corrected.
        _db.Entry(session).Property(item => item.CheckInAt).IsModified = true;

        return SaveAsync(cancellationToken);
    }

    public Task<TimeCorrectionDto?> GetAsync(CurrentUser user, Guid timeCorrectionId, CancellationToken cancellationToken) =>
        Project(_scope.TimeCorrections(user).AsNoTracking().Where(correction => correction.Id == timeCorrectionId))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<PagedResult<TimeCorrectionDto>> ListPendingAsync(CurrentUser user, PendingTimeCorrectionsQuery query, CancellationToken cancellationToken)
    {
        var source = _scope.TimeCorrectionsForDecision(user).AsNoTracking()
            .Where(correction => correction.Status == TimeCorrectionStatus.Pending);

        var paging = query.Paging;
        var totalCount = await source.CountAsync(cancellationToken);
        var skip = (long)(paging.Page - 1) * paging.PageSize;

        IReadOnlyList<TimeCorrectionDto> items = skip >= totalCount
            ? []
            : await Project(source
                    .OrderBy(correction => correction.Id)
                    .Skip((int)skip)
                    .Take(paging.PageSize))
                .ToListAsync(cancellationToken);

        return new PagedResult<TimeCorrectionDto>(items, paging.Page, paging.PageSize, totalCount);
    }

    private async Task<WorkOrderStatus> LockWorkOrderAsync(Guid workOrderId, CancellationToken cancellationToken) =>
        await _db.WorkOrders
            .FromSqlInterpolated($"SELECT * FROM [work_order] WITH (UPDLOCK, HOLDLOCK) WHERE [work_order_id] = {workOrderId}")
            .AsNoTracking()
            .Select(workOrder => workOrder.Status)
            .SingleAsync(cancellationToken);

    private async Task<WorkOrderSaveOutcome> SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return WorkOrderSaveOutcome.Saved;
        }
        catch (DbUpdateConcurrencyException)
        {
            _db.ChangeTracker.Clear();
            return WorkOrderSaveOutcome.ConcurrencyConflict;
        }
    }

    private IQueryable<TimeCorrectionDto> Project(IQueryable<TimeCorrection> corrections) =>
        from correction in corrections
        join session in _db.WorkSessions on correction.WorkSessionId equals session.Id
        join visit in _db.ServiceVisits on session.ServiceVisitId equals visit.Id
        join workOrder in _db.WorkOrders on visit.WorkOrderId equals workOrder.Id
        join request in _db.RepairRequests on workOrder.RepairRequestId equals request.Id
        select new TimeCorrectionDto(
            correction.Id,
            correction.WorkSessionId,
            correction.WorkSessionPauseId,
            visit.Id,
            workOrder.Id,
            workOrder.WorkOrderNo,
            _db.Sites.Where(site => site.Id == request.SiteId).Select(site => site.SiteCode).FirstOrDefault(),
            _db.Equipment.Where(item => item.Id == request.EquipmentId).Select(item => item.EquipmentCode).FirstOrDefault(),
            correction.TimestampType,
            correction.OldValue,
            correction.NewValue,
            correction.Reason,
            correction.RequestedBy,
            correction.Status,
            correction.DecidedBy,
            correction.DecisionReason,
            correction.DecidedAt,
            correction.RowVersion);

    private static bool IsPendingDuplicate(DbUpdateException exception) =>
        exception.InnerException is SqlException { Number: UniqueIndexViolation or UniqueConstraintViolation } sql
        && sql.Message.Contains(PendingIndexPrefix, StringComparison.Ordinal);

    private static int? SqlErrorNumber(Exception exception) =>
        exception switch
        {
            SqlException sql => sql.Number,
            { InnerException: SqlException inner } => inner.Number,
            _ => null
        };
}
