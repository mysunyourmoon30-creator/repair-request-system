using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using RepairRequest.Api.Contracts.WorkOrders;
using RepairRequest.Api.Http;
using RepairRequest.Application.Common;
using RepairRequest.Application.Security;
using RepairRequest.Application.WorkOrders;

namespace RepairRequest.Api.Controllers;

/// <summary>
/// Service Visit action endpoints (RR-API-003..007 WO-API-003..007; S2-003; ST-SV-004..009; UC-WO-005..009):
/// Reschedule, Reassign, Cancel, Mark Missed and Decide Missed. All use the ServiceVisit.Manage policy
/// (COORDINATOR) within the Work Order's Site scope; If-Match is required on every action, checked against the
/// Visit's own RowVersion. Every action returns the Visit's parent Work Order (including the full <c>visits</c>
/// array) so the caller never needs a separate fetch — there is no dedicated Service Visit list/detail resource.
/// S3-001 adds the Technician-only "My Visits" list (<see cref="Mine"/>) and Check-in (<see cref="CheckIn"/>,
/// WS-API-001, ST-WS-001).
/// </summary>
[ApiController]
[Route("api/v1/service-visits")]
public sealed class ServiceVisitsController : CommandControllerBase
{
    private const string ResourceType = "ServiceVisit";

    private readonly ServiceVisitManageService _manage;
    private readonly WorkOrderService _workOrders;
    private readonly TechnicianCheckInService _checkIn;
    private readonly PagingOptions _paging;

    public ServiceVisitsController(
        ServiceVisitManageService manage,
        WorkOrderService workOrders,
        TechnicianCheckInService checkIn,
        ICurrentUserAccessor currentUserAccessor,
        IOptions<PagingOptions> paging)
        : base(currentUserAccessor)
    {
        _manage = manage;
        _workOrders = workOrders;
        _checkIn = checkIn;
        _paging = paging.Value;
    }

