using RepairRequest.Application.Common;
using RepairRequest.Application.RepairRequests;
using RepairRequest.Domain.Approvals;
using RepairRequest.Domain.RepairRequests;
using RepairRequest.Domain.Security;
using RepairRequestAggregate = RepairRequest.Domain.RepairRequests.RepairRequest;

namespace RepairRequest.Application.Approvals;

/// <summary>
/// RR-API-006 Approve (ST-RR-004) and RR-API-007 Reject (ST-RR-005) by the assigned approver (S1-008). One decision runs in
/// one transaction with the request's review-workflow lock held, and the checks run in this deterministic order:
/// <list type="number">
/// <item>APPROVER role, and the request within the caller's S1-003 Repair Request scope (404 otherwise);</item>
/// <item>no decision on the caller's own request, even with an assignment (403; DEC-PRE-S1-008-01);</item>
/// <item>row version (409 CONCURRENCY_CONFLICT);</item>
/// <item>UNDER_REVIEW only: SUBMITTED has no assigned approver (409 STATE_CONFLICT; DEC-PRE-S1-008-03);</item>
/// <item>the caller is the assigned approver of the pending step (403; S1-007R assignment);</item>
/// <item>Reject only: a reason, trimmed, 1..1000 characters (422).</item>
/// </list>
/// Success decides the approval step, moves the request to APPROVED or REJECTED (with reject_reason) and writes one audit
/// record with the approver as actor. Nothing is written on any failure. Decision notifications, the SLA stop on Reject,
/// Return for Correction and Work Order conversion are out of scope.
/// </summary>
public sealed class RepairRequestDecisionService
{
    private readonly IRepairRequestDecisionStore _store;
    private readonly TimeProvider _clock;

    public RepairRequestDecisionService(IRepairRequestDecisionStore store, TimeProvider clock)
    {
        _store = store;
        _clock = clock;
    }

    public Task<CommandResult<RepairRequestDraftDto>> ApproveAsync(
        CommandContext context,
        Guid repairRequestId,
        byte[] expectedRowVersion,
        CancellationToken cancellationToken) =>
        DecideAsync(context, repairRequestId, expectedRowVersion, approve: true, reason: null, cancellationToken);

    public Task<CommandResult<RepairRequestDraftDto>> RejectAsync(
        CommandContext context,
        Guid repairRequestId,
        byte[] expectedRowVersion,
        string? reason,
        CancellationToken cancellationToken) =>
        DecideAsync(context, repairRequestId, expectedRowVersion, approve: false, reason, cancellationToken);

    private Task<CommandResult<RepairRequestDraftDto>> DecideAsync(
        CommandContext context,
        Guid repairRequestId,
        byte[] expectedRowVersion,
        bool approve,
        string? reason,
        CancellationToken cancellationToken)
    {
        // The API policy already requires APPROVER; the rule is re-checked here so no other caller can decide.
        if (!context.User.IsInRole(RoleCodes.Approver))
        {
            return Task.FromResult<CommandResult<RepairRequestDraftDto>>(CommandError.AccessDenied);
        }

        return _store.RunInTransactionAsync(
            () => DecideLockedAsync(context, repairRequestId, expectedRowVersion, approve, reason, cancellationToken),
            cancellationToken);
    }

    private async Task<CommandResult<RepairRequestDraftDto>> DecideLockedAsync(
        CommandContext context,
        Guid repairRequestId,
        byte[] expectedRowVersion,
        bool approve,
        string? reason,
        CancellationToken cancellationToken)
    {
        var callerId = context.User.UserId;
        var request = await _store.LockForDecisionAsync(context.User, repairRequestId, cancellationToken);
        if (request is null)
        {
            return CommandError.NotFound;
        }

        // Segregation of duties, independent of routing having excluded the creator (DEC-PRE-S1-008-01).
        if (request.CreatedBy == callerId)
        {
            return CommandError.AccessDenied;
        }

        if (!request.RowVersion.AsSpan().SequenceEqual(expectedRowVersion))
        {
            return CommandError.ConcurrencyConflict;
        }

        if (request.Status != RepairRequestStatus.UnderReview)
        {
            return CommandError.StateConflict("Only an UNDER_REVIEW Repair Request can be approved or rejected.");
        }

        var approval = await _store.FindStepApprovalAsync(request.TenantId, request.Id, ApprovalRouteStep.FirstStepNo, cancellationToken);
        if (approval is null || !approval.IsAssignedPending || approval.AssignedApproverId != callerId)
        {
            return CommandError.AccessDenied;
        }

        var rejectReason = approve ? null : RejectReason(reason);
        if (!approve && rejectReason is null)
        {
            return CommandError.Validation(
                RepairRequestFields.Reason,
                $"A reason of 1 to {RepairRequestAggregate.ReasonMaxLength} characters is required to reject.");
        }

        var now = UtcNow();
        if (approve)
        {
            approval.Approve(now);
            request.Approve();
            _store.AddAudit(RepairRequestAudit.Approved(context, request, approval, now));
        }
        else
        {
            approval.Reject(rejectReason!, now);
            request.Reject(rejectReason!);
            _store.AddAudit(RepairRequestAudit.Rejected(context, request, approval, now));
        }

        var outcome = await _store.SaveChangesAsync(request, expectedRowVersion, cancellationToken);

        return outcome == RepairRequestSaveOutcome.Saved
            ? CommandResult<RepairRequestDraftDto>.Success(RepairRequestDraftDto.From(request))
            : CommandError.ConcurrencyConflict;
    }

    /// <summary>The trimmed reason, or null when it is missing, blank or longer than RR-DD-001 RR-016 allows.</summary>
    private static string? RejectReason(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) || trimmed.Length > RepairRequestAggregate.ReasonMaxLength ? null : trimmed;
    }

    /// <summary>UTC truncated to the datetime2(3) precision used by persisted timestamps.</summary>
    private DateTime UtcNow()
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        return now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMillisecond));
    }
}
