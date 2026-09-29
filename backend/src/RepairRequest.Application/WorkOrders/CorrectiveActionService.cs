using RepairRequest.Application.Common;
using RepairRequest.Domain.WorkOrders;

namespace RepairRequest.Application.WorkOrders;

/// <summary>
/// Corrective Action Submit Plan (ST-CA-002; CA-API-001; Team Lead), Approve Plan (ST-CA-003; CA-API-002;
/// Supervisor) — `docs/13` §4.21; Ticket 6 — and Schedule Rework (CA-API-003; Coordinator; `docs/13` §4.22). Each
/// runs in one transaction, checks in the same deterministic order as every other command in this codebase: scope
/// (404), the concurrency token (409 CONCURRENCY_CONFLICT — the Work Order's own RowVersion for Submit/Approve
/// Plan, the Corrective Action's own for Schedule Rework; see <see cref="ICorrectiveActionStore"/>'s own remarks),
/// the Corrective Action's own state guard (409 STATE_CONFLICT), then field validation (422). Submit/Approve Plan
/// success writes the Corrective Action's new fields, the Work Order's own status transition (ST-CA-002 /
/// ST-WO-008, or ST-CA-003 / ST-WO-009) and one audit row, all in the same save. No Separation of Duties guards
/// Approve Plan — `docs/13` §4.21 Decision (d) records that absence as a review risk, not a baseline requirement.
/// Schedule Rework success writes the new Service Visit, links it on the Corrective Action and one audit row —
/// deliberately leaves both the Corrective Action's Status and the Work Order's Status untouched (`docs/13` §4.22).
/// </summary>
public sealed class CorrectiveActionService
{
    private readonly ICorrectiveActionStore _store;
    private readonly TimeProvider _clock;

    public CorrectiveActionService(ICorrectiveActionStore store, TimeProvider clock)
    {
        _store = store;
        _clock = clock;
    }

    public Task<CommandResult<CorrectiveActionDto>> SubmitPlanAsync(
        CommandContext context, Guid correctiveActionId, byte[] expectedRowVersion, string? planText, Guid? planFileAssetId, CancellationToken cancellationToken) =>
        _store.RunInTransactionAsync(() => SubmitPlanLockedAsync(context, correctiveActionId, expectedRowVersion, planText, planFileAssetId, cancellationToken), cancellationToken);

    private async Task<CommandResult<CorrectiveActionDto>> SubmitPlanLockedAsync(
        CommandContext context, Guid correctiveActionId, byte[] expectedRowVersion, string? planText, Guid? planFileAssetId, CancellationToken cancellationToken)
    {
        var (workOrder, correctiveAction) = await _store.LoadInScopeAsync(context.User, correctiveActionId, cancellationToken);
        if (workOrder is null || correctiveAction is null)
        {
            return CommandError.NotFound;
        }

        if (!workOrder.RowVersion.AsSpan().SequenceEqual(expectedRowVersion))
        {
            return CommandError.ConcurrencyConflict;
        }

        if (!CorrectiveActionStatusTransitions.IsAllowed(correctiveAction.Status, CorrectiveActionStatus.PendingPlanApproval))
        {
            return CommandError.StateConflict(
                correctiveAction.Status == CorrectiveActionStatus.Draft
                    ? "Only a DRAFT Corrective Action can have its plan submitted."
                    : "This Corrective Action's plan has already been submitted.");
        }

        var trimmedPlanText = planText?.Trim() ?? string.Empty;
        if (trimmedPlanText.Length == 0 || trimmedPlanText.Length > CorrectiveAction.PlanTextMaxLength)
        {
            return CommandError.Validation(CorrectiveActionFields.PlanText, $"A plan of 1 to {CorrectiveAction.PlanTextMaxLength} characters is required.");
        }

        if (planFileAssetId is null || planFileAssetId == Guid.Empty)
        {
            return CommandError.Validation(CorrectiveActionFields.PlanFileAssetId, "A plan file is required.");
        }

        // UC-WO-023 Preconditions: "Plan text + CLEAN evidence" (docs/04); its Validation/Authorization line names
        // "file scan" explicitly — the plan file must exist, belong to this tenant, and have passed the malware
        // scan before Submit Plan proceeds (docs/13 §4.21).
        if (!await _store.IsPlanFileUsableAsync(workOrder.TenantId, planFileAssetId.Value, cancellationToken))
        {
            return CommandError.Validation(CorrectiveActionFields.PlanFileAssetId, "The plan file was not found, or has not passed the malware scan (CLEAN).");
        }

        var now = WorkOrderScheduleService.ToUtc(_clock.GetUtcNow())!.Value;
        correctiveAction.SubmitPlan(context.User.UserId, trimmedPlanText, planFileAssetId.Value);
        workOrder.SubmitCorrectivePlan();
        _store.AddAudit(WorkOrderAudit.CorrectiveActionPlanSubmitted(context, correctiveAction, workOrder, now));

        var outcome = await _store.SaveChangesAsync(workOrder, expectedRowVersion, cancellationToken);
        return outcome == WorkOrderSaveOutcome.Saved ? CommandResult<CorrectiveActionDto>.Success(ToDto(correctiveAction, workOrder)) : CommandError.ConcurrencyConflict;
    }

