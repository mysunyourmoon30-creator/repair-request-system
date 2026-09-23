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
/// EF Core implementation of the "My Visits" read and Check-in ports (S3-001). Every read starts from
/// <see cref="IDataScope.AssignedServiceVisits"/>, so tenant/site/assignment scope is part of the same SQL
/// statement — same convention <c>WorkOrderStore</c> and <c>ServiceVisitStore</c> use.
/// </summary>
internal sealed class WorkSessionStore : IWorkSessionStore
{
    private const int UniqueIndexViolation = 2601;
    private const int UniqueConstraintViolation = 2627;
    private const int DeadlockVictim = 1205;

    private readonly RepairRequestDbContext _db;
    private readonly IDataScope _scope;

    public WorkSessionStore(RepairRequestDbContext db, IDataScope scope)
    {
        _db = db;
        _scope = scope;
    }

    public async Task<PagedResult<MyVisitSummaryDto>> ListMineAsync(CurrentUser user, MyVisitsQuery query, CancellationToken cancellationToken)
    {
        // Only SCHEDULED visits are actionable from "My Visits" (S3-001 plan: no client-side re-filtering needed).
        var source = _scope.AssignedServiceVisits(user).AsNoTracking()
            .Where(visit => visit.Status == ServiceVisitStatus.Scheduled);

        var paging = query.Paging;
        var totalCount = await source.CountAsync(cancellationToken);
        var skip = (long)(paging.Page - 1) * paging.PageSize;

        IReadOnlyList<MyVisitSummaryDto> items = skip >= totalCount
            ? []
            : await Project(source
                    .OrderBy(visit => visit.ScheduledStartAt)
                    .ThenBy(visit => visit.Id)
                    .Skip((int)skip)
                    .Take(paging.PageSize))
                .ToListAsync(cancellationToken);

        return new PagedResult<MyVisitSummaryDto>(items, paging.Page, paging.PageSize, totalCount);
    }

    private IQueryable<MyVisitSummaryDto> Project(IQueryable<ServiceVisit> visits) =>
        from visit in visits
        join workOrder in _db.WorkOrders on visit.WorkOrderId equals workOrder.Id
        join request in _db.RepairRequests on workOrder.RepairRequestId equals request.Id
        select new MyVisitSummaryDto(
            visit.Id,
            workOrder.Id,
            workOrder.WorkOrderNo,
            visit.Status,
            _db.Sites.Where(site => site.Id == request.SiteId).Select(site => site.SiteCode).FirstOrDefault(),
            _db.Equipment.Where(item => item.Id == request.EquipmentId).Select(item => item.EquipmentCode).FirstOrDefault(),
            visit.ScheduledStartAt,
            visit.ScheduledEndAt,
            visit.RowVersion);

