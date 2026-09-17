using RepairRequest.Application.Approvals;
using RepairRequest.Application.Common;
using RepairRequest.Domain.Approvals;
using RepairRequest.Domain.RepairRequests;

namespace RepairRequest.Application.Tests.RepairRequests;

/// <summary>
/// In-memory post-Submit router. By default it records a ROUTE_NOT_FOUND routing failure (the request stays SUBMITTED with
/// an unchanged row version); tests can return another result or throw. SQL routing is covered by integration tests.
/// </summary>
internal sealed class FakeSubmittedRequestRouter : ISubmittedRequestRouter
{
    public List<Guid> RoutedRequestIds { get; } = [];

    public Func<Guid, byte[], CommandResult<RoutingResultDto>>? Result { get; set; }

    public Exception? Throw { get; set; }

    public int DiscardCalls { get; private set; }

    public Task<CommandResult<RoutingResultDto>> RouteSubmittedAsync(
        CommandContext context,
        Guid repairRequestId,
        byte[] submittedRowVersion,
        CancellationToken cancellationToken)
    {
        RoutedRequestIds.Add(repairRequestId);

        if (Throw is not null)
        {
            throw Throw;
        }

        return Task.FromResult(
            Result?.Invoke(repairRequestId, submittedRowVersion)
            ?? CommandResult<RoutingResultDto>.Success(
                new RoutingResultDto(repairRequestId, RepairRequestStatus.Submitted, RoutingFailureCodes.RouteNotFound, submittedRowVersion)));
    }

    public void DiscardPendingChanges() => DiscardCalls++;
}
