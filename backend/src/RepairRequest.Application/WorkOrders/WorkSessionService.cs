using RepairRequest.Application.Common;
using RepairRequest.Application.Security;
using RepairRequest.Domain.WorkOrders;

namespace RepairRequest.Application.WorkOrders;

/// <summary>
/// Pause (ST-WS-002; UC-WO-012; S3-002), Resume (ST-WS-003; S3-003) and Check-out (ST-WS-004; UC-WO-016; S3-004)
/// of the Technician's own Work Session, plus the read of their current session every control needs. Each runs
/// in one transaction, checks in the same deterministic order (the same order as every Service Visit action):
/// the session is the caller's own, within their tenant and current Site scope, and not someone else's (404
/// otherwise); row version (409 CONCURRENCY_CONFLICT, checked against the session's own RowVersion); the right
/// source state (409 STATE_CONFLICT — a second Pause of an already-paused session, a Resume of a non-paused one,
/// or a Check-out of a non-CHECKED_IN one, lands here); Pause additionally requires a non-blank reason (422),
/// Resume and Check-out have none. Success flips status, records the relevant time on the session (and, for
/// Check-out, the Visit too), and audits — all with server-derived time, one save. Pause and Resume leave the
/// Work Order and the Visit at IN_PROGRESS (RR-STS-001 defines no transition for either); Check-out moves the
/// Visit to COMPLETED (ST-SV-003) but never the Work Order (UC-WO-016 postcondition: "WO remains IN_PROGRESS
/// until summary submit") — BR-06's summary/outcome/evidence half is not part of this ticket (see
/// <see cref="WorkSession.CheckOut"/>'s own doc comment).
/// </summary>
public sealed class WorkSessionService
{
    private readonly IWorkSessionStore _store;
    private readonly TimeProvider _clock;

    public WorkSessionService(IWorkSessionStore store, TimeProvider clock)
    {
        _store = store;
        _clock = clock;
    }

    public Task<WorkSessionDto?> GetCurrentAsync(CurrentUser user, CancellationToken cancellationToken) =>
        _store.GetCurrentAsync(user, cancellationToken);

    public Task<WorkSessionDto?> GetAsync(CurrentUser user, Guid workSessionId, CancellationToken cancellationToken) =>
        _store.GetOwnSessionAsync(user, workSessionId, cancellationToken);

    public Task<CommandResult<Guid>> PauseAsync(
        CommandContext context, Guid workSessionId, byte[] expectedRowVersion, string? reason, CancellationToken cancellationToken) =>
        _store.RunInTransactionAsync(() => PauseLockedAsync(context, workSessionId, expectedRowVersion, reason, cancellationToken), cancellationToken);

    private async Task<CommandResult<Guid>> PauseLockedAsync(
        CommandContext context, Guid workSessionId, byte[] expectedRowVersion, string? reason, CancellationToken cancellationToken)
    {
        var session = await _store.LoadOwnForUpdateAsync(context.User, workSessionId, cancellationToken);
        if (session is null)
        {
            return CommandError.NotFound;
        }

        if (!session.RowVersion.AsSpan().SequenceEqual(expectedRowVersion))
        {
            return CommandError.ConcurrencyConflict;
        }

        if (!WorkSessionStatusTransitions.IsAllowed(session.Status, WorkSessionStatus.Paused))
        {
            return CommandError.StateConflict(
                session.Status == WorkSessionStatus.Paused
                    ? "This Work Session is already paused."
                    : "Only a CHECKED_IN Work Session can be paused.");
        }

        var trimmed = reason?.Trim() ?? string.Empty;
        if (trimmed.Length == 0 || trimmed.Length > WorkSessionPause.ReasonMaxLength)
        {
            return CommandError.Validation(
                WorkSessionFields.Reason, $"A pause reason of 1 to {WorkSessionPause.ReasonMaxLength} characters is required.");
        }

        var now = WorkOrderScheduleService.ToUtc(_clock.GetUtcNow())!.Value;
        var pause = session.Pause(trimmed, now);

        _store.AddPause(pause);
        _store.AddAudit(WorkOrderAudit.Paused(context, session, pause, now));

        var outcome = await _store.SaveChangesAsync(session, expectedRowVersion, cancellationToken);
        return outcome == WorkOrderSaveOutcome.Saved ? CommandResult<Guid>.Success(session.Id) : CommandError.ConcurrencyConflict;
    }

