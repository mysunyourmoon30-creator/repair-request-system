using RepairRequest.Application.Common;
using RepairRequest.Application.Security;
using RepairRequest.Domain.Common;
using RepairRequest.Domain.WorkOrders;

namespace RepairRequest.Application.WorkOrders;

/// <summary>
/// Request (ST-TC-001), Approve (ST-TC-002) and Reject (ST-TC-003) of a Time Correction (UC-TIME-001; `docs/17`, owner approval
/// of 13 October 2026), plus the reads they need. Every command runs in one transaction that holds its locks until the apply,
/// the audit rows and the decision commit together. Checks run in this order (the repository convention): scope (404);
/// Approve/Reject: the caller is not the requester (403); the correction's row version (409 CONCURRENCY_CONFLICT); the source
/// state (409 STATE_CONFLICT); then the Work Order guard (Request/Approve only: CLOSED or CANCELLED is a 409; Reject is still
/// allowed because it changes no time fact); then field validation (422, Request and Reject); Approve re-validates the facts as
/// a 409 because it carries no client input to be wrong about.
/// <list type="bullet">
/// <item>Targets: CHECK_IN and CHECK_OUT address the session; PAUSE_START and RESUME must name the exact pause
/// (<c>workSessionPauseId</c>), which must be a pause row of that session (a RESUME target must be closed).</item>
/// <item>Values: the new value must differ from the current one, must not exceed the server UTC now, and the whole session
/// (check-in, every pause interval, check-out) must stay chronological — at Request (422) and again at Approve against the
/// <em>current</em> facts (409). There is no Visit schedule-window validation.</item>
/// <item>Stale approvals: Approve compares the target's current value with the recorded <c>old_value</c> and refuses (409) if it
/// changed since the request, so one correction can never silently overwrite another.</item>
/// <item>A CHECK_OUT correction also corrects <c>service_visit.completed_at</c> only when existing code proves the Check-out is
/// that value's authoritative source: the Visit is COMPLETED, its completion time equals the correction's recorded old value (the
/// instant the Check-out wrote) and the corrected session is the Visit's only session. Otherwise the completion time is left
/// untouched and the approval audit records that it was not synchronized.</item>
/// <item>SLA is never read or written (BR-13).</item>
/// </list>
/// </summary>
public sealed class TimeCorrectionService
{
    private readonly ITimeCorrectionStore _store;
    private readonly TimeProvider _clock;

    public TimeCorrectionService(ITimeCorrectionStore store, TimeProvider clock)
    {
        _store = store;
        _clock = clock;
    }

    public Task<TimeCorrectionDto?> GetAsync(CurrentUser user, Guid timeCorrectionId, CancellationToken cancellationToken) =>
        _store.GetAsync(user, timeCorrectionId, cancellationToken);

    public Task<PagedResult<TimeCorrectionDto>> ListPendingAsync(CurrentUser user, PendingTimeCorrectionsQuery query, CancellationToken cancellationToken) =>
        _store.ListPendingAsync(user, query, cancellationToken);

    // ------------------------------------------------------------------------------------------------ Request

    public Task<CommandResult<Guid>> RequestAsync(
        CommandContext context,
        Guid workSessionId,
        string? timestampTypeCode,
        Guid? workSessionPauseId,
        DateTimeOffset? newValue,
        string? reason,
        CancellationToken cancellationToken) =>
        _store.RunInTransactionAsync(
            () => RequestLockedAsync(context, workSessionId, timestampTypeCode, workSessionPauseId, newValue, reason, cancellationToken),
            cancellationToken);

