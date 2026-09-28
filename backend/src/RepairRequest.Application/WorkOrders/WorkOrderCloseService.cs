using RepairRequest.Application.Common;
using RepairRequest.Domain.WorkOrders;

namespace RepairRequest.Application.WorkOrders;

/// <summary>
/// Work Order Close (ST-WO-006; WO-API-010; BR-08; `docs/13` §4.20) — Supervisor only, within the caller's Site
/// scope. Runs in one transaction and checks in the same deterministic order as every other command in this
/// codebase: scope (404), the Work Order's own RowVersion (409 CONCURRENCY_CONFLICT), then every guard (409
/// STATE_CONFLICT, each with its own message): COMPLETED; a current Work Summary; that Work Summary reviewed;
/// the customer's ACCEPT on the current submission; a reviewed Cost Summary. Nothing is written on any failure.
/// Success moves the Work Order to CLOSED (server-derived <c>closed_by</c>/<c>closed_at</c>) and writes one
/// WORK_ORDER_CLOSED audit row in the same save. Q1: no SLA stop (no <c>sla_record</c> exists); Q5: no extra
/// Separation-of-Duties / Visit / Work Session / Corrective Action guard.
/// </summary>
public sealed class WorkOrderCloseService
{
    private readonly IWorkOrderCloseStore _store;
    private readonly TimeProvider _clock;

    public WorkOrderCloseService(IWorkOrderCloseStore store, TimeProvider clock)
    {
        _store = store;
        _clock = clock;
    }

    public Task<CommandResult<Guid>> CloseAsync(
        CommandContext context, Guid workOrderId, byte[] expectedRowVersion, CancellationToken cancellationToken) =>
        _store.RunInTransactionAsync(() => CloseLockedAsync(context, workOrderId, expectedRowVersion, cancellationToken), cancellationToken);

    private async Task<CommandResult<Guid>> CloseLockedAsync(
        CommandContext context, Guid workOrderId, byte[] expectedRowVersion, CancellationToken cancellationToken)
    {
        var workOrder = await _store.LoadInScopeAsync(context.User, workOrderId, cancellationToken);
        if (workOrder is null)
        {
            return CommandError.NotFound;
        }

        if (!workOrder.RowVersion.AsSpan().SequenceEqual(expectedRowVersion))
        {
            return CommandError.ConcurrencyConflict;
        }

        if (!WorkOrderStatusTransitions.IsAllowed(workOrder.Status, WorkOrderStatus.Closed))
        {
            return CommandError.StateConflict(
                workOrder.Status == WorkOrderStatus.Closed
                    ? "This Work Order was already closed."
                    : "Only a COMPLETED Work Order can be closed.");
        }

        var evidence = await _store.GetCloseEvidenceAsync(workOrder.Id, cancellationToken);

        if (!evidence.HasWorkSummary)
        {
            return CommandError.StateConflict("This Work Order has no Work Summary, so it cannot be closed.");
        }

        // No review flag exists for the Work Summary. In this codebase WO-008 is assigned by
        // WorkOrder.SubmitForAcceptance (ST-WO-004, whose baseline guard is "Summary reviewed"), so a non-null
        // value is used as the evidence that step ran (docs/13 §4.20 Q2).
        if (workOrder.AcceptanceContactId is null)
        {
            return CommandError.StateConflict("This Work Order's Work Summary has not been reviewed and submitted for acceptance, so it cannot be closed.");
        }

        if (evidence.LatestAcceptanceDecision != AcceptanceDecision.Accept)
        {
            return CommandError.StateConflict("The customer has not accepted this Work Order's current Work Summary, so it cannot be closed.");
        }

        if (evidence.CostSummaryId is null)
        {
            return CommandError.StateConflict("This Work Order has no Cost Summary, so it cannot be closed. A reviewed Cost Summary is required.");
        }

        if (evidence.CostSummaryReviewedAt is null)
        {
            return CommandError.StateConflict("This Work Order's Cost Summary has not been reviewed yet, so it cannot be closed.");
        }

        var now = WorkOrderScheduleService.ToUtc(_clock.GetUtcNow())!.Value;
        workOrder.Close(context.User.UserId, now);
        _store.AddAudit(WorkOrderAudit.Closed(context, workOrder, evidence.CostSummaryId.Value, now));

        var outcome = await _store.SaveChangesAsync(workOrder, expectedRowVersion, cancellationToken);
        return outcome == WorkOrderSaveOutcome.Saved ? CommandResult<Guid>.Success(workOrder.Id) : CommandError.ConcurrencyConflict;
    }
}