    public Task<CommandResult<Guid>> ResumeAsync(
        CommandContext context, Guid workSessionId, byte[] expectedRowVersion, CancellationToken cancellationToken) =>
        _store.RunInTransactionAsync(() => ResumeLockedAsync(context, workSessionId, expectedRowVersion, cancellationToken), cancellationToken);

    private async Task<CommandResult<Guid>> ResumeLockedAsync(
        CommandContext context, Guid workSessionId, byte[] expectedRowVersion, CancellationToken cancellationToken)
    {
        var session = await _store.LoadOwnForUpdateAsync(context.User, workSessionId, cancellationToken);
        if (session is null)
        {
            return CommandError.NotFound;
        }

        if (!session.RowVersion.AsSpan().SequenceEqual(expectedRowVersion))
        {
            return CommandError.ConcurrencyConflict;
        }

        if (!WorkSessionStatusTransitions.IsAllowed(session.Status, WorkSessionStatus.CheckedIn))
        {
            return CommandError.StateConflict(
                session.Status == WorkSessionStatus.CheckedIn
                    ? "This Work Session is not paused."
                    : "Only a PAUSED Work Session can be resumed.");
        }

        var openPause = await _store.LoadOpenPauseAsync(session.Id, cancellationToken);
        if (openPause is null)
        {
            // Defensive: unreachable in practice (Pause/Resume are the session's only writers), but a PAUSED
            // session found with no open pause row is a data-integrity fault, not a crash.
            return CommandError.StateConflict("This Work Session has no active pause to resume.");
        }

        var now = WorkOrderScheduleService.ToUtc(_clock.GetUtcNow())!.Value;
        session.Resume(openPause, now);

        _store.AddAudit(WorkOrderAudit.Resumed(context, session, openPause, now));

        var outcome = await _store.SaveChangesAsync(session, expectedRowVersion, cancellationToken);
        return outcome == WorkOrderSaveOutcome.Saved ? CommandResult<Guid>.Success(session.Id) : CommandError.ConcurrencyConflict;
    }

    public Task<CommandResult<Guid>> CheckOutAsync(
        CommandContext context, Guid workSessionId, byte[] expectedRowVersion, CancellationToken cancellationToken) =>
        _store.RunInTransactionAsync(() => CheckOutLockedAsync(context, workSessionId, expectedRowVersion, cancellationToken), cancellationToken);

    private async Task<CommandResult<Guid>> CheckOutLockedAsync(
        CommandContext context, Guid workSessionId, byte[] expectedRowVersion, CancellationToken cancellationToken)
    {
        var loaded = await _store.LoadOwnForCheckOutAsync(context.User, workSessionId, cancellationToken);
        if (loaded is null)
        {
            return CommandError.NotFound;
        }

        var (session, visit) = loaded;

        if (!session.RowVersion.AsSpan().SequenceEqual(expectedRowVersion))
        {
            return CommandError.ConcurrencyConflict;
        }

        if (!WorkSessionStatusTransitions.IsAllowed(session.Status, WorkSessionStatus.CheckedOut))
        {
            return CommandError.StateConflict(
                session.Status == WorkSessionStatus.CheckedOut
                    ? "This Work Session is already checked out."
                    : "Only a CHECKED_IN Work Session can be checked out.");
        }

        var now = WorkOrderScheduleService.ToUtc(_clock.GetUtcNow())!.Value;
        session.CheckOut(now);
        visit.CheckOut(now);

        _store.AddAudit(WorkOrderAudit.CheckedOut(context, session, now));
        _store.AddAudit(WorkOrderAudit.VisitCompleted(context, visit, session, now));

        var outcome = await _store.SaveChangesAsync(session, visit, expectedRowVersion, cancellationToken);
        return outcome == WorkOrderSaveOutcome.Saved ? CommandResult<Guid>.Success(session.Id) : CommandError.ConcurrencyConflict;
    }
}