    private async Task<CommandResult<Guid>> RequestLockedAsync(
        CommandContext context,
        Guid workSessionId,
        string? timestampTypeCode,
        Guid? workSessionPauseId,
        DateTimeOffset? newValue,
        string? reason,
        CancellationToken cancellationToken)
    {
        var loaded = await _store.LoadForRequestAsync(context.User, workSessionId, cancellationToken);
        if (loaded is null)
        {
            return CommandError.NotFound;
        }

        var (session, pauses, status) = (loaded.Session, loaded.Pauses, loaded.WorkOrderStatus);

        if (IsLocked(status))
        {
            return CommandError.StateConflict($"A Work Order that is {status.ToString().ToUpperInvariant()} can no longer have its work times corrected.");
        }

        if (!TimeCorrectionTimestampTypeCodes.TryParse(timestampTypeCode, out var type))
        {
            return CommandError.Validation(
                TimeCorrectionFields.TimestampType, "timestampType must be one of CHECK_IN, PAUSE_START, RESUME, CHECK_OUT.");
        }

        WorkSessionPause? pause = null;
        if (TimeCorrection.RequiresPause(type))
        {
            if (workSessionPauseId is null || workSessionPauseId == Guid.Empty)
            {
                return CommandError.Validation(
                    TimeCorrectionFields.WorkSessionPauseId, "workSessionPauseId is required for PAUSE_START and RESUME corrections.");
            }

            // Only this session's own pause rows are loaded: an unknown id and another session's or tenant's id look the same.
            pause = pauses.FirstOrDefault(item => item.Id == workSessionPauseId.Value);
            if (pause is null)
            {
                return CommandError.Validation(
                    TimeCorrectionFields.WorkSessionPauseId, "The pause was not found on this Work Session.");
            }
        }
        else if (workSessionPauseId is not null)
        {
            return CommandError.Validation(
                TimeCorrectionFields.WorkSessionPauseId, "workSessionPauseId is only allowed for PAUSE_START and RESUME corrections.");
        }

        if (!session.HasTimestamp(type, pause))
        {
            return CommandError.Validation(
                TimeCorrectionFields.TimestampType, "This Work Session has no value yet for the requested timestamp.");
        }

        var utcNewValue = WorkOrderScheduleService.ToUtc(newValue);
        if (utcNewValue is null)
        {
            return CommandError.Validation(TimeCorrectionFields.NewValue, "newValue is required.");
        }

        var trimmedReason = reason?.Trim() ?? string.Empty;
        if (trimmedReason.Length == 0 || trimmedReason.Length > TimeCorrection.ReasonMaxLength)
        {
            return CommandError.Validation(
                TimeCorrectionFields.Reason, $"A reason of 1 to {TimeCorrection.ReasonMaxLength} characters is required.");
        }

        var now = UtcNow();
        if (utcNewValue.Value > now)
        {
            return CommandError.Validation(TimeCorrectionFields.NewValue, "newValue cannot be in the future.");
        }

        var oldValue = session.CurrentTimestamp(type, pause);
        if (utcNewValue.Value == oldValue)
        {
            return CommandError.Validation(TimeCorrectionFields.NewValue, "newValue must differ from the current value.");
        }

        var violation = session.ChronologyViolationAfter(type, utcNewValue.Value, pause, pauses);
        if (violation is not null)
        {
            return CommandError.Validation(TimeCorrectionFields.NewValue, $"The corrected timestamp would break the chronological order: {violation}");
        }

        var correction = TimeCorrection.Request(
            session.TenantId, session.Id, pause?.Id, type, oldValue, utcNewValue.Value, trimmedReason, context.User.UserId);

        _store.Add(correction);
        _store.AddAudit(WorkOrderAudit.TimeCorrectionRequested(context, correction, now));

        return await _store.SaveNewAsync(cancellationToken) == TimeCorrectionInsertOutcome.Saved
            ? CommandResult<Guid>.Success(correction.Id)
            : CommandError.StateConflict("A pending correction already exists for this timestamp.");
    }

    // ------------------------------------------------------------------------------------------------ Approve

    public Task<CommandResult<Guid>> ApproveAsync(
        CommandContext context, Guid timeCorrectionId, byte[] expectedRowVersion, CancellationToken cancellationToken) =>
        _store.RunInTransactionAsync(() => ApproveLockedAsync(context, timeCorrectionId, expectedRowVersion, cancellationToken), cancellationToken);