    public async Task<CommandResult<T>> RunInTransactionAsync<T>(Func<Task<CommandResult<T>>> command, CancellationToken cancellationToken)
    {
        try
        {
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

    public async Task<ServiceVisitForCheckIn?> LoadForCheckInAsync(CurrentUser user, Guid serviceVisitId, CancellationToken cancellationToken)
    {
        var scopedVisits = _scope.AssignedServiceVisits(user);

        var row = await (
            from visit in scopedVisits
            join workOrder in _db.WorkOrders on visit.WorkOrderId equals workOrder.Id
            where visit.Id == serviceVisitId
            select new { Visit = visit, WorkOrder = workOrder })
            .SingleOrDefaultAsync(cancellationToken);

        return row is null ? null : new ServiceVisitForCheckIn(row.Visit, row.WorkOrder);
    }

    public Task<bool> HasActiveSessionAsync(Guid tenantId, Guid technicianId, CancellationToken cancellationToken) =>
        _db.WorkSessions.AnyAsync(session =>
            session.TenantId == tenantId
            && session.TechnicianId == technicianId
            && session.Status != WorkSessionStatus.CheckedOut,
            cancellationToken);

    public void Add(WorkSession session) => _db.WorkSessions.Add(session);

    public void AddAudit(AuditHistory audit) => _db.AuditHistory.Add(audit);

    public async Task<WorkOrderSaveOutcome> SaveChangesAsync(ServiceVisit visit, byte[] expectedRowVersion, CancellationToken cancellationToken)
    {
        // The UPDATE applies only WHERE row_version = the client's If-Match token (the Visit's own). The parent
        // Work Order was loaded fresh in this same transaction (no client token for it), so EF's ordinary
        // optimistic-concurrency check on its own RowVersion is sufficient without an explicit override.
        _db.Entry(visit).Property(item => item.RowVersion).OriginalValue = expectedRowVersion;

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
        catch (DbUpdateException exception) when (SqlErrorNumber(exception) is UniqueIndexViolation or UniqueConstraintViolation or DeadlockVictim)
        {
            // Covers the true-concurrent-overlap race: two simultaneous Check-ins for the same technician on two
            // different Visits can both pass the app-level HasActiveSessionAsync check before either commits;
            // the DB-level unique filtered index (IX_work_session_tenant_id_technician_id_active) is the backstop.
            _db.ChangeTracker.Clear();
            return WorkOrderSaveOutcome.ConcurrencyConflict;
        }
    }

    public Task<WorkSession?> LoadOwnForUpdateAsync(CurrentUser user, Guid workSessionId, CancellationToken cancellationToken) =>
        _scope.OwnWorkSessions(user).SingleOrDefaultAsync(session => session.Id == workSessionId, cancellationToken);

    public void AddPause(WorkSessionPause pause) => _db.WorkSessionPauses.Add(pause);

    public Task<WorkSessionPause?> LoadOpenPauseAsync(Guid workSessionId, CancellationToken cancellationToken) =>
        _db.WorkSessionPauses.SingleOrDefaultAsync(pause => pause.WorkSessionId == workSessionId && pause.ResumedAt == null, cancellationToken);

    public async Task<WorkOrderSaveOutcome> SaveChangesAsync(WorkSession session, byte[] expectedRowVersion, CancellationToken cancellationToken)
    {
        // The UPDATE applies only WHERE row_version = the client's If-Match token (the session's own).
        _db.Entry(session).Property(item => item.RowVersion).OriginalValue = expectedRowVersion;

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
        catch (DbUpdateException exception) when (SqlErrorNumber(exception) is UniqueIndexViolation or UniqueConstraintViolation or DeadlockVictim)
        {
            // Backstop: IX_work_session_pause_open allows one open pause per session, so a second concurrent Pause
            // that slipped past the row-version check still cannot create a second open period.
            _db.ChangeTracker.Clear();
            return WorkOrderSaveOutcome.ConcurrencyConflict;
        }
    }

    public async Task<WorkSessionForCheckOut?> LoadOwnForCheckOutAsync(CurrentUser user, Guid workSessionId, CancellationToken cancellationToken)
    {
        var row = await (
            from session in _scope.OwnWorkSessions(user)
            join visit in _db.ServiceVisits on session.ServiceVisitId equals visit.Id
            where session.Id == workSessionId
            select new { Session = session, Visit = visit })
            .SingleOrDefaultAsync(cancellationToken);

        return row is null ? null : new WorkSessionForCheckOut(row.Session, row.Visit);
    }

    public async Task<WorkOrderSaveOutcome> SaveChangesAsync(WorkSession session, ServiceVisit visit, byte[] expectedRowVersion, CancellationToken cancellationToken)
    {
        // The UPDATE applies only WHERE row_version = the client's If-Match token (the session's own). The Visit
        // was loaded fresh in this same transaction (no client token for it), so EF's ordinary optimistic-
        // concurrency check on its own RowVersion is sufficient without an explicit override — same treatment
        // Check-in gives the parent Work Order.
        _db.Entry(session).Property(item => item.RowVersion).OriginalValue = expectedRowVersion;

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

    public Task<WorkSessionDto?> GetOwnSessionAsync(CurrentUser user, Guid workSessionId, CancellationToken cancellationToken) =>
        ProjectSession(_scope.OwnWorkSessions(user).AsNoTracking().Where(session => session.Id == workSessionId))
            .SingleOrDefaultAsync(cancellationToken);

    public Task<WorkSessionDto?> GetCurrentAsync(CurrentUser user, CancellationToken cancellationToken) =>
        ProjectSession(_scope.OwnWorkSessions(user).AsNoTracking().Where(session => session.Status != WorkSessionStatus.CheckedOut))
            .SingleOrDefaultAsync(cancellationToken);

    private IQueryable<WorkSessionDto> ProjectSession(IQueryable<WorkSession> sessions) =>
        from session in sessions
        join visit in _db.ServiceVisits on session.ServiceVisitId equals visit.Id
        join workOrder in _db.WorkOrders on visit.WorkOrderId equals workOrder.Id
        join request in _db.RepairRequests on workOrder.RepairRequestId equals request.Id
        select new WorkSessionDto(
            session.Id,
            visit.Id,
            workOrder.Id,
            workOrder.WorkOrderNo,
            _db.Sites.Where(site => site.Id == request.SiteId).Select(site => site.SiteCode).FirstOrDefault(),
            _db.Equipment.Where(item => item.Id == request.EquipmentId).Select(item => item.EquipmentCode).FirstOrDefault(),
            session.Status,
            session.CheckInAt,
            session.PauseStartAt,
            session.ResumeAt,
            session.CheckOutAt,
            _db.WorkSessionPauses
                .Where(pause => pause.WorkSessionId == session.Id)
                .OrderByDescending(pause => pause.PausedAt)
                .ThenByDescending(pause => pause.Id)
                .Select(pause => new WorkSessionPauseDto(pause.Id, pause.PausedAt, pause.PauseReason, pause.ResumedAt))
                .ToList(),
            session.RowVersion);

    private static int? SqlErrorNumber(Exception exception) =>
        exception switch
        {
            SqlException sql => sql.Number,
            { InnerException: SqlException inner } => inner.Number,
            _ => null
        };
}