    /// <summary>"My Visits" (S3-001; UI-040): the caller's own assigned, SCHEDULED Service Visits, newest-first by scheduled start.</summary>
    [HttpGet("mine")]
    [Authorize(Policy = AuthorizationPolicies.MyVisitsRead)]
    [ProducesResponseType<PagedResponse<MyVisitSummaryResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Mine([FromQuery] MyVisitsListRequest request, CancellationToken cancellationToken)
    {
        if (!PageRequests.TryCreate(request.Page, request.PageSize, _paging, HttpContext, out var paging, out var problem))
        {
            return problem;
        }

        var page = await _checkIn.ListMineAsync(await CallerAsync(cancellationToken), new MyVisitsQuery(paging), cancellationToken);
        return Ok(new PagedResponse<MyVisitSummaryResponse>(
            page.Items.Select(MyVisitSummaryResponses.ToResponse).ToList(), page.Page, page.PageSize, page.TotalCount));
    }

    /// <summary>
    /// WS-API-001 Check-in (ST-WS-001; BR-05): only the assigned Technician, only a SCHEDULED Visit with no
    /// active Work Session elsewhere. Empty request body — the client supplies neither status nor Check-in time.
    /// </summary>
    [HttpPost("{serviceVisitId:guid}/check-in")]
    [Authorize(Policy = AuthorizationPolicies.WorkSessionCheckIn)]
    [ProducesResponseType<WorkOrderResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> CheckIn(Guid serviceVisitId, CancellationToken cancellationToken)
    {
        if (!TryReadIfMatch(out var rowVersion, out var problem))
        {
            return problem;
        }

        var context = await CommandContextAsync(cancellationToken);
        var result = await _checkIn.CheckInAsync(context, serviceVisitId, rowVersion, cancellationToken);
        if (!result.Succeeded)
        {
            return ProblemFor(result.Error!, ResourceType);
        }

        // Not RespondAsync/_workOrders.GetAsync: that scope (IDataScope.WorkOrders) deliberately excludes
        // TECHNICIAN (DEC-S2-001-03) — see IWorkOrderStore.GetForTechnicianAsync's own doc comment.
        var workOrder = await _workOrders.GetForTechnicianAsync(context.User, result.Value, cancellationToken);
        return DetailResult(workOrder, "WorkOrder", WorkOrderResponses.ToResponse, dto => dto.RowVersion);
    }

    /// <summary>WO-API-004 Reschedule (ST-SV-004/005): reason and the new window are required.</summary>
    [HttpPost("{serviceVisitId:guid}/reschedule")]
    [Authorize(Policy = AuthorizationPolicies.ServiceVisitManage)]
    [ProducesResponseType<WorkOrderResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Reschedule(Guid serviceVisitId, [FromBody] RescheduleServiceVisitRequest request, CancellationToken cancellationToken)
    {
        if (!TryReadIfMatch(out var rowVersion, out var problem))
        {
            return problem;
        }

        var context = await CommandContextAsync(cancellationToken);
        var result = await _manage.RescheduleAsync(context, serviceVisitId, rowVersion, request.Reason, request.ScheduledStartAt, request.ScheduledEndAt, cancellationToken);
        return await RespondAsync(context, result, cancellationToken);
    }

    /// <summary>WO-API-003 Reassign (ST-SV-006): reason and the new team/technician are required; stays SCHEDULED.</summary>
    [HttpPost("{serviceVisitId:guid}/reassign")]
    [Authorize(Policy = AuthorizationPolicies.ServiceVisitManage)]
    [ProducesResponseType<WorkOrderResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Reassign(Guid serviceVisitId, [FromBody] ReassignServiceVisitRequest request, CancellationToken cancellationToken)
    {
        if (!TryReadIfMatch(out var rowVersion, out var problem))
        {
            return problem;
        }

        var context = await CommandContextAsync(cancellationToken);
        var result = await _manage.ReassignAsync(context, serviceVisitId, rowVersion, request.Reason, request.AssignedTeamId, request.AssignedTechnicianId, cancellationToken);
        return await RespondAsync(context, result, cancellationToken);
    }

    /// <summary>WO-API-005 Cancel Visit (ST-SV-007): only before Check-in; reason required. Work Order/SLA continue.</summary>
    [HttpPost("{serviceVisitId:guid}/cancel")]
    [Authorize(Policy = AuthorizationPolicies.ServiceVisitManage)]
    [ProducesResponseType<WorkOrderResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Cancel(Guid serviceVisitId, [FromBody] CancelServiceVisitRequest request, CancellationToken cancellationToken)
    {
        if (!TryReadIfMatch(out var rowVersion, out var problem))
        {
            return problem;
        }

        var context = await CommandContextAsync(cancellationToken);
        var result = await _manage.CancelAsync(context, serviceVisitId, rowVersion, request.Reason, cancellationToken);
        return await RespondAsync(context, result, cancellationToken);
    }

    /// <summary>WO-API-006 Mark Missed (ST-SV-008): only before Check-in; reason required.</summary>
    [HttpPost("{serviceVisitId:guid}/mark-missed")]
    [Authorize(Policy = AuthorizationPolicies.ServiceVisitManage)]
    [ProducesResponseType<WorkOrderResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> MarkMissed(Guid serviceVisitId, [FromBody] MarkMissedServiceVisitRequest request, CancellationToken cancellationToken)
    {
        if (!TryReadIfMatch(out var rowVersion, out var problem))
        {
            return problem;
        }

        var context = await CommandContextAsync(cancellationToken);
        var result = await _manage.MarkMissedAsync(context, serviceVisitId, rowVersion, request.Reason, cancellationToken);
        return await RespondAsync(context, result, cancellationToken);
    }

    /// <summary>
    /// WO-API-007 Decide Missed (ST-SV-009/D-15): reason required; <c>newSchedule</c> required only for
    /// RESCHEDULE/FOLLOW_UP/REASSIGN, creating a new SCHEDULED Visit. The original stays MISSED and immutable.
    /// </summary>
    [HttpPost("{serviceVisitId:guid}/missed-decision")]
    [Authorize(Policy = AuthorizationPolicies.ServiceVisitManage)]
    [ProducesResponseType<WorkOrderResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> MissedDecision(Guid serviceVisitId, [FromBody] MissedDecisionRequest request, CancellationToken cancellationToken)
    {
        if (!TryReadIfMatch(out var rowVersion, out var problem))
        {
            return problem;
        }

        var context = await CommandContextAsync(cancellationToken);
        var newSchedule = request.NewSchedule is null
            ? null
            : new MissedVisitFollowUpSchedule(
                request.NewSchedule.AssignedTeamId, request.NewSchedule.AssignedTechnicianId, request.NewSchedule.ScheduledStartAt, request.NewSchedule.ScheduledEndAt);

        var result = await _manage.DecideMissedAsync(context, serviceVisitId, rowVersion, request.Decision, request.Reason, newSchedule, cancellationToken);
        return await RespondAsync(context, result, cancellationToken);
    }

    private async Task<IActionResult> RespondAsync(CommandContext context, CommandResult<Guid> result, CancellationToken cancellationToken)
    {
        if (!result.Succeeded)
        {
            return ProblemFor(result.Error!, ResourceType);
        }

        var workOrder = await _workOrders.GetAsync(context.User, result.Value, cancellationToken);
        return DetailResult(workOrder, "WorkOrder", WorkOrderResponses.ToResponse, dto => dto.RowVersion);
    }
}
