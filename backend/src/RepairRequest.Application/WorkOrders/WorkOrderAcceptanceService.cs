using RepairRequest.Application.Common;
using RepairRequest.Application.Security;
using RepairRequest.Domain.WorkOrders;

namespace RepairRequest.Application.WorkOrders;

/// <summary>
/// Customer Accept (ST-WO-005; UC-WO-021; the exact designated Acceptance Contact only), per `docs/13` §4.16.
/// Runs in one transaction, checks in the same deterministic order as every other command in this codebase:
/// scope (404 — resource-specific identity + tenant + Site, not a role-scoped query), row version against the
/// Work Order's own RowVersion (409 CONCURRENCY_CONFLICT), the right source state (409 STATE_CONFLICT). No
/// field validation — Accept has no body. Success sets the Work Order to COMPLETED, all server-derived, one
/// save, one audit row.
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
        workOrder.Accept();

        _store.AddAudit(WorkOrderAudit.Accepted(context, workOrder, now));

        var outcome = await _store.SaveChangesAsync(workOrder, expectedRowVersion, cancellationToken);
        return outcome == WorkOrderSaveOutcome.Saved ? CommandResult<Guid>.Success(workOrder.Id) : CommandError.ConcurrencyConflict;
    }
}
