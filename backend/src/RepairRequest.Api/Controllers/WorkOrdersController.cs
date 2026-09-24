using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using RepairRequest.Api.Contracts.WorkOrders;
using RepairRequest.Api.Http;
using RepairRequest.Application.Common;
using RepairRequest.Application.Security;
using RepairRequest.Application.WorkOrders;
using RepairRequest.Domain.WorkOrders;

namespace RepairRequest.Api.Controllers;

/// <summary>
/// Work Order endpoints. List/Detail (S2-001; DEC-S2-001-01..05, `docs/12` Section 11) use the WorkOrder.Read
/// policy within the caller's data scope (<see cref="IDataScope.WorkOrders"/>). Schedule (RR-API-002 WO-API-002,
/// S2-003) uses WorkOrder.Schedule (COORDINATOR); Reassign at the Work Order level and every later transition
/// remain out of scope.
/// </summary>
[ApiController]
[Route("api/v1/work-orders")]
public sealed class WorkOrdersController : CommandControllerBase
{
    private const string ResourceType = "WorkOrder";
    private const string WorkSummaryResourceType = "WorkSummary";

    private readonly WorkOrderService _workOrders;
    private readonly WorkOrderScheduleService _schedule;
    private readonly WorkSummaryService _workSummaries;
    private readonly WorkOrderAcceptanceService _acceptance;
    private readonly PagingOptions _paging;

