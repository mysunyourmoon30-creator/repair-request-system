using RepairRequest.Application.Common;
using RepairRequest.Domain.RepairRequests;
using RepairRequest.Domain.WorkOrders;
using RepairRequestAggregate = RepairRequest.Domain.RepairRequests.RepairRequest;

namespace RepairRequest.Application.RepairRequests;

/// <summary>
/// ST-RR-008 Convert (UC-WO-001; BR-03) by a Coordinator (S2-002). One convert runs in one transaction, and the
/// checks run in this deterministic order:
/// <list type="number">
/// <item>the Repair Request is within the Coordinator's site scope (404 otherwise);</item>
/// <item>row version (409 CONCURRENCY_CONFLICT);</item>
/// <item>source state APPROVED; anything else, including an already-CONVERTED request, is denied
/// (409 STATE_CONFLICT) — this is also the "no duplicate Convert" guard, not a separate mechanism.</item>
/// </list>
/// Success creates exactly one Work Order (OPEN, with a freshly allocated Work Order No.), moves the request to
/// CONVERTED, and writes one audit record referencing the new Work Order. No reason applies (absent from BR-04's
/// list). Nothing is written on any failure, including a Work Order No./FK collision at save time.
/// </summary>
public sealed class RepairRequestConvertService
{
    private readonly IRepairRequestConvertStore _store;
    private readonly TimeProvider _clock;

    public RepairRequestConvertService(IRepairRequestConvertStore store, TimeProvider clock)
    {
        _store = store;
        _clock = clock;
    }

    public Task<CommandResult<Guid>> ConvertAsync(
        CommandContext context,
        Guid repairRequestId,
        byte[] expectedRowVersion,
        CancellationToken cancellationToken) =>
        _store.RunInTransactionAsync(
            () => ConvertLockedAsync(context, repairRequestId, expectedRowVersion, cancellationToken),
            cancellationToken);

    private async Task<CommandResult<Guid>> ConvertLockedAsync(
        CommandContext context,
        Guid repairRequestId,
        byte[] expectedRowVersion,
        CancellationToken cancellationToken)
    {
        var request = await _store.LoadForConvertAsync(context.User, repairRequestId, cancellationToken);
        if (request is null)
        {
            return CommandError.NotFound;
        }

        if (!request.RowVersion.AsSpan().SequenceEqual(expectedRowVersion))
        {
            return CommandError.ConcurrencyConflict;
        }

        if (!RepairRequestStatusTransitions.IsAllowed(request.Status, RepairRequestStatus.Converted))
        {
            return CommandError.StateConflict("Only an APPROVED Repair Request can be converted.");
        }

        var now = UtcNow();
        var sequence = await _store.AllocateWorkOrderNumberAsync(request.TenantId, now.Year, cancellationToken);
        var workOrder = WorkOrder.Create(request.TenantId, request.Id, WorkOrderNumber.Format(now.Year, sequence), now);

        request.Convert();
        _store.Add(workOrder);
        _store.AddAudit(RepairRequestAudit.Converted(context, request, workOrder, now));

        var outcome = await _store.SaveChangesAsync(request, expectedRowVersion, cancellationToken);

        return outcome == RepairRequestSaveOutcome.Saved
            ? CommandResult<Guid>.Success(workOrder.Id)
            : CommandError.ConcurrencyConflict;
    }

    /// <summary>UTC truncated to the datetime2(3) precision used by persisted timestamps.</summary>
    private DateTime UtcNow()
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        return now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMillisecond));
    }
}
