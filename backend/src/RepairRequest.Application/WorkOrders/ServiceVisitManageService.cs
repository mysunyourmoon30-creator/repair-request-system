using RepairRequest.Application.Common;
using RepairRequest.Domain.WorkOrders;

namespace RepairRequest.Application.WorkOrders;

/// <summary>
/// ST-SV-004..009 Service Visit management by a Coordinator (S2-003; UC-WO-005..009): Reschedule, Reassign, Cancel,
/// Mark Missed, Decide Missed. Each command runs in one transaction: the Visit is within the Coordinator's site
/// scope (404 otherwise, correlated through its Work Order); row version (409 CONCURRENCY_CONFLICT, checked
/// against the Visit's own RowVersion, not the Work Order's); source state (409 STATE_CONFLICT); reason and any
/// other fields (422). None of these actions change the Work Order's own status (explicit for Cancel — "WO/SLA
/// continue" — inferred by omission for the rest, since no WO-level transition ID is cited for them).
/// </summary>
public sealed class ServiceVisitManageService
{
    private readonly IServiceVisitStore _store;
    private readonly TimeProvider _clock;

    public ServiceVisitManageService(IServiceVisitStore store, TimeProvider clock)
    {
        _store = store;
        _clock = clock;
    }

    public Task<CommandResult<Guid>> RescheduleAsync(
        CommandContext context, Guid serviceVisitId, byte[] expectedRowVersion, string? reason, DateTimeOffset? newStart, DateTimeOffset? newEnd, CancellationToken cancellationToken) =>
        _store.RunInTransactionAsync(() => RescheduleLockedAsync(context, serviceVisitId, expectedRowVersion, reason, newStart, newEnd, cancellationToken), cancellationToken);

    private async Task<CommandResult<Guid>> RescheduleLockedAsync(
        CommandContext context, Guid serviceVisitId, byte[] expectedRowVersion, string? reason, DateTimeOffset? newStart, DateTimeOffset? newEnd, CancellationToken cancellationToken)
    {
        var loaded = await _store.LoadForManageAsync(context.User, serviceVisitId, cancellationToken);
        if (loaded is null)
        {
            return CommandError.NotFound;
        }

        var (visit, _, workOrderId, _) = loaded;

        if (!visit.RowVersion.AsSpan().SequenceEqual(expectedRowVersion))
        {
            return CommandError.ConcurrencyConflict;
        }

        if (visit.Status != ServiceVisitStatus.Scheduled)
        {
            return CommandError.StateConflict("Only a SCHEDULED Service Visit can be rescheduled.");
        }

        if (!TryValidateReason(reason, out var trimmedReason, out var reasonError))
        {
            return reasonError!;
        }

        var start = WorkOrderScheduleService.ToUtc(newStart);
        var end = WorkOrderScheduleService.ToUtc(newEnd);
        if (start is null)
        {
            return CommandError.Validation(ServiceVisitFields.ScheduledStartAt, "A scheduled start is required to reschedule.");
        }

        if (end is null)
        {
            return CommandError.Validation(ServiceVisitFields.ScheduledEndAt, "A scheduled end is required to reschedule.");
        }

        if (end < start)
        {
            return CommandError.Validation(ServiceVisitFields.ScheduledEndAt, "The scheduled end must not be earlier than the scheduled start.");
        }

        var now = UtcNow();
        visit.Reschedule(trimmedReason, start.Value, end.Value);
        _store.AddAudit(WorkOrderAudit.Rescheduled(context, visit, now));

        var outcome = await _store.SaveChangesAsync(visit, expectedRowVersion, cancellationToken);
        return outcome == WorkOrderSaveOutcome.Saved ? CommandResult<Guid>.Success(workOrderId) : CommandError.ConcurrencyConflict;
    }

    public Task<CommandResult<Guid>> ReassignAsync(
        CommandContext context, Guid serviceVisitId, byte[] expectedRowVersion, string? reason, Guid? assignedTeamId, Guid? assignedTechnicianId, CancellationToken cancellationToken) =>
        _store.RunInTransactionAsync(() => ReassignLockedAsync(context, serviceVisitId, expectedRowVersion, reason, assignedTeamId, assignedTechnicianId, cancellationToken), cancellationToken);

