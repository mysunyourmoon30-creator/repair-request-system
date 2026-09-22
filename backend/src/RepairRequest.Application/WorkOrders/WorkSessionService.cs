using RepairRequest.Application.Common;
using RepairRequest.Application.Security;
using RepairRequest.Domain.WorkOrders;

namespace RepairRequest.Application.WorkOrders;

/// <summary>
/// Pause (ST-WS-002; UC-WO-012; S3-002) and Resume (ST-WS-003; S3-003) of the Technician's own Work Session, plus
/// the read of their current session both controls need. Each runs in one transaction, checks in the same
/// deterministic order (the same order as every Service Visit action): the session is the caller's own, within
/// their tenant and current Site scope, and not someone else's (404 otherwise); row version (409
/// CONCURRENCY_CONFLICT, checked against the session's own RowVersion); the right source state (409
/// STATE_CONFLICT — a second Pause of an already-paused session, or a Resume of a non-paused one, lands here);
/// Pause additionally requires a non-blank reason (422), Resume has none. Success flips status, records the
/// pause/resume time on the session and pause period, and audits — all with server-derived time, one save. The
/// Work Order and the Visit stay IN_PROGRESS throughout — RR-STS-001 defines no Work Order or Visit transition
/// for either action.
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
}
