using RepairRequest.Application.Common;

namespace RepairRequest.Application.Approvals;

/// <summary>
/// Routing step invoked by Submit right after its commit (DEC-PRE-S1-007R-01). Implemented by
/// <see cref="RepairRequestRoutingService"/>; a separate port keeps Submit testable without a routing store.
/// </summary>
public interface ISubmittedRequestRouter
{
    /// <summary>Runs one routing attempt in its own transaction for a request that was just submitted.</summary>
    Task<CommandResult<RoutingResultDto>> RouteSubmittedAsync(
        CommandContext context,
        Guid repairRequestId,
        byte[] submittedRowVersion,
        CancellationToken cancellationToken);

    /// <summary>Drops unsaved routing changes after an unexpected failure.</summary>
    void DiscardPendingChanges();
}