    private async Task<CommandResult<Guid>> ReassignLockedAsync(
        CommandContext context, Guid serviceVisitId, byte[] expectedRowVersion, string? reason, Guid? assignedTeamId, Guid? assignedTechnicianId, CancellationToken cancellationToken)
    {
        var loaded = await _store.LoadForManageAsync(context.User, serviceVisitId, cancellationToken);
        if (loaded is null)
        {
            return CommandError.NotFound;
        }

        var (visit, tenantId, workOrderId, siteId) = loaded;

        if (!visit.RowVersion.AsSpan().SequenceEqual(expectedRowVersion))
        {
            return CommandError.ConcurrencyConflict;
        }

        if (visit.Status != ServiceVisitStatus.Scheduled)
        {
            return CommandError.StateConflict("Only a SCHEDULED Service Visit can be reassigned.");
        }

        if (!TryValidateReason(reason, out var trimmedReason, out var reasonError))
        {
            return reasonError!;
        }

        if (assignedTeamId is not { } team || team == Guid.Empty)
        {
            return CommandError.Validation(ServiceVisitFields.AssignedTeamId, "A team identifier is required to reassign.");
        }

        if (assignedTechnicianId is not { } technician
            || siteId is null
            || !await _store.IsTechnicianEligibleAsync(tenantId, technician, siteId.Value, cancellationToken))
        {
            return CommandError.Validation(
                ServiceVisitFields.AssignedTechnicianId, "The technician must be an active Technician assigned to this Work Order's Site.");
        }

        var now = UtcNow();
        visit.Reassign(trimmedReason, team, technician);
        _store.AddAudit(WorkOrderAudit.Reassigned(context, visit, now));

        var outcome = await _store.SaveChangesAsync(visit, expectedRowVersion, cancellationToken);
        return outcome == WorkOrderSaveOutcome.Saved ? CommandResult<Guid>.Success(workOrderId) : CommandError.ConcurrencyConflict;
    }

    public Task<CommandResult<Guid>> CancelAsync(
        CommandContext context, Guid serviceVisitId, byte[] expectedRowVersion, string? reason, CancellationToken cancellationToken) =>
        _store.RunInTransactionAsync(() => CancelLockedAsync(context, serviceVisitId, expectedRowVersion, reason, cancellationToken), cancellationToken);

    private async Task<CommandResult<Guid>> CancelLockedAsync(
        CommandContext context, Guid serviceVisitId, byte[] expectedRowVersion, string? reason, CancellationToken cancellationToken)
    {
        var loaded = await _store.LoadForManageAsync(context.User, serviceVisitId, cancellationToken);
        if (loaded is null)
        {
            return CommandError.NotFound;
        }

        var (visit, _, workOrderId, _) = loaded;

        if (!visit.RowVersion.AsSpan().SequenceEqual(expectedRowVersion))
        {
            return CommandError.ConcurrencyConflict;
        }

        if (!ServiceVisitStatusTransitions.IsAllowed(visit.Status, ServiceVisitStatus.Cancelled))
        {
            return CommandError.StateConflict("Only a SCHEDULED Service Visit can be cancelled.");
        }

        if (!TryValidateReason(reason, out var trimmedReason, out var reasonError))
        {
            return reasonError!;
        }

        var now = UtcNow();
        visit.Cancel(trimmedReason);
        _store.AddAudit(WorkOrderAudit.Cancelled(context, visit, now));

        var outcome = await _store.SaveChangesAsync(visit, expectedRowVersion, cancellationToken);
        return outcome == WorkOrderSaveOutcome.Saved ? CommandResult<Guid>.Success(workOrderId) : CommandError.ConcurrencyConflict;
    }

    public Task<CommandResult<Guid>> MarkMissedAsync(
        CommandContext context, Guid serviceVisitId, byte[] expectedRowVersion, string? reason, CancellationToken cancellationToken) =>
        _store.RunInTransactionAsync(() => MarkMissedLockedAsync(context, serviceVisitId, expectedRowVersion, reason, cancellationToken), cancellationToken);

    private async Task<CommandResult<Guid>> MarkMissedLockedAsync(
        CommandContext context, Guid serviceVisitId, byte[] expectedRowVersion, string? reason, CancellationToken cancellationToken)
    {
        var loaded = await _store.LoadForManageAsync(context.User, serviceVisitId, cancellationToken);
        if (loaded is null)
        {
            return CommandError.NotFound;
        }

        var (visit, _, workOrderId, _) = loaded;

        if (!visit.RowVersion.AsSpan().SequenceEqual(expectedRowVersion))
        {
            return CommandError.ConcurrencyConflict;
        }

        if (!ServiceVisitStatusTransitions.IsAllowed(visit.Status, ServiceVisitStatus.Missed))
        {
            return CommandError.StateConflict("Only a SCHEDULED Service Visit can be marked missed.");
        }

        if (!TryValidateReason(reason, out var trimmedReason, out var reasonError))
        {
            return reasonError!;
        }

        var now = UtcNow();
        visit.MarkMissed(trimmedReason);
        _store.AddAudit(WorkOrderAudit.MarkedMissed(context, visit, now));

        var outcome = await _store.SaveChangesAsync(visit, expectedRowVersion, cancellationToken);
        return outcome == WorkOrderSaveOutcome.Saved ? CommandResult<Guid>.Success(workOrderId) : CommandError.ConcurrencyConflict;
    }

