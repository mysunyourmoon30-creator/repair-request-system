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
    private const string CostSummaryResourceType = "CostSummary";

    private readonly WorkOrderService _workOrders;
    private readonly WorkOrderScheduleService _schedule;
    private readonly WorkSummaryService _workSummaries;
    private readonly WorkOrderAcceptanceService _acceptance;
    private readonly CostSummaryService _costSummaries;
    private readonly WorkOrderCloseService _close;
    private readonly WorkOrderCancelService _cancel;
    private readonly PagingOptions _paging;

    public WorkOrdersController(
        WorkOrderService workOrders,
        WorkOrderScheduleService schedule,
        WorkSummaryService workSummaries,
        WorkOrderAcceptanceService acceptance,
        CostSummaryService costSummaries,
        WorkOrderCloseService close,
        WorkOrderCancelService cancel,
        ICurrentUserAccessor currentUserAccessor,
        IOptions<PagingOptions> paging)
        : base(currentUserAccessor)
    {
        _workOrders = workOrders;
        _schedule = schedule;
        _workSummaries = workSummaries;
        _acceptance = acceptance;
        _costSummaries = costSummaries;
        _close = close;
        _cancel = cancel;
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

    /// <summary>
    /// ACC-API-002 Customer Reject (ST-WO-007; UC-WO-022; BR-07/BR-15; `docs/13` §4.17): only the exact
    /// designated Acceptance Contact (a REQUESTER) — the same resource-specific scope as Accept. If-Match is
    /// checked against the Work Order's own RowVersion. <c>decisionReason</c> is required. Moves the Work Order
    /// to CORRECTIVE_ACTION_REQUIRED, recording a REJECT decision row and a DRAFT Corrective Action row in the
    /// same transaction.
    /// </summary>
    [HttpPost("{workOrderId:guid}/reject")]
    [Authorize(Policy = AuthorizationPolicies.WorkOrderReject)]
    [ProducesResponseType<WorkOrderResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Reject(Guid workOrderId, [FromBody] RejectWorkOrderRequest request, CancellationToken cancellationToken)
    {
        if (!TryReadIfMatch(out var rowVersion, out var problem))
        {
            return problem;
        }

        var context = await CommandContextAsync(cancellationToken);
        var result = await _acceptance.RejectAsync(context, workOrderId, rowVersion, request.DecisionReason, cancellationToken);
        if (!result.Succeeded)
        {
            return ProblemFor(result.Error!, ResourceType);
        }

        // Same reason as Accept's own read-back: the general Requester-scoped query does not cover a Work Order
        // the caller is only the designated Acceptance Contact of, not the creator.
        var workOrder = await _workOrders.GetForAcceptanceContactAsync(context.User, result.Value, cancellationToken);
        return DetailResult(workOrder, ResourceType, WorkOrderResponses.ToResponse, dto => dto.RowVersion);
    }

    /// <summary>
    /// CST-API-001 Prepare Cost Summary (Team Lead only; BR-08; `docs/13` §4.18): create or, before Review, edit
    /// the single Cost Summary row for a COMPLETED Work Order. If-Match is checked against the Work Order's own
    /// RowVersion on first Prepare, or the Cost Summary's own RowVersion on a later edit (see
    /// <see cref="ICostSummaryStore"/>'s own remarks) — the response always carries the current, correct target
    /// for the caller's next edit as its ETag.
    /// </summary>
    [HttpPut("{workOrderId:guid}/cost-summary")]
    [Authorize(Policy = AuthorizationPolicies.CostSummaryPrepare)]
    [ProducesResponseType<CostSummaryResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> PrepareCostSummary(Guid workOrderId, [FromBody] PrepareCostSummaryRequest request, CancellationToken cancellationToken)
    {
        if (!TryReadIfMatch(out var rowVersion, out var problem))
        {
            return problem;
        }

        var context = await CommandContextAsync(cancellationToken);
        var result = await _costSummaries.PrepareAsync(context, workOrderId, rowVersion, request.TotalAmount, request.CurrencyCode, request.Note, cancellationToken);
        if (!result.Succeeded)
        {
            return ProblemFor(result.Error!, ResourceType);
        }

        return DetailResult(result.Value, ResourceType, CostSummaryResponses.ToResponse, dto => dto.RowVersion);
    }

    /// <summary>
    /// Cost Summary read (`docs/13` §4.19, technical addition — no baseline read endpoint exists): Team Lead
    /// (their own Prepare scope) or Supervisor (site-wide, to review). 404 when none has been prepared yet, or
    /// the caller is out of scope (identical response) — never leaks whether a Cost Summary exists to a caller
    /// who cannot see it.
    /// </summary>
    [HttpGet("{workOrderId:guid}/cost-summary")]
    [Authorize(Policy = AuthorizationPolicies.CostSummaryRead)]
    [ProducesResponseType<CostSummaryResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetCostSummary(Guid workOrderId, CancellationToken cancellationToken)
    {
        var costSummary = await _costSummaries.GetAsync(await CallerAsync(cancellationToken), workOrderId, cancellationToken);
        if (costSummary is null)
        {
            return ApiProblemResults.ResourceNotFound(HttpContext, CostSummaryResourceType);
        }

        return Ok(CostSummaryResponses.ToResponse(costSummary));
    }

    /// <summary>
    /// The Supervisor pending Cost Summary Review queue (`docs/13` §4.19, technical addition — no baseline queue
    /// endpoint exists): every Work Order, within the caller's Site scope, that is COMPLETED with an unreviewed
    /// Cost Summary. Newest-prepared-first, paged like every other list in this codebase.
    /// </summary>
    [HttpGet("pending-cost-summary-review")]
    [Authorize(Policy = AuthorizationPolicies.CostSummaryReadPendingReview)]
    [ProducesResponseType<PagedResponse<PendingCostSummaryReviewResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> PendingCostSummaryReview([FromQuery] PendingCostSummaryReviewListRequest request, CancellationToken cancellationToken)
    {
        if (!PageRequests.TryCreate(request.Page, request.PageSize, _paging, HttpContext, out var paging, out var problem))
        {
            return problem;
        }

        var page = await _costSummaries.ListPendingReviewAsync(await CallerAsync(cancellationToken), new PendingCostSummaryReviewQuery(paging), cancellationToken);
        return Ok(new PagedResponse<PendingCostSummaryReviewResponse>(
            page.Items.Select(PendingCostSummaryReviewResponses.ToResponse).ToList(), page.Page, page.PageSize, page.TotalCount));
    }

    /// <summary>
    /// CST-API-002 Review Cost Summary (Supervisor only; BR-08; `docs/13` §4.19): mark a COMPLETED Work Order's
    /// not-yet-reviewed Cost Summary as reviewed. If-Match is checked against the Cost Summary's own RowVersion.
    /// Empty request body — <c>reviewedBy</c>/<c>reviewedAt</c> are always server-derived, never accepted from
    /// the client. Separation of Duties: the caller must not be the Cost Summary's own preparer, even holding
    /// both Team Lead and Supervisor roles — 403 otherwise. Does not change the Work Order's own status.
    /// </summary>
    [HttpPost("{workOrderId:guid}/review-cost-summary")]
    [Authorize(Policy = AuthorizationPolicies.CostSummaryReview)]
    [ProducesResponseType<CostSummaryResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ReviewCostSummary(Guid workOrderId, CancellationToken cancellationToken)
    {
        if (!TryReadIfMatch(out var rowVersion, out var problem))
        {
            return problem;
        }

        var context = await CommandContextAsync(cancellationToken);
        var result = await _costSummaries.ReviewAsync(context, workOrderId, rowVersion, cancellationToken);
        if (!result.Succeeded)
        {
            return ProblemFor(result.Error!, ResourceType);
        }

        return DetailResult(result.Value, ResourceType, CostSummaryResponses.ToResponse, dto => dto.RowVersion);
    }

    /// <summary>
    /// WO-API-010 Close Work Order (ST-WO-006; BR-08; `docs/13` §4.20): Supervisor only, within the caller's Site
    /// scope. If-Match is checked against the Work Order's own RowVersion. Empty request body —
    /// <c>closed_by</c>/<c>closed_at</c> are always server-derived. Requires the Work Order to be COMPLETED, a
    /// current Work Summary that was reviewed, a customer ACCEPT on the current submission, and a reviewed Cost
    /// Summary — any miss is 409 STATE_CONFLICT with a specific message. Moves the Work Order to CLOSED and
    /// returns it with a fresh ETag.
    /// </summary>
    [HttpPost("{workOrderId:guid}/close")]
    [Authorize(Policy = AuthorizationPolicies.WorkOrderClose)]
    [ProducesResponseType<WorkOrderResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Close(Guid workOrderId, CancellationToken cancellationToken)
    {
        if (!TryReadIfMatch(out var rowVersion, out var problem))
        {
            return problem;
        }

        var context = await CommandContextAsync(cancellationToken);
        var result = await _close.CloseAsync(context, workOrderId, rowVersion, cancellationToken);
        if (!result.Succeeded)
        {
            return ProblemFor(result.Error!, ResourceType);
        }

        // The caller just passed IDataScope.WorkOrders() inside Close, so the ordinary scoped read-back covers them.
        var workOrder = await _workOrders.GetAsync(context.User, result.Value, cancellationToken);
        return DetailResult(workOrder, ResourceType, WorkOrderResponses.ToResponse, dto => dto.RowVersion);
    }

    /// <summary>
    /// WO-API-011 Cancel Work Order (ST-WO-011; UC-WO-026; BR-09/BR-12; `docs/13` §4.25): Supervisor only, within
    /// the caller's Site scope. If-Match is checked against the Work Order's own RowVersion. <c>reason</c> is
    /// required. Allowed from every non-terminal, pre-Accept status; denied once the customer has accepted, or the
    /// Work Order is COMPLETED, CLOSED or already CANCELLED (409 STATE_CONFLICT), or while any Service Visit is
    /// IN_PROGRESS (409 STATE_CONFLICT — check out the active session first). Success cascades Cancel to every
    /// still-SCHEDULED Service Visit and returns the Work Order with a fresh ETag.
    /// </summary>
    [HttpPost("{workOrderId:guid}/cancel")]
    [Authorize(Policy = AuthorizationPolicies.WorkOrderCancel)]
    [ProducesResponseType<WorkOrderResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Cancel(Guid workOrderId, [FromBody] CancelWorkOrderRequest request, CancellationToken cancellationToken)
    {
        if (!TryReadIfMatch(out var rowVersion, out var problem))
        {
            return problem;
        }

        var context = await CommandContextAsync(cancellationToken);
        var result = await _cancel.CancelAsync(context, workOrderId, rowVersion, request.Reason, cancellationToken);
        if (!result.Succeeded)
        {
            return ProblemFor(result.Error!, ResourceType);
        }

        var workOrder = await _workOrders.GetAsync(context.User, result.Value, cancellationToken);
        return DetailResult(workOrder, ResourceType, WorkOrderResponses.ToResponse, dto => dto.RowVersion);
    }
}
