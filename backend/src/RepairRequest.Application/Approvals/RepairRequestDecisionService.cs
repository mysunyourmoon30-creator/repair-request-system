using RepairRequest.Application.Common;
using RepairRequest.Application.RepairRequests;
using RepairRequest.Domain.Approvals;
using RepairRequest.Domain.RepairRequests;
using RepairRequest.Domain.Security;
using RepairRequestAggregate = RepairRequest.Domain.RepairRequests.RepairRequest;

namespace RepairRequest.Application.Approvals;

/// <summary>
/// RR-API-006 Approve (ST-RR-004), RR-API-007 Reject (ST-RR-005; S1-008) and RR-API-008 Return for Correction (ST-RR-006;
/// S1-010) by the assigned approver of the current approval cycle. One decision runs in
/// one transaction with the request's review-workflow lock held, and the checks run in this deterministic order:
/// <list type="number">
/// <item>APPROVER role, and the request within the caller's S1-003 Repair Request scope (404 otherwise);</item>
/// <item>no decision on the caller's own request, even with an assignment (403; DEC-PRE-S1-008-01);</item>
/// <item>row version (409 CONCURRENCY_CONFLICT);</item>
/// <item>UNDER_REVIEW only: SUBMITTED has no assigned approver (409 STATE_CONFLICT; DEC-PRE-S1-008-03, DEC-PRE-S1-010-03);</item>
/// <item>the caller is the assigned approver of the pending step (403; S1-007R assignment);</item>
/// <item>Reject and Return only: a reason, trimmed, 1..1000 characters (422).</item>
/// </list>
/// Success decides the approval step and moves the request to APPROVED, REJECTED (with reject_reason) or back to DRAFT
/// (Return: the reason is the step's decision_reason and the step is kept as history), and writes one audit record with
/// the approver as actor. Nothing is written on any failure. Decision notifications, the SLA stop on Reject and Work Order
/// conversion are out of scope.
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
        DecideAsync(context, repairRequestId, expectedRowVersion, DecisionKind.Approve, reason: null, cancellationToken);

    public Task<CommandResult<RepairRequestDraftDto>> RejectAsync(
        CommandContext context,
        Guid repairRequestId,
        byte[] expectedRowVersion,
        string? reason,
        CancellationToken cancellationToken) =>
        DecideAsync(context, repairRequestId, expectedRowVersion, DecisionKind.Reject, reason, cancellationToken);

    public Task<CommandResult<RepairRequestDraftDto>> ReturnForCorrectionAsync(
        CommandContext context,
        Guid repairRequestId,
        byte[] expectedRowVersion,
        string? reason,
        CancellationToken cancellationToken) =>
        DecideAsync(context, repairRequestId, expectedRowVersion, DecisionKind.ReturnForCorrection, reason, cancellationToken);

    private enum DecisionKind
    {
        Approve,
        Reject,
        ReturnForCorrection
    }

    private Task<CommandResult<RepairRequestDraftDto>> DecideAsync(
        CommandContext context,
        Guid repairRequestId,
        byte[] expectedRowVersion,
        DecisionKind kind,
        string? reason,
        CancellationToken cancellationToken)
    {
        // The API policy already requires APPROVER; the rule is re-checked here so no other caller can decide.
        if (!context.User.IsInRole(RoleCodes.Approver))
        {
            return Task.FromResult<CommandResult<RepairRequestDraftDto>>(CommandError.AccessDenied);
        }

        return _store.RunInTransactionAsync(
            () => DecideLockedAsync(context, repairRequestId, expectedRowVersion, kind, reason, cancellationToken),
            cancellationToken);
    }

    private async Task<CommandResult<RepairRequestDraftDto>> DecideLockedAsync(
        CommandContext context,
        Guid repairRequestId,
        byte[] expectedRowVersion,
        DecisionKind kind,
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
            return CommandError.StateConflict(kind == DecisionKind.ReturnForCorrection
                ? "Only an UNDER_REVIEW Repair Request can be returned for correction."
                : "Only an UNDER_REVIEW Repair Request can be approved or rejected.");
        }

        var approval = await _store.FindStepApprovalAsync(request.TenantId, request.Id, ApprovalRouteStep.FirstStepNo, cancellationToken);
        if (approval is null || !approval.IsAssignedPending || approval.AssignedApproverId != callerId)
        {
            return CommandError.AccessDenied;
        }

        var decisionReason = kind == DecisionKind.Approve ? null : DecisionReason(reason);
        if (kind != DecisionKind.Approve && decisionReason is null)
        {
            return CommandError.Validation(
                RepairRequestFields.Reason,
                kind == DecisionKind.Reject
                    ? $"A reason of 1 to {RepairRequestAggregate.ReasonMaxLength} characters is required to reject."
                    : $"A reason of 1 to {RepairRequestApproval.DecisionReasonMaxLength} characters is required to return for correction.");
        }

        var now = UtcNow();
        switch (kind)
        {
            case DecisionKind.Approve:
                approval.Approve(now);
                request.Approve();
                _store.AddAudit(RepairRequestAudit.Approved(context, request, approval, now));
                break;
            case DecisionKind.Reject:
                approval.Reject(decisionReason!, now);
                request.Reject(decisionReason!);
                _store.AddAudit(RepairRequestAudit.Rejected(context, request, approval, now));
                break;
            default:
                approval.ReturnForCorrection(decisionReason!, now);
                request.ReturnForCorrection();
                _store.AddAudit(RepairRequestAudit.ReturnedForCorrection(context, request, approval, now));
                break;
        }

        var outcome = await _store.SaveChangesAsync(request, expectedRowVersion, cancellationToken);

        return outcome == RepairRequestSaveOutcome.Saved
            ? CommandResult<RepairRequestDraftDto>.Success(RepairRequestDraftDto.From(request))
            : CommandError.ConcurrencyConflict;
    }

    /// <summary>
    /// The trimmed reason, or null when it is missing, blank or longer than RR-DD-001 RR-016 / APR-008 allow (both 1000).
    /// </summary>
    private static string? DecisionReason(string? value)
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
