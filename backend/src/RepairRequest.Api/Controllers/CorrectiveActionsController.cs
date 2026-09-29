using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RepairRequest.Api.Contracts.WorkOrders;
using RepairRequest.Application.Security;
using RepairRequest.Application.WorkOrders;

namespace RepairRequest.Api.Controllers;

/// <summary>
/// Corrective Action endpoints (`docs/13` §4.21; Ticket 6). Distinct route root from
/// <see cref="WorkOrdersController"/> because the baseline's own catalog (`docs/09`) names these resources
/// `/api/v1/corrective-actions/{id}/...`, not `/api/v1/work-orders/{id}/...` — the Corrective Action is the
/// resource acted upon, even though both actions also advance the linked Work Order's own status
/// (ST-WO-008/009) and use its RowVersion as the concurrency token.
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
}