    public Task<CommandResult<CorrectiveActionDto>> ApprovePlanAsync(
        CommandContext context, Guid correctiveActionId, byte[] expectedRowVersion, CancellationToken cancellationToken) =>
        _store.RunInTransactionAsync(() => ApprovePlanLockedAsync(context, correctiveActionId, expectedRowVersion, cancellationToken), cancellationToken);

    private async Task<CommandResult<CorrectiveActionDto>> ApprovePlanLockedAsync(
        CommandContext context, Guid correctiveActionId, byte[] expectedRowVersion, CancellationToken cancellationToken)
    {
        var (workOrder, correctiveAction) = await _store.LoadInScopeAsync(context.User, correctiveActionId, cancellationToken);
        if (workOrder is null || correctiveAction is null)
        {
            return CommandError.NotFound;
        }

        if (!workOrder.RowVersion.AsSpan().SequenceEqual(expectedRowVersion))
        {
            return CommandError.ConcurrencyConflict;
        }

        if (!CorrectiveActionStatusTransitions.IsAllowed(correctiveAction.Status, CorrectiveActionStatus.Approved))
        {
            return CommandError.StateConflict(
                correctiveAction.Status == CorrectiveActionStatus.Approved
                    ? "This Corrective Action's plan has already been approved."
                    : "Only a Corrective Action with a submitted plan can be approved.");
        }

        var now = WorkOrderScheduleService.ToUtc(_clock.GetUtcNow())!.Value;
        correctiveAction.ApprovePlan(context.User.UserId, now);
        workOrder.ApproveCorrectivePlan();
        _store.AddAudit(WorkOrderAudit.CorrectiveActionPlanApproved(context, correctiveAction, workOrder, now));

        var outcome = await _store.SaveChangesAsync(workOrder, expectedRowVersion, cancellationToken);
        return outcome == WorkOrderSaveOutcome.Saved ? CommandResult<CorrectiveActionDto>.Success(ToDto(correctiveAction, workOrder)) : CommandError.ConcurrencyConflict;
    }

    /// <summary>
    /// CA-API-003 Schedule Rework (Coordinator only; `docs/13` §4.22 — Portfolio Project Owner directive, not a
    /// baseline-literal actor). Reuses the same team/technician-eligibility/window checks as ST-WO-001 Schedule
    /// and ST-SV-006 Reassign (<see cref="WorkOrderScheduleService"/>). Deliberately enforces <b>no</b> Visit-
    /// scheduling-time overlap guard — `docs/03` §3's own guard column for ST-SV-001 ("Create Scheduled Visit")
    /// lists none; the only "no overlap" guards in the baseline (ST-SV-002, ST-WS-001) are Check-in-time, out of
    /// this ticket's scope. This is baseline-silent, not baseline-permitting — see `docs/13` §4.22's own note.
    /// </summary>
    public Task<CommandResult<CorrectiveActionDto>> ScheduleReworkAsync(
        CommandContext context,
        Guid correctiveActionId,
        byte[] expectedRowVersion,
        Guid? assignedTeamId,
        Guid? assignedTechnicianId,
        DateTimeOffset? scheduledStartAt,
        DateTimeOffset? scheduledEndAt,
        CancellationToken cancellationToken) =>
        _store.RunInTransactionAsync(
            () => ScheduleReworkLockedAsync(context, correctiveActionId, expectedRowVersion, assignedTeamId, assignedTechnicianId, scheduledStartAt, scheduledEndAt, cancellationToken),
            cancellationToken);

