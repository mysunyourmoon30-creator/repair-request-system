using RepairRequest.Application.Common;
using RepairRequest.Domain.WorkOrders;

namespace RepairRequest.Application.WorkOrders;

/// <summary>
/// Work Order Cancel (ST-WO-011; WO-API-011; UC-WO-026; BR-09/BR-12; `docs/13` §4.25) — Supervisor only, within
/// the caller's Site scope. Runs in one transaction and checks in the same deterministic order as every other
/// command in this codebase: scope (404), the Work Order's own RowVersion (409 CONCURRENCY_CONFLICT), the
/// non-terminal/pre-Accept state guard (409 STATE_CONFLICT), the "no Service Visit currently IN_PROGRESS" guard
/// (409 STATE_CONFLICT — `docs/13` §4.25 Decision: deny, unchanged, rather than force-terminate an active Work
/// Session), then the reason (422). Nothing is written on any failure. Success moves the Work Order to CANCELLED
/// with the required reason, cascades Cancel to every still-SCHEDULED Service Visit (reusing <see
/// cref="ServiceVisit.Cancel"/>, ST-SV-007), and writes one WORK_ORDER_CANCELLED audit row plus one
/// SERVICE_VISIT_CANCELLED row per cascaded Visit — all in the same save. COMPLETED/MISSED/CANCELLED Visits are
/// left untouched (history preserved, per UC-WO-026's own Main Flow). A Corrective Action row in flight
/// (DRAFT/PENDING_PLAN_APPROVAL/APPROVED) is likewise left untouched — `docs/13` §4.25 Decision: no new state,
/// no retroactive edit; it becomes inert history once the Work Order is terminal. No SLA stop (no `sla_record`
/// subsystem exists yet) and no NTF-CANCEL outbox event — both explicit, out-of-scope gaps, not implemented here.
/// </summary>
public sealed class WorkOrderCancelService
{
    private readonly IWorkOrderCancelStore _store;
    private readonly TimeProvider _clock;

    public WorkOrderCancelService(IWorkOrderCancelStore store, TimeProvider clock)
    {
        _store = store;
        _clock = clock;
    }

    public Task<CommandResult<Guid>> CancelAsync(
        CommandContext context, Guid workOrderId, byte[] expectedRowVersion, string? reason, CancellationToken cancellationToken) =>
        _store.RunInTransactionAsync(() => CancelLockedAsync(context, workOrderId, expectedRowVersion, reason, cancellationToken), cancellationToken);

    private async Task<CommandResult<Guid>> CancelLockedAsync(
        CommandContext context, Guid workOrderId, byte[] expectedRowVersion, string? reason, CancellationToken cancellationToken)
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

        var fromState = workOrder.Status;
        if (!WorkOrderStatusTransitions.IsAllowed(fromState, WorkOrderStatus.Cancelled))
        {
            return CommandError.StateConflict(
                fromState == WorkOrderStatus.Cancelled
                    ? "This Work Order was already cancelled."
                    : "This Work Order can no longer be cancelled: the customer has already accepted, or it is completed or closed.");
        }

        var visits = await _store.GetServiceVisitsAsync(workOrder.Id, cancellationToken);
        if (visits.Any(visit => visit.Status == ServiceVisitStatus.InProgress))
        {
            return CommandError.StateConflict(
                "This Work Order has a Service Visit in progress. Check out the active session before cancelling.");
        }

        var trimmed = reason?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > WorkOrder.ReasonMaxLength)
        {
            return CommandError.Validation(
                WorkOrderCancelFields.Reason,
                $"A reason of 1 to {WorkOrder.ReasonMaxLength} characters is required to cancel.");
        }

        var now = WorkOrderScheduleService.ToUtc(_clock.GetUtcNow())!.Value;
        workOrder.Cancel(trimmed);
        _store.AddAudit(WorkOrderAudit.Cancelled(context, workOrder, fromState, now));

        foreach (var visit in visits.Where(visit => visit.Status == ServiceVisitStatus.Scheduled))
        {
            visit.Cancel(trimmed);
            _store.AddAudit(WorkOrderAudit.Cancelled(context, visit, now));
        }

        var outcome = await _store.SaveChangesAsync(workOrder, expectedRowVersion, cancellationToken);
        return outcome == WorkOrderSaveOutcome.Saved ? CommandResult<Guid>.Success(workOrder.Id) : CommandError.ConcurrencyConflict;
    }
}
