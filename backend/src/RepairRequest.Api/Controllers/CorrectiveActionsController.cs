using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RepairRequest.Api.Contracts.WorkOrders;
using RepairRequest.Application.Security;
using RepairRequest.Application.WorkOrders;

namespace RepairRequest.Api.Controllers;

/// <summary>
/// Corrective Action endpoints (`docs/13` §4.21 Ticket 6; §4.22 CA-API-003). Distinct route root from
/// <see cref="WorkOrdersController"/> because the baseline's own catalog (`docs/09`) names these resources
/// `/api/v1/corrective-actions/{id}/...`, not `/api/v1/work-orders/{id}/...` — the Corrective Action is the
/// resource acted upon. Submit Plan/Approve Plan advance the linked Work Order's own status (ST-WO-008/009) and
/// use its RowVersion as the concurrency token; Schedule Rework advances neither and uses the Corrective Action's
/// own RowVersion instead (see its own remarks).
/// </summary>
[ApiController]
[Route("api/v1/corrective-actions")]
public sealed class CorrectiveActionsController : CommandControllerBase
{
    private const string ResourceType = "CorrectiveAction";

    private readonly CorrectiveActionService _correctiveActions;

    public CorrectiveActionsController(CorrectiveActionService correctiveActions, ICurrentUserAccessor currentUserAccessor)
        : base(currentUserAccessor)
    {
        _correctiveActions = correctiveActions;
    }

    /// <summary>
    /// CA-API-001 Submit Plan (ST-CA-002; Team Lead only; `docs/13` §4.21): only from a DRAFT Corrective Action.
    /// If-Match is checked against the linked Work Order's own RowVersion. Binds <c>owner_team_lead_id</c> to the
    /// caller atomically with the state transition.
    /// </summary>
    [HttpPost("{correctiveActionId:guid}/submit-plan")]
    [Authorize(Policy = AuthorizationPolicies.CorrectiveActionSubmitPlan)]
    [ProducesResponseType<CorrectiveActionResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> SubmitPlan(Guid correctiveActionId, [FromBody] SubmitCorrectivePlanRequest request, CancellationToken cancellationToken)
    {
        if (!TryReadIfMatch(out var rowVersion, out var problem))
        {
            return problem;
        }

        var context = await CommandContextAsync(cancellationToken);
        var result = await _correctiveActions.SubmitPlanAsync(context, correctiveActionId, rowVersion, request.PlanText, request.PlanFileAssetId, cancellationToken);
        return CommandResult(result, ResourceType, CorrectiveActionResponses.ToResponse, dto => dto.WorkOrderRowVersion);
    }

    /// <summary>
    /// CA-API-002 Approve Plan (ST-CA-003; Supervisor only; `docs/13` §4.21): only from a Corrective Action whose
    /// plan was submitted. If-Match is checked against the linked Work Order's own RowVersion. Empty request body.
    /// No Separation of Duties (§4.21 Decision d — recorded review risk, not a baseline requirement).
    /// </summary>
    [HttpPost("{correctiveActionId:guid}/approve-plan")]
    [Authorize(Policy = AuthorizationPolicies.CorrectiveActionApprovePlan)]
    [ProducesResponseType<CorrectiveActionResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ApprovePlan(Guid correctiveActionId, CancellationToken cancellationToken)
    {
        if (!TryReadIfMatch(out var rowVersion, out var problem))
        {
            return problem;
        }

        var context = await CommandContextAsync(cancellationToken);
        var result = await _correctiveActions.ApprovePlanAsync(context, correctiveActionId, rowVersion, cancellationToken);
        return CommandResult(result, ResourceType, CorrectiveActionResponses.ToResponse, dto => dto.WorkOrderRowVersion);
    }

    /// <summary>
    /// CA-API-003 Schedule Rework (Coordinator only; `docs/13` §4.22 — Portfolio Project Owner directive, not a
    /// baseline-literal actor): only from an APPROVED Corrective Action with no rework scheduled yet. If-Match is
    /// checked against the Corrective Action's own RowVersion — not the Work Order's, since this action changes
    /// neither the Corrective Action's nor the Work Order's Status. Creates and links one SCHEDULED corrective
    /// Service Visit atomically; the Work Order itself stays CORRECTIVE_PLAN_APPROVED (ST-WO-010 "Start Rework" is
    /// a separate, later, Technician-driven transition this ticket does not implement).
    /// </summary>
    [HttpPost("{correctiveActionId:guid}/schedule-rework")]
    [Authorize(Policy = AuthorizationPolicies.CorrectiveActionScheduleRework)]
    [ProducesResponseType<CorrectiveActionResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ScheduleRework(Guid correctiveActionId, [FromBody] NewVisitScheduleRequest request, CancellationToken cancellationToken)
    {
        if (!TryReadIfMatch(out var rowVersion, out var problem))
        {
            return problem;
        }

        var context = await CommandContextAsync(cancellationToken);
        var result = await _correctiveActions.ScheduleReworkAsync(
            context, correctiveActionId, rowVersion, request.AssignedTeamId, request.AssignedTechnicianId, request.ScheduledStartAt, request.ScheduledEndAt, cancellationToken);
        return CommandResult(result, ResourceType, CorrectiveActionResponses.ToResponse, dto => dto.RowVersion);
    }
}
