using RepairRequest.Application.Common;
using RepairRequest.Application.RepairRequests;
using RepairRequest.Application.Security;
using RepairRequest.Domain.Approvals;
using RepairRequest.Domain.RepairRequests;
using RepairRequestAggregate = RepairRequest.Domain.RepairRequests.RepairRequest;

namespace RepairRequest.Application.Approvals;

/// <summary>
/// ST-RR-003 Route for Review by System (S1-007R; DEC-PRE-S1-007R-01..10). One routing attempt, in one transaction with
/// the request row locked:
/// <list type="number">
/// <item>tenant lookup (404) -> row version (409) -> SUBMITTED (409) -> no assigned PENDING approval (409);</item>
/// <item>route selection and approver resolution by <see cref="ApprovalRoutingRules"/>, re-run identically on retry;</item>
/// <item>success: create or update the approval row with the assigned approver, SUBMITTED -> UNDER_REVIEW, ROUTED audit;</item>
/// <item>failure: the request stays SUBMITTED; an approval row with the failure code only when a route and step were
/// resolved; ROUTING_FAILED audit with the code. Request No and submitted_at never change.</item>
/// </list>
/// The caller never chooses a route or an approver. Configuration or recovery rights never grant Approve/Reject.
/// </summary>
public sealed class RepairRequestRoutingService : ISubmittedRequestRouter
{
    private readonly IApprovalRoutingStore _store;
    private readonly TimeProvider _clock;

    public RepairRequestRoutingService(IApprovalRoutingStore store, TimeProvider clock)
    {
        _store = store;
        _clock = clock;
    }

    /// <summary>ADMINISTRATOR routing-issue list (DEC-PRE-S1-007R-10).</summary>
    public Task<PagedResult<RoutingIssueDto>> ListIssuesAsync(CurrentUser user, PageRequest paging, CancellationToken cancellationToken) =>
        _store.ListRoutingIssuesAsync(user, paging, cancellationToken);

    /// <summary>
    /// ADMINISTRATOR Retry Routing (DEC-PRE-S1-007R-07/08) for a SUBMITTED request with an unresolved routing failure or
    /// one that was never routed. The API policy restricts the caller; a caller without tenant-wide configuration scope
    /// is additionally treated as not found.
    /// </summary>
    public Task<CommandResult<RoutingResultDto>> RetryAsync(
        CommandContext context,
        Guid repairRequestId,
        byte[] expectedRowVersion,
        CancellationToken cancellationToken)
    {
        if (!context.User.HasTenantWideConfigurationScope)
        {
            return Task.FromResult<CommandResult<RoutingResultDto>>(CommandError.NotFound);
        }

        return _store.RunInTransactionAsync(
            () => RouteAsync(context, repairRequestId, expectedRowVersion, RoutingTrigger.AdminRetry, cancellationToken),
            cancellationToken);
    }

    /// <summary>Routing right after the Submit commit, in its own transaction (DEC-PRE-S1-007R-01).</summary>
    public Task<CommandResult<RoutingResultDto>> RouteSubmittedAsync(
        CommandContext context,
        Guid repairRequestId,
        byte[] submittedRowVersion,
        CancellationToken cancellationToken) =>
        _store.RunInTransactionAsync(
            () => RouteAsync(context, repairRequestId, submittedRowVersion, RoutingTrigger.Submit, cancellationToken),
            cancellationToken);

    /// <summary>Drops unsaved routing changes after an unexpected failure so the committed Submit result can be read back.</summary>
    public void DiscardPendingChanges() => _store.DiscardPendingChanges();

