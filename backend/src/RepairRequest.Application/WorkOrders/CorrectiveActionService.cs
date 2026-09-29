using RepairRequest.Application.Common;
using RepairRequest.Domain.WorkOrders;

namespace RepairRequest.Application.WorkOrders;

/// <summary>
/// Corrective Action Submit Plan (ST-CA-002; CA-API-001; Team Lead) and Approve Plan (ST-CA-003; CA-API-002;
/// Supervisor) — `docs/13` §4.21; Ticket 6. Each runs in one transaction, checks in the same deterministic order
/// as every other command in this codebase: scope (404), the Work Order's own RowVersion (409
/// CONCURRENCY_CONFLICT — see <see cref="ICorrectiveActionStore"/>'s own remarks on why this is the correct
/// token), the Corrective Action's own state guard (409 STATE_CONFLICT), then field validation (422, Submit Plan
/// only). Success writes the Corrective Action's new fields, the Work Order's own status transition (ST-CA-002 /
/// ST-WO-008, or ST-CA-003 / ST-WO-009) and one audit row, all in the same save. No Separation of Duties guards
/// Approve Plan — `docs/13` §4.21 Decision (d) records that absence as a review risk, not a baseline requirement.
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
            workOrder.RowVersion);
}