    /// <summary>
    /// ST-SV-009/D-15: RESCHEDULE/FOLLOW_UP/REASSIGN create a new SCHEDULED follow-up Visit (VisitType FollowUp for
    /// all three — the decision code records *why* the follow-up exists, not a different kind of visit);
    /// NO_FOLLOW_UP creates nothing. The original Visit stays MISSED and immutable either way.
    /// </summary>
    public Task<CommandResult<Guid>> DecideMissedAsync(
        CommandContext context,
        Guid serviceVisitId,
        byte[] expectedRowVersion,
        string? decision,
        string? reason,
        MissedVisitFollowUpSchedule? newSchedule,
        CancellationToken cancellationToken) =>
        _store.RunInTransactionAsync(
            () => DecideMissedLockedAsync(context, serviceVisitId, expectedRowVersion, decision, reason, newSchedule, cancellationToken), cancellationToken);

    private async Task<CommandResult<Guid>> DecideMissedLockedAsync(
        CommandContext context,
        Guid serviceVisitId,
        byte[] expectedRowVersion,
        string? decision,
        string? reason,
        MissedVisitFollowUpSchedule? newSchedule,
        CancellationToken cancellationToken)
    {
        var loaded = await _store.LoadForManageAsync(context.User, serviceVisitId, cancellationToken);
        if (loaded is null)
        {
            return CommandError.NotFound;
        }

        var (visit, tenantId, workOrderId, siteId) = loaded;

        if (!visit.RowVersion.AsSpan().SequenceEqual(expectedRowVersion))
        {
            return CommandError.ConcurrencyConflict;
        }

        if (visit.Status != ServiceVisitStatus.Missed)
        {
            return CommandError.StateConflict("Only a MISSED Service Visit can have a follow-up decision recorded.");
        }

        if (visit.MissedDecisionCode is not null)
        {
            return CommandError.StateConflict("This Service Visit's Missed decision was already recorded.");
        }

        if (!MissedVisitDecisionCodes.TryParse(decision, out var parsedDecision))
        {
            return CommandError.Validation(ServiceVisitFields.Decision, "decision must be one of RESCHEDULE, FOLLOW_UP, REASSIGN, NO_FOLLOW_UP.");
        }

        if (!TryValidateReason(reason, out var trimmedReason, out var reasonError))
        {
            return reasonError!;
        }

        ServiceVisit? newVisit = null;
        if (parsedDecision != MissedVisitDecisionCode.NoFollowUp)
        {
            if (newSchedule is null)
            {
                return CommandError.Validation(ServiceVisitFields.NewSchedule, "A new schedule (team, technician, start, end) is required for this decision.");
            }

            if (newSchedule.AssignedTeamId is not { } team || team == Guid.Empty)
            {
                return CommandError.Validation(ServiceVisitFields.AssignedTeamId, "A team identifier is required for the new Visit.");
            }

            var start = WorkOrderScheduleService.ToUtc(newSchedule.ScheduledStartAt);
            var end = WorkOrderScheduleService.ToUtc(newSchedule.ScheduledEndAt);
            if (start is null)
            {
                return CommandError.Validation(ServiceVisitFields.ScheduledStartAt, "A scheduled start is required for the new Visit.");
            }

            if (end is null)
            {
                return CommandError.Validation(ServiceVisitFields.ScheduledEndAt, "A scheduled end is required for the new Visit.");
            }

            if (end < start)
            {
                return CommandError.Validation(ServiceVisitFields.ScheduledEndAt, "The scheduled end must not be earlier than the scheduled start.");
            }

            if (newSchedule.AssignedTechnicianId is not { } technician
                || siteId is null
                || !await _store.IsTechnicianEligibleAsync(tenantId, technician, siteId.Value, cancellationToken))
            {
                return CommandError.Validation(
                    ServiceVisitFields.AssignedTechnicianId, "The technician must be an active Technician assigned to this Work Order's Site.");
            }

            newVisit = ServiceVisit.Create(tenantId, workOrderId, ServiceVisitType.FollowUp, team, technician, start.Value, end.Value, sourceMissedVisitId: visit.Id);
        }

        var now = UtcNow();
        visit.DecideMissed(parsedDecision, now);
        if (newVisit is not null)
        {
            _store.Add(newVisit);
        }

        _store.AddAudit(WorkOrderAudit.MissedDecided(context, visit, trimmedReason, newVisit, now));

        var outcome = await _store.SaveChangesAsync(visit, expectedRowVersion, cancellationToken);
        return outcome == WorkOrderSaveOutcome.Saved ? CommandResult<Guid>.Success(workOrderId) : CommandError.ConcurrencyConflict;
    }

    private static bool TryValidateReason(string? reason, out string trimmed, out CommandError? error)
    {
        trimmed = reason?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > ServiceVisit.ReasonMaxLength)
        {
            error = CommandError.Validation(
                ServiceVisitFields.Reason, $"A reason of 1 to {ServiceVisit.ReasonMaxLength} characters is required.");
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>UTC truncated to the datetime2(3) precision used by persisted timestamps.</summary>
    private DateTime UtcNow() => WorkOrderScheduleService.ToUtc(_clock.GetUtcNow())!.Value;
}
