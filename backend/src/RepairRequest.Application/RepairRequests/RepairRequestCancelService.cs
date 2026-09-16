using RepairRequest.Application.Common;
using RepairRequest.Domain.RepairRequests;
using RepairRequestAggregate = RepairRequest.Domain.RepairRequests.RepairRequest;

namespace RepairRequest.Application.RepairRequests;

/// <summary>
/// RR-API-009 Cancel (ST-RR-007; UC-RR-004) by the owning Requester (S1-009). One cancel runs in one transaction with the
/// request's review-workflow lock held, and the checks run in this deterministic order:
/// <list type="number">
/// <item>the caller created the request and it is within the S1-003 Repair Request scope (404 otherwise);</item>
/// <item>row version (409 CONCURRENCY_CONFLICT);</item>
/// <item>source state DRAFT, SUBMITTED, UNDER_REVIEW or APPROVED; REJECTED, CANCELLED and CONVERTED are denied
/// (409 STATE_CONFLICT);</item>
/// <item>a reason, trimmed, 1..1000 characters (422).</item>
/// </list>
/// Success moves the request to CANCELLED with cancel_reason and writes one audit record with the Requester as actor.
/// Approval rows are left unchanged (DEC-PRE-S1-009-01): a pending step becomes inert because Approve/Reject require
/// UNDER_REVIEW. Nothing is written on any failure. The SLA stop (EV-SLA-005) and NTF-CANCEL are out of scope.
/// </summary>
public sealed class RepairRequestCancelService
{
    private readonly IRepairRequestCancelStore _store;
    private readonly TimeProvider _clock;

    public RepairRequestCancelService(IRepairRequestCancelStore store, TimeProvider clock)
    {
        _store = store;
        _clock = clock;
    }

    public Task<CommandResult<RepairRequestDraftDto>> CancelAsync(
        CommandContext context,
        Guid repairRequestId,
        byte[] expectedRowVersion,
        string? reason,
        CancellationToken cancellationToken) =>
        _store.RunInTransactionAsync(
            () => CancelLockedAsync(context, repairRequestId, expectedRowVersion, reason, cancellationToken),
            cancellationToken);

    private async Task<CommandResult<RepairRequestDraftDto>> CancelLockedAsync(
        CommandContext context,
        Guid repairRequestId,
        byte[] expectedRowVersion,
        string? reason,
        CancellationToken cancellationToken)
    {
        // Ownership and business scope are part of the lookup: another user's or an out-of-scope request is a 404.
        var request = await _store.LockOwnForCancelAsync(context.User, repairRequestId, cancellationToken);
        if (request is null)
        {
            return CommandError.NotFound;
        }

        if (!request.RowVersion.AsSpan().SequenceEqual(expectedRowVersion))
        {
            return CommandError.ConcurrencyConflict;
        }

        var fromState = request.Status;
        if (!RepairRequestStatusTransitions.IsAllowed(fromState, RepairRequestStatus.Cancelled))
        {
            return CommandError.StateConflict("Only a DRAFT, SUBMITTED, UNDER_REVIEW or APPROVED Repair Request can be cancelled.");
        }

        var trimmed = reason?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > RepairRequestAggregate.ReasonMaxLength)
        {
            return CommandError.Validation(
                RepairRequestFields.Reason,
                $"A reason of 1 to {RepairRequestAggregate.ReasonMaxLength} characters is required to cancel.");
        }

        var now = UtcNow();
        request.Cancel(trimmed);
        _store.AddAudit(RepairRequestAudit.Cancelled(context, request, fromState, now));

        var outcome = await _store.SaveChangesAsync(request, expectedRowVersion, cancellationToken);

        return outcome == RepairRequestSaveOutcome.Saved
            ? CommandResult<RepairRequestDraftDto>.Success(RepairRequestDraftDto.From(request))
            : CommandError.ConcurrencyConflict;
    }

    /// <summary>UTC truncated to the datetime2(3) precision used by persisted timestamps.</summary>
    private DateTime UtcNow()
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        return now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMillisecond));
    }
}