    public WorkOrdersController(
        WorkOrderService workOrders,
        WorkOrderScheduleService schedule,
        WorkSummaryService workSummaries,
        WorkOrderAcceptanceService acceptance,
        ICurrentUserAccessor currentUserAccessor,
        IOptions<PagingOptions> paging)
        : base(currentUserAccessor)
    {
        _workOrders = workOrders;
        _schedule = schedule;
        _workSummaries = workSummaries;
        _acceptance = acceptance;
        _paging = paging.Value;
    }

    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.WorkOrderRead)]
    [ProducesResponseType<PagedResponse<WorkOrderResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] WorkOrderListRequest request, CancellationToken cancellationToken)
    {
        if (!PageRequests.TryCreate(request.Page, request.PageSize, _paging, HttpContext, out var paging, out var problem))
        {
            return problem;
        }

        WorkOrderStatus? status = null;
        if (request.Status is not null)
        {
            if (!WorkOrderStatusCodes.TryParse(request.Status, out var parsed))
            {
                return ApiProblemResults.BadRequest(HttpContext, "status is not a recognised Work Order status.");
            }

            status = parsed;
        }

        var page = await _workOrders.ListAsync(await CallerAsync(cancellationToken), new WorkOrderListQuery(paging, status), cancellationToken);
        return Ok(new PagedResponse<WorkOrderResponse>(
            page.Items.Select(WorkOrderResponses.ToResponse).ToList(), page.Page, page.PageSize, page.TotalCount));
    }

    [HttpGet("{workOrderId:guid}")]
    [Authorize(Policy = AuthorizationPolicies.WorkOrderRead)]
    [ProducesResponseType<WorkOrderResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid workOrderId, CancellationToken cancellationToken)
    {
        var workOrder = await _workOrders.GetAsync(await CallerAsync(cancellationToken), workOrderId, cancellationToken);
        return DetailResult(workOrder, ResourceType, WorkOrderResponses.ToResponse, dto => dto.RowVersion);
    }

    /// <summary>
    /// RR-API-002 (WO-API-002) Schedule (ST-WO-001) of an OPEN Work Order by a Coordinator within their Site scope.
    /// If-Match is required. Success creates the first Service Visit (SCHEDULED) and returns the Work Order,
    /// including it in <c>visits</c>, with a fresh ETag.
    /// </summary>
    [HttpPost("{workOrderId:guid}/schedule")]
    [Authorize(Policy = AuthorizationPolicies.WorkOrderSchedule)]
    [ProducesResponseType<WorkOrderResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Schedule(Guid workOrderId, [FromBody] ScheduleWorkOrderRequest request, CancellationToken cancellationToken)
    {
        if (!TryReadIfMatch(out var rowVersion, out var problem))
        {
            return problem;
        }

        var context = await CommandContextAsync(cancellationToken);
        var result = await _schedule.ScheduleAsync(
            context, workOrderId, rowVersion, request.OwnerTeamId, request.AssignedTechnicianId, request.ScheduledStartAt, request.ScheduledEndAt, cancellationToken);
        if (!result.Succeeded)
        {
            return ProblemFor(result.Error!, ResourceType);
        }

        var workOrder = await _workOrders.GetAsync(context.User, result.Value, cancellationToken);
        return DetailResult(workOrder, ResourceType, WorkOrderResponses.ToResponse, dto => dto.RowVersion);
    }

    /// <summary>
    /// WSM-API-001 Submit Work Summary (ST-WO-003; UC-WO-020; `docs/13` §4.15): the assigned Technician only, for
    /// a Work Order they have a checked-out Work Session on. If-Match is checked against the Work Order's own
    /// RowVersion (the resource named in this URL). Moves the Work Order to AWAITING_SUPERVISOR_REVIEW.
    /// </summary>
    [HttpPost("{workOrderId:guid}/submit-work-summary")]
    [Authorize(Policy = AuthorizationPolicies.WorkOrderSubmitWorkSummary)]
    [ProducesResponseType<WorkOrderResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> SubmitWorkSummary(Guid workOrderId, [FromBody] SubmitWorkSummaryRequest request, CancellationToken cancellationToken)
    {
        if (!TryReadIfMatch(out var rowVersion, out var problem))
        {
            return problem;
        }

        var context = await CommandContextAsync(cancellationToken);
        var result = await _workSummaries.SubmitAsync(context, workOrderId, rowVersion, request.SummaryText, request.RepairOutcomeCode, cancellationToken);
        if (!result.Succeeded)
        {
            return ProblemFor(result.Error!, ResourceType);
        }

        var workOrder = await _workOrders.GetForTechnicianAsync(context.User, result.Value, cancellationToken);
        return DetailResult(workOrder, ResourceType, WorkOrderResponses.ToResponse, dto => dto.RowVersion);
    }

    /// <summary>
    /// WSM-API-002 Submit for Acceptance (ST-WO-004; `docs/13` §4.15 Decision 2, §4.16 Decision 2): either the
    /// Team Lead or the Supervisor, within their Work Order site scope. If-Match is checked against the Work
    /// Order's own RowVersion. The caller must supply <c>acceptanceContactId</c>, validated server-side (existing
    /// REQUESTER, correct tenant and Site) — never a client-supplied snapshot. Moves the Work Order to
    /// AWAITING_CUSTOMER_ACCEPTANCE and designates the Acceptance Contact in the same transaction.
    /// </summary>
    [HttpPost("{workOrderId:guid}/submit-for-acceptance")]
    [Authorize(Policy = AuthorizationPolicies.WorkOrderSubmitForAcceptance)]
    [ProducesResponseType<WorkOrderResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> SubmitForAcceptance(Guid workOrderId, [FromBody] SubmitForAcceptanceRequest request, CancellationToken cancellationToken)
    {
        if (!TryReadIfMatch(out var rowVersion, out var problem))
        {
            return problem;
        }

        var context = await CommandContextAsync(cancellationToken);
        var result = await _workSummaries.SubmitForAcceptanceAsync(context, workOrderId, rowVersion, request.AcceptanceContactId, cancellationToken);
        if (!result.Succeeded)
        {
            return ProblemFor(result.Error!, ResourceType);
        }

        var workOrder = await _workOrders.GetAsync(context.User, result.Value, cancellationToken);
        return DetailResult(workOrder, ResourceType, WorkOrderResponses.ToResponse, dto => dto.RowVersion);
    }

    /// <summary>
    /// Work Summary read (`docs/13` §4.15): the caller's own, for a Technician; site-wide, for Team Lead/
    /// Supervisor. 404 when none has been submitted yet, or the caller is out of scope (identical response).
    /// </summary>
    [HttpGet("{workOrderId:guid}/work-summary")]
    [Authorize(Policy = AuthorizationPolicies.WorkOrderReadWorkSummary)]
    [ProducesResponseType<WorkSummaryResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetWorkSummary(Guid workOrderId, CancellationToken cancellationToken)
    {
        var summary = await _workSummaries.GetAsync(await CallerAsync(cancellationToken), workOrderId, cancellationToken);
        if (summary is null)
        {
            return ApiProblemResults.ResourceNotFound(HttpContext, WorkSummaryResourceType);
        }

        return Ok(WorkSummaryResponses.ToResponse(summary));
    }

    /// <summary>
    /// `ACC-API-ADD-001` eligible Acceptance Contacts (`docs/13` §4.16): every REQUESTER within the Work Order's
    /// Site scope, for the Team Lead/Supervisor picking a contact before Submit for Acceptance. Never a
    /// free-text id path — this is the only way the caller learns a valid <c>acceptanceContactId</c>. Returns an
    /// empty list (not 404) when the Work Order is out of scope or has no Site, so the UI can show "no eligible
    /// contacts" rather than an error.
    /// </summary>
    [HttpGet("{workOrderId:guid}/eligible-acceptance-contacts")]
    [Authorize(Policy = AuthorizationPolicies.WorkOrderReadEligibleAcceptanceContacts)]
    [ProducesResponseType<IReadOnlyList<EligibleAcceptanceContactResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetEligibleAcceptanceContacts(Guid workOrderId, CancellationToken cancellationToken)
    {
        var contacts = await _workSummaries.ListEligibleAcceptanceContactsAsync(await CallerAsync(cancellationToken), workOrderId, cancellationToken);
        return Ok(contacts.Select(EligibleAcceptanceContactResponses.ToResponse).ToList());
    }

    /// <summary>
    /// ACC-API-001 Customer Accept (ST-WO-005; UC-WO-021; `docs/13` §4.16): only the exact designated Acceptance
    /// Contact (a REQUESTER). If-Match is checked against the Work Order's own RowVersion. Empty request body.
    /// Moves the Work Order to COMPLETED. Scope is resource-specific (caller id == AcceptanceContactId), not a
    /// role-scoped query — any other Requester, even in the same tenant/Site, gets the same non-leaking 404 as a
    /// nonexistent Work Order.
    /// </summary>
    [HttpPost("{workOrderId:guid}/accept")]
    [Authorize(Policy = AuthorizationPolicies.WorkOrderAccept)]
    [ProducesResponseType<WorkOrderResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Accept(Guid workOrderId, CancellationToken cancellationToken)
    {
        if (!TryReadIfMatch(out var rowVersion, out var problem))
        {
            return problem;
        }

        var context = await CommandContextAsync(cancellationToken);
        var result = await _acceptance.AcceptAsync(context, workOrderId, rowVersion, cancellationToken);
        if (!result.Succeeded)
        {
            return ProblemFor(result.Error!, ResourceType);
        }

        // Not _workOrders.GetAsync: IDataScope.WorkOrders()'s REQUESTER branch only covers Work Orders the
        // caller's own Repair Request created, which the accepting contact need not be.
        var workOrder = await _workOrders.GetForAcceptanceContactAsync(context.User, result.Value, cancellationToken);
        return DetailResult(workOrder, ResourceType, WorkOrderResponses.ToResponse, dto => dto.RowVersion);
    }
}