    private async Task<CommandResult<Guid>> ApproveLockedAsync(
        CommandContext context, Guid timeCorrectionId, byte[] expectedRowVersion, CancellationToken cancellationToken)
    {
        var loaded = await _store.LoadForApproveAsync(context.User, timeCorrectionId, cancellationToken);
        if (loaded is null)
        {
            return CommandError.NotFound;
        }

        var (correction, session, pauses, visit) = (loaded.Correction, loaded.Session, loaded.Pauses, loaded.Visit);

        // Separation of duties (`docs/17` P7): never the requester, even for a user holding both roles; object-level 403 before
        // concurrency and state, like the Cost Summary review.
        if (correction.RequestedBy == context.User.UserId)
        {
            return CommandError.AccessDenied;
        }

        if (!correction.RowVersion.AsSpan().SequenceEqual(expectedRowVersion))
        {
            return CommandError.ConcurrencyConflict;
        }

        if (!TimeCorrectionStatusTransitions.IsAllowed(correction.Status, TimeCorrectionStatus.Approved))
        {
            return CommandError.StateConflict(
                correction.Status == TimeCorrectionStatus.Approved
                    ? "This Time Correction is already approved."
                    : "Only a PENDING Time Correction can be approved.");
        }

        if (IsLocked(loaded.WorkOrderStatus))
        {
            return CommandError.StateConflict($"A Work Order that is {loaded.WorkOrderStatus.ToString().ToUpperInvariant()} can no longer have its work times corrected.");
        }

        var type = correction.TimestampType;
        var pause = correction.WorkSessionPauseId is { } pauseId ? pauses.FirstOrDefault(item => item.Id == pauseId) : null;

        // The session's facts may have changed since the request. Approve carries no client input, so a mismatch is the current
        // state conflicting with the request (409), never a 422.
        if (!session.HasTimestamp(type, pause))
        {
            return CommandError.StateConflict("The timestamp this correction targets no longer exists on the Work Session.");
        }

        if (session.CurrentTimestamp(type, pause) != correction.OldValue)
        {
            return CommandError.StateConflict("The timestamp this correction targets has changed since it was requested; it cannot be approved.");
        }

        var now = UtcNow();
        if (correction.NewValue > now)
        {
            return CommandError.StateConflict("The corrected timestamp cannot be in the future.");
        }

        var violation = session.ChronologyViolationAfter(type, correction.NewValue, pause, pauses);
        if (violation is not null)
        {
            return CommandError.StateConflict($"The Work Session's facts have changed since this correction was requested; applying it would now break the chronological order: {violation}");
        }

        try
        {
            session.ApplyTimeCorrection(type, correction.NewValue, pause, pauses);
        }
        catch (DomainRuleViolationException exception)
        {
            return CommandError.StateConflict(exception.Message);
        }

        var visitSynchronized = false;
        if (type == TimeCorrectionTimestampType.CheckOut
            && visit.Status == ServiceVisitStatus.Completed
            && visit.CompletedAt == correction.OldValue
            && loaded.VisitSessionCount == 1)
        {
            visit.CorrectCompletedAt(correction.NewValue);
            visitSynchronized = true;
        }

        correction.Approve(now, context.User.UserId);
        _store.AddAudit(WorkOrderAudit.TimeCorrectionApproved(context, correction, visitSynchronized, now));

        var outcome = await _store.SaveApprovalAsync(correction, session, expectedRowVersion, cancellationToken);
        return outcome == WorkOrderSaveOutcome.Saved ? CommandResult<Guid>.Success(correction.Id) : CommandError.ConcurrencyConflict;
    }

    // ------------------------------------------------------------------------------------------------ Reject

    public Task<CommandResult<Guid>> RejectAsync(
        CommandContext context, Guid timeCorrectionId, byte[] expectedRowVersion, string? decisionReason, CancellationToken cancellationToken) =>
        _store.RunInTransactionAsync(
            () => RejectLockedAsync(context, timeCorrectionId, expectedRowVersion, decisionReason, cancellationToken), cancellationToken);

    private async Task<CommandResult<Guid>> RejectLockedAsync(
        CommandContext context, Guid timeCorrectionId, byte[] expectedRowVersion, string? decisionReason, CancellationToken cancellationToken)
    {
        var correction = await _store.LoadForRejectAsync(context.User, timeCorrectionId, cancellationToken);
        if (correction is null)
        {
            return CommandError.NotFound;
        }

        if (correction.RequestedBy == context.User.UserId)
        {
            return CommandError.AccessDenied;
        }

        if (!correction.RowVersion.AsSpan().SequenceEqual(expectedRowVersion))
        {
            return CommandError.ConcurrencyConflict;
        }

        if (!TimeCorrectionStatusTransitions.IsAllowed(correction.Status, TimeCorrectionStatus.Rejected))
        {
            return CommandError.StateConflict(
                correction.Status == TimeCorrectionStatus.Rejected
                    ? "This Time Correction is already rejected."
                    : "Only a PENDING Time Correction can be rejected.");
        }

        var trimmed = decisionReason?.Trim() ?? string.Empty;
        if (trimmed.Length == 0 || trimmed.Length > TimeCorrection.ReasonMaxLength)
        {
            return CommandError.Validation(
                TimeCorrectionFields.DecisionReason, $"A decision reason of 1 to {TimeCorrection.ReasonMaxLength} characters is required to reject.");
        }

        // Rejecting changes no time fact, so it stays possible after the Work Order is CLOSED or CANCELLED (`docs/17` P4).
        var now = UtcNow();
        correction.Reject(trimmed, now, context.User.UserId);
        _store.AddAudit(WorkOrderAudit.TimeCorrectionRejected(context, correction, now));

        var outcome = await _store.SaveRejectionAsync(correction, expectedRowVersion, cancellationToken);
        return outcome == WorkOrderSaveOutcome.Saved ? CommandResult<Guid>.Success(correction.Id) : CommandError.ConcurrencyConflict;
    }

    /// <summary>`docs/17` P4: a CLOSED or CANCELLED Work Order accepts no new or approved correction (the baseline "operation locked").</summary>
    private static bool IsLocked(WorkOrderStatus status) => status is WorkOrderStatus.Closed or WorkOrderStatus.Cancelled;

    /// <summary>UTC truncated to the datetime2(3) precision used by persisted timestamps.</summary>
    private DateTime UtcNow() => WorkOrderScheduleService.ToUtc(_clock.GetUtcNow())!.Value;
}