    private async Task<CommandResult<RoutingResultDto>> RouteAsync(
        CommandContext context,
        Guid repairRequestId,
        byte[] expectedRowVersion,
        RoutingTrigger trigger,
        CancellationToken cancellationToken)
    {
        var tenantId = context.User.TenantId;
        var request = await _store.LockRequestAsync(tenantId, repairRequestId, cancellationToken);
        if (request is null)
        {
            return CommandError.NotFound;
        }

        if (!request.RowVersion.AsSpan().SequenceEqual(expectedRowVersion))
        {
            return CommandError.ConcurrencyConflict;
        }

        if (request.Status != RepairRequestStatus.Submitted || request.SiteId is null || request.RequestCategoryCode is null)
        {
            return CommandError.StateConflict("Only a SUBMITTED Repair Request can be routed.");
        }

        // The current approval cycle (DEC-PRE-S1-010-01): a step returned for correction is history, so a resubmitted
        // request is routed into the next cycle; an unassigned failure row of the current cycle is routed again in place.
        var existing = await _store.FindStepApprovalAsync(tenantId, request.Id, ApprovalRouteStep.FirstStepNo, cancellationToken);
        var cycleNo = RepairRequestApproval.FirstCycleNo;
        if (existing is not null)
        {
            if (existing.Status == ApprovalStatus.ReturnedForCorrection)
            {
                cycleNo = checked((short)(existing.ApprovalCycleNo + 1));
                existing = null;
            }
            else if (existing.Status != ApprovalStatus.Pending || existing.AssignedApproverId is not null)
            {
                return CommandError.StateConflict("The Repair Request already has an assigned approval.");
            }
            else
            {
                cycleNo = existing.ApprovalCycleNo;
            }
        }

        var decision = await DecideAsync(tenantId, request, cancellationToken);
        var now = UtcNow();

        if (decision.AssignedApproverId is { } approverId)
        {
            if (existing is null)
            {
                _store.AddApproval(RepairRequestApproval.Assigned(
                    tenantId, request.Id, cycleNo, decision.RouteId!.Value, decision.StepNo!.Value, approverId, now));
            }
            else
            {
                existing.AssignOnRetry(decision.RouteId!.Value, decision.StepNo!.Value, approverId, now);
            }

            request.RouteForReview();
            _store.AddAudit(RepairRequestRoutingAudit.Routed(context, request, decision, cycleNo, trigger, now));
        }
        else
        {
            // Route and step resolved but no approver: keep a failure row. No route or step: audit only, and a failure row left
            // by an earlier approver-level failure is removed so the current state never contradicts the latest routing audit
            // (DEC-PRE-S1-007R-09). The earlier failure stays in the append-only audit history.
            if (decision.RouteId is { } routeId && decision.StepNo is { } stepNo)
            {
                if (existing is null)
                {
                    _store.AddApproval(RepairRequestApproval.AssignmentFailed(tenantId, request.Id, cycleNo, routeId, stepNo, decision.FailureCode!));
                }
                else
                {
                    existing.FailOnRetry(routeId, stepNo, decision.FailureCode!);
                }
            }
            else if (existing is not null)
            {
                existing.EnsureDiscardableRoutingFailure();
                _store.RemoveApproval(existing);
            }

            _store.AddAudit(RepairRequestRoutingAudit.RoutingFailed(context, request, decision, cycleNo, trigger, now));
        }

        var outcome = await _store.SaveChangesAsync(request, expectedRowVersion, cancellationToken);

        return outcome == RepairRequestSaveOutcome.Saved
            ? CommandResult<RoutingResultDto>.Success(new RoutingResultDto(request.Id, request.Status, decision.FailureCode, request.RowVersion))
            : CommandError.ConcurrencyConflict;
    }

    private async Task<RoutingDecision> DecideAsync(Guid tenantId, RepairRequestAggregate request, CancellationToken cancellationToken)
    {
        var siteId = request.SiteId!.Value;
        var routes = await _store.ListActiveRoutesAsync(tenantId, request.RequestCategoryCode!, siteId, cancellationToken);

        var selection = ApprovalRoutingRules.SelectRoute(routes);
        if (selection.FailureCode is { } routeFailure)
        {
            return RoutingDecision.RouteFailure(routeFailure);
        }

        var routeId = selection.RouteId!.Value;
        var step = selection.Step!;

        if (step.ApproverUserId is { } approverUserId)
        {
            var eligible = approverUserId != request.CreatedBy
                           && await _store.IsEligibleApproverAsync(tenantId, approverUserId, siteId, cancellationToken);
            return ApprovalRoutingRules.ResolveSpecificApprover(routeId, step, request.CreatedBy, eligible);
        }

        var candidates = await _store.FindEligibleApproversAsync(
            tenantId, siteId, request.CreatedBy, ApprovalRoutingRules.CandidateProbeSize, cancellationToken);
        return ApprovalRoutingRules.ResolveRolePool(routeId, step, request.CreatedBy, candidates);
    }

    /// <summary>UTC truncated to the datetime2(3) precision used by persisted timestamps.</summary>
    private DateTime UtcNow()
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        return now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMillisecond));
    }
}
