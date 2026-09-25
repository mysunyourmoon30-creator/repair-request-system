using RepairRequest.Application.Common;
using RepairRequest.Application.Security;
using RepairRequest.Domain.WorkOrders;

namespace RepairRequest.Application.WorkOrders;

/// <summary>
/// Customer Accept (ST-WO-005; UC-WO-021, per `docs/13` §4.16) and Customer Reject (ST-WO-007; UC-WO-022;
/// BR-07/BR-15, per `docs/13` §4.17) — the exact designated Acceptance Contact only. Each runs in one
/// transaction, checks in the same deterministic order as every other command in this codebase: scope (404 —
/// resource-specific identity + tenant + Site, not a role-scoped query), row version against the Work Order's
/// own RowVersion (409 CONCURRENCY_CONFLICT), the right source state (409 STATE_CONFLICT), then field validation
/// (422, Reject only — the decision reason). Success writes the Work Order's new status, a <see
/// cref="CustomerAcceptance"/> decision row (both actions, per `docs/13` §4.17 schema decision), and — Reject
/// only — a DRAFT <see cref="CorrectiveAction"/> row, all server-derived, one save, one audit row.
/// </summary>
public sealed class WorkOrderAcceptanceService
{
    private readonly IWorkOrderAcceptanceStore _store;
    private readonly TimeProvider _clock;

    public WorkOrderAcceptanceService(IWorkOrderAcceptanceStore store, TimeProvider clock)
    {
        _store = store;
        _clock = clock;
    }

    public Task<CommandResult<Guid>> AcceptAsync(
        CommandContext context, Guid workOrderId, byte[] expectedRowVersion, CancellationToken cancellationToken) =>
        _store.RunInTransactionAsync(() => AcceptLockedAsync(context, workOrderId, expectedRowVersion, cancellationToken), cancellationToken);

    private async Task<CommandResult<Guid>> AcceptLockedAsync(
        CommandContext context, Guid workOrderId, byte[] expectedRowVersion, CancellationToken cancellationToken)
    {
        var workOrder = await _store.LoadForAcceptAsync(context.User, workOrderId, cancellationToken);
        if (workOrder is null)
        {
            return CommandError.NotFound;
        }

        if (!workOrder.RowVersion.AsSpan().SequenceEqual(expectedRowVersion))
        {
            return CommandError.ConcurrencyConflict;
        }

        if (!WorkOrderStatusTransitions.IsAllowed(workOrder.Status, WorkOrderStatus.Completed))
        {
            return CommandError.StateConflict(
                workOrder.Status == WorkOrderStatus.Completed
                    ? "This Work Order was already accepted."
                    : "Only a Work Order awaiting customer acceptance can be accepted.");
        }

        var now = WorkOrderScheduleService.ToUtc(_clock.GetUtcNow())!.Value;
        var roundNo = await _store.NextAcceptanceRoundNoAsync(workOrder.Id, cancellationToken);
        var acceptance = CustomerAcceptance.Accept(workOrder.TenantId, workOrder.Id, roundNo, workOrder.AcceptanceContactId!.Value, now);
        workOrder.Accept();

        _store.Add(acceptance);
        _store.AddAudit(WorkOrderAudit.Accepted(context, workOrder, now));

        var outcome = await _store.SaveChangesAsync(workOrder, expectedRowVersion, cancellationToken);
        return outcome == WorkOrderSaveOutcome.Saved ? CommandResult<Guid>.Success(workOrder.Id) : CommandError.ConcurrencyConflict;
    }

    public Task<CommandResult<Guid>> RejectAsync(
        CommandContext context, Guid workOrderId, byte[] expectedRowVersion, string? decisionReason, CancellationToken cancellationToken) =>
        _store.RunInTransactionAsync(() => RejectLockedAsync(context, workOrderId, expectedRowVersion, decisionReason, cancellationToken), cancellationToken);

    private async Task<CommandResult<Guid>> RejectLockedAsync(
        CommandContext context, Guid workOrderId, byte[] expectedRowVersion, string? decisionReason, CancellationToken cancellationToken)
    {
        var workOrder = await _store.LoadForAcceptAsync(context.User, workOrderId, cancellationToken);
        if (workOrder is null)
        {
            return CommandError.NotFound;
        }

        if (!workOrder.RowVersion.AsSpan().SequenceEqual(expectedRowVersion))
        {
            return CommandError.ConcurrencyConflict;
        }

        if (!WorkOrderStatusTransitions.IsAllowed(workOrder.Status, WorkOrderStatus.CorrectiveActionRequired))
        {
            return CommandError.StateConflict(
                workOrder.Status == WorkOrderStatus.CorrectiveActionRequired
                    ? "This Work Order was already rejected."
                    : "Only a Work Order awaiting customer acceptance can be rejected.");
        }

        // ACC-007 "Required REJECT" (docs/02 p.4; UC-WO-022 precondition "reason required").
        var trimmedReason = decisionReason?.Trim() ?? string.Empty;
        if (trimmedReason.Length == 0 || trimmedReason.Length > CustomerAcceptance.DecisionReasonMaxLength)
        {
            return CommandError.Validation(RejectFields.DecisionReason, $"A reason of 1 to {CustomerAcceptance.DecisionReasonMaxLength} characters is required.");
        }

        var now = WorkOrderScheduleService.ToUtc(_clock.GetUtcNow())!.Value;
        var roundNo = await _store.NextAcceptanceRoundNoAsync(workOrder.Id, cancellationToken);
        var acceptance = CustomerAcceptance.Reject(workOrder.TenantId, workOrder.Id, roundNo, workOrder.AcceptanceContactId!.Value, trimmedReason, now);
        _store.Add(acceptance);

        // acceptance.Id is only populated once tracked (client-side sequential-GUID key generation on Add) —
        // the same ordering WorkSummaryService relies on for summary.Id before its own audit call.
        var cycleNo = await _store.NextCorrectiveActionCycleNoAsync(workOrder.Id, cancellationToken);
        var correctiveAction = CorrectiveAction.CreateDraft(workOrder.TenantId, workOrder.Id, acceptance.Id, cycleNo);
        _store.Add(correctiveAction);
        workOrder.Reject();
        _store.AddAudit(WorkOrderAudit.Rejected(context, workOrder, acceptance, correctiveAction, trimmedReason, now));

        var outcome = await _store.SaveChangesAsync(workOrder, expectedRowVersion, cancellationToken);
        return outcome == WorkOrderSaveOutcome.Saved ? CommandResult<Guid>.Success(workOrder.Id) : CommandError.ConcurrencyConflict;
    }
}
