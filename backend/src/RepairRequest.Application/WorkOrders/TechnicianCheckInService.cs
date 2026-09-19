using RepairRequest.Application.Common;
using RepairRequest.Application.Security;
using RepairRequest.Domain.WorkOrders;

namespace RepairRequest.Application.WorkOrders;

/// <summary>
/// "My Visits" (read) and Check-in (ST-WS-001; S3-001; UC-WO-016; BR-05) by the assigned Technician. Check-in
/// runs in one transaction: the Visit is within the caller's own assignment/site scope (404 otherwise, via
/// <see cref="IDataScope.AssignedServiceVisits"/> — "Primary team" plays no part, per Portfolio Project Owner
/// decision pre-S3-001: eligibility is <c>AssignedTechnicianId == caller</c> only); row version (409
/// CONCURRENCY_CONFLICT, checked against the Visit's own RowVersion); source state SCHEDULED (409
/// STATE_CONFLICT); no active overlap elsewhere (409 STATE_CONFLICT, BR-05). Success moves the Work Order and
/// the Visit to IN_PROGRESS and opens a Work Session — the client supplies neither status nor time; both are
/// server-derived. Location capture is out of scope for S3-001 (`docs/01` section 2 lists GPS route
/// optimization as explicit Out of Scope; confirmed with the Portfolio Project Owner pre-implementation).
/// </summary>
public sealed class TechnicianCheckInService
{
    private readonly IWorkSessionStore _store;
    private readonly TimeProvider _clock;

    public TechnicianCheckInService(IWorkSessionStore store, TimeProvider clock)
    {
        _store = store;
        _clock = clock;
    }

    public Task<PagedResult<MyVisitSummaryDto>> ListMineAsync(CurrentUser user, MyVisitsQuery query, CancellationToken cancellationToken) =>
        _store.ListMineAsync(user, query, cancellationToken);

    public Task<CommandResult<Guid>> CheckInAsync(
        CommandContext context, Guid serviceVisitId, byte[] expectedRowVersion, CancellationToken cancellationToken) =>
        _store.RunInTransactionAsync(() => CheckInLockedAsync(context, serviceVisitId, expectedRowVersion, cancellationToken), cancellationToken);

    private async Task<CommandResult<Guid>> CheckInLockedAsync(
        CommandContext context, Guid serviceVisitId, byte[] expectedRowVersion, CancellationToken cancellationToken)
    {
        var loaded = await _store.LoadForCheckInAsync(context.User, serviceVisitId, cancellationToken);
        if (loaded is null)
        {
            return CommandError.NotFound;
        }

        var (visit, workOrder) = loaded;

        if (!visit.RowVersion.AsSpan().SequenceEqual(expectedRowVersion))
        {
            return CommandError.ConcurrencyConflict;
        }

        if (!ServiceVisitStatusTransitions.IsAllowed(visit.Status, ServiceVisitStatus.InProgress))
        {
            return CommandError.StateConflict("Only a SCHEDULED Service Visit can be checked into.");
        }

        // ST-WO-002: the Work Order itself must still be SCHEDULED. Checked here (not left to BeginWork's own guard)
        // so a Work Order that has moved on returns a controlled 409 instead of an unhandled domain exception.
        if (!WorkOrderStatusTransitions.IsAllowed(workOrder.Status, WorkOrderStatus.InProgress))
        {
            return CommandError.StateConflict("The Work Order is not in a state that allows work to begin.");
        }

        if (await _store.HasActiveSessionAsync(context.User.TenantId, context.User.UserId, cancellationToken))
        {
            return CommandError.StateConflict("You already have an active Work Session on another Visit. Check out before checking in elsewhere.");
        }

        var now = UtcNow();
        var session = WorkSession.Create(visit.TenantId, visit.Id, context.User.UserId, now);

        workOrder.BeginWork();
        visit.CheckIn();
        _store.Add(session);
        _store.AddAudit(WorkOrderAudit.WorkStarted(context, workOrder, now));
        _store.AddAudit(WorkOrderAudit.CheckedIn(context, visit, session, now));

        var outcome = await _store.SaveChangesAsync(visit, expectedRowVersion, cancellationToken);
        return outcome == WorkOrderSaveOutcome.Saved ? CommandResult<Guid>.Success(workOrder.Id) : CommandError.ConcurrencyConflict;
    }

    /// <summary>UTC truncated to the datetime2(3) precision used by persisted timestamps.</summary>
    private DateTime UtcNow() => WorkOrderScheduleService.ToUtc(_clock.GetUtcNow())!.Value;
}