    private async Task<CommandResult<CorrectiveActionDto>> ScheduleReworkLockedAsync(
        CommandContext context,
        Guid correctiveActionId,
        byte[] expectedRowVersion,
        Guid? assignedTeamId,
        Guid? assignedTechnicianId,
        DateTimeOffset? scheduledStartAt,
        DateTimeOffset? scheduledEndAt,
        CancellationToken cancellationToken)
    {
        var loaded = await _store.LoadForScheduleReworkAsync(context.User, correctiveActionId, cancellationToken);
        if (loaded is null)
        {
            return CommandError.NotFound;
        }

        var (workOrder, correctiveAction, siteId) = loaded;

        if (!correctiveAction.RowVersion.AsSpan().SequenceEqual(expectedRowVersion))
        {
            return CommandError.ConcurrencyConflict;
        }

        if (correctiveAction.Status != CorrectiveActionStatus.Approved)
        {
            return CommandError.StateConflict("Only an APPROVED Corrective Action can have its rework scheduled.");
        }

        if (correctiveAction.CorrectiveServiceVisitId is not null)
        {
            return CommandError.StateConflict("This Corrective Action's rework has already been scheduled.");
        }

        if (assignedTeamId is not { } team || team == Guid.Empty)
        {
            return CommandError.Validation(ServiceVisitFields.AssignedTeamId, "A team identifier is required to schedule rework.");
        }

        var start = WorkOrderScheduleService.ToUtc(scheduledStartAt);
        var end = WorkOrderScheduleService.ToUtc(scheduledEndAt);
        if (start is null)
        {
            return CommandError.Validation(ServiceVisitFields.ScheduledStartAt, "A scheduled start is required to schedule rework.");
        }

        if (end is null)
        {
            return CommandError.Validation(ServiceVisitFields.ScheduledEndAt, "A scheduled end is required to schedule rework.");
        }

        if (end < start)
        {
            return CommandError.Validation(ServiceVisitFields.ScheduledEndAt, "The scheduled end must not be earlier than the scheduled start.");
        }

        if (assignedTechnicianId is not { } technician
            || siteId is null
            || !await _store.IsTechnicianEligibleAsync(workOrder.TenantId, technician, siteId.Value, cancellationToken))
        {
            return CommandError.Validation(
                ServiceVisitFields.AssignedTechnicianId, "The technician must be an active Technician assigned to this Work Order's Site.");
        }

        var now = WorkOrderScheduleService.ToUtc(_clock.GetUtcNow())!.Value;
        var visit = ServiceVisit.Create(workOrder.TenantId, workOrder.Id, ServiceVisitType.Corrective, team, technician, start.Value, end.Value);

        // Add before anything reads visit.Id: the client-side sequential-GUID generator only assigns it once the
        // entity is tracked (same ordering as WorkOrderScheduleService.ScheduleLockedAsync).
        _store.Add(visit);
        correctiveAction.ScheduleRework(visit.Id);
        _store.AddAudit(WorkOrderAudit.CorrectiveActionReworkScheduled(context, correctiveAction, workOrder, visit, now));

        var outcome = await _store.SaveScheduleReworkAsync(correctiveAction, expectedRowVersion, cancellationToken);
        return outcome == WorkOrderSaveOutcome.Saved ? CommandResult<CorrectiveActionDto>.Success(ToDto(correctiveAction, workOrder)) : CommandError.ConcurrencyConflict;
    }

    private static CorrectiveActionDto ToDto(CorrectiveAction correctiveAction, WorkOrder workOrder) =>
        new(
            correctiveAction.Id,
            correctiveAction.WorkOrderId,
            correctiveAction.CycleNo,
            correctiveAction.Status,
            correctiveAction.OwnerTeamLeadId,
            correctiveAction.PlanText,
            correctiveAction.PlanFileAssetId,
            correctiveAction.ApprovedBy,
            correctiveAction.ApprovedAt,
            workOrder.RowVersion,
            correctiveAction.CorrectiveServiceVisitId,
            correctiveAction.RowVersion);
}
