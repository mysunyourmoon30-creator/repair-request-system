using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Options;
using RepairRequest.Api.Contracts.RepairRequests;
using RepairRequest.Api.Contracts.WorkOrders;
using RepairRequest.Api.Http;
using RepairRequest.Application.Approvals;
using RepairRequest.Application.RepairRequests;
using RepairRequest.Application.Security;
using RepairRequest.Application.WorkOrders;
using RepairRequest.Domain.RepairRequests;

namespace RepairRequest.Api.Controllers;

/// <summary>
/// Repair Request endpoints (RR-API-001 / 002 / 004..010; UC-RR-001..004; UC-WO-001). List and detail use
/// RepairRequest.Read within the S1-003 scope; Create, edit and submit (including Resubmit of a DRAFT returned for
/// correction) use the RepairRequest.Draft policy (REQUESTER); Approve, Reject and Return for Correction use
/// RepairRequest.Review (APPROVER) plus the assigned-approver and self-decision rules; Cancel (RR-API-009) uses
/// RepairRequest.Draft plus ownership; Convert (RR-API-010, S2-002) uses RepairRequest.Convert (COORDINATOR)
/// within their Site scope.
/// </summary>
[ApiController]
[Route("api/v1/repair-requests")]
public sealed class RepairRequestsController : CommandControllerBase
{
    private const string ResourceType = "RepairRequest";

    private readonly RepairRequestDraftService _drafts;
    private readonly RepairRequestSubmitService _submits;
    private readonly RepairRequestDecisionService _decisions;
    private readonly RepairRequestCancelService _cancels;
    private readonly RepairRequestConvertService _converts;
    private readonly WorkOrderService _workOrders;
    private readonly PagingOptions _paging;

    public RepairRequestsController(
        RepairRequestDraftService drafts,
        RepairRequestSubmitService submits,
        RepairRequestDecisionService decisions,
        RepairRequestCancelService cancels,
        RepairRequestConvertService converts,
        WorkOrderService workOrders,
        ICurrentUserAccessor currentUserAccessor,
        IOptions<PagingOptions> paging)
        : base(currentUserAccessor)
    {
        _drafts = drafts;
        _submits = submits;
        _decisions = decisions;
        _cancels = cancels;
        _converts = converts;
        _workOrders = workOrders;
        _paging = paging.Value;
    }

    /// <summary>RR-API-002 List (S2-002): paging plus an optional status filter, within the caller's Repair Request scope.</summary>
    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.RepairRequestRead)]
    [ProducesResponseType<PagedResponse<RepairRequestDraftResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] RepairRequestListRequest request, CancellationToken cancellationToken)
    {
        if (!PageRequests.TryCreate(request.Page, request.PageSize, _paging, HttpContext, out var paging, out var problem))
        {
            return problem;
        }

        RepairRequestStatus? status = null;
        if (request.Status is not null)
        {
            if (!RepairRequestStatusCodes.TryParse(request.Status, out var parsed))
            {
                return ApiProblemResults.BadRequest(HttpContext, "status is not a recognised Repair Request status.");
            }

            status = parsed;
        }

        var page = await _drafts.ListAsync(await CallerAsync(cancellationToken), new RepairRequestListQuery(paging, status), cancellationToken);
        return Ok(new PagedResponse<RepairRequestDraftResponse>(
            page.Items.Select(RepairRequestResponses.ToResponse).ToList(), page.Page, page.PageSize, page.TotalCount));
    }

    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.RepairRequestDraft)]
    [ProducesResponseType<RepairRequestDraftResponse>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create([FromBody] RepairRequestDraftRequest request, CancellationToken cancellationToken)
    {
        var result = await _drafts.CreateAsync(await CommandContextAsync(cancellationToken), request.ToFields(), cancellationToken);
        return CommandResult(result, "Site", RepairRequestResponses.ToResponse, dto => dto.RowVersion, dto => $"/api/v1/repair-requests/{dto.Id}");
    }

    [HttpGet("{repairRequestId:guid}")]
    [Authorize(Policy = AuthorizationPolicies.RepairRequestRead)]
    [ProducesResponseType<RepairRequestDraftResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid repairRequestId, CancellationToken cancellationToken)
    {
        var request = await _drafts.GetAsync(await CallerAsync(cancellationToken), repairRequestId, cancellationToken);
        return DetailResult(request, ResourceType, RepairRequestResponses.ToResponse, dto => dto.RowVersion);
    }

    [HttpPatch("{repairRequestId:guid}")]
    [Authorize(Policy = AuthorizationPolicies.RepairRequestDraft)]
    [ProducesResponseType<RepairRequestDraftResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(Guid repairRequestId, [FromBody] RepairRequestDraftRequest request, CancellationToken cancellationToken)
    {
        if (!TryReadIfMatch(out var rowVersion, out var problem))
        {
            return problem;
        }

        var result = await _drafts.UpdateAsync(
            await CommandContextAsync(cancellationToken), repairRequestId, rowVersion, request.ToFields(), cancellationToken);
        return CommandResult(result, ResourceType, RepairRequestResponses.ToResponse, dto => dto.RowVersion);
    }

    /// <summary>
    /// RR-API-005 Submit (ST-RR-002). If-Match is required. A BR-14 duplicate without a continuation reason returns 422 with a
    /// <c>duplicateCount</c> only; success returns the SUBMITTED request with its Request No and a fresh ETag.
    /// </summary>
    [HttpPost("{repairRequestId:guid}/submit")]
    [Authorize(Policy = AuthorizationPolicies.RepairRequestDraft)]
    [ProducesResponseType<RepairRequestDraftResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Submit(
        Guid repairRequestId,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] SubmitRepairRequestRequest? request,
        CancellationToken cancellationToken)
    {
        if (!TryReadIfMatch(out var rowVersion, out var problem))
        {
            return problem;
        }

        var result = await _submits.SubmitAsync(
            await CommandContextAsync(cancellationToken), repairRequestId, rowVersion, request?.DuplicateContinuationReason, cancellationToken);
        return CommandResult(result, ResourceType, RepairRequestResponses.ToResponse, dto => dto.RowVersion);
    }

    /// <summary>
    /// RR-API-006 Approve (ST-RR-004) by the assigned approver of an UNDER_REVIEW request. If-Match is required and there is no
    /// body. Success returns the APPROVED request with a fresh ETag.
    /// </summary>
    [HttpPost("{repairRequestId:guid}/approve")]
    [Authorize(Policy = AuthorizationPolicies.RepairRequestReview)]
    [ProducesResponseType<RepairRequestDraftResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Approve(Guid repairRequestId, CancellationToken cancellationToken)
    {
        if (!TryReadIfMatch(out var rowVersion, out var problem))
        {
            return problem;
        }

        var result = await _decisions.ApproveAsync(await CommandContextAsync(cancellationToken), repairRequestId, rowVersion, cancellationToken);
        return CommandResult(result, ResourceType, RepairRequestResponses.ToResponse, dto => dto.RowVersion);
    }

    /// <summary>
    /// RR-API-007 Reject (ST-RR-005) by the assigned approver of an UNDER_REVIEW request, with the required reason. If-Match is
    /// required. Success returns the REJECTED request with a fresh ETag.
    /// </summary>
    [HttpPost("{repairRequestId:guid}/reject")]
    [Authorize(Policy = AuthorizationPolicies.RepairRequestReview)]
    [ProducesResponseType<RepairRequestDraftResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Reject(
        Guid repairRequestId,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] RejectRepairRequestRequest? request,
        CancellationToken cancellationToken)
    {
        if (!TryReadIfMatch(out var rowVersion, out var problem))
        {
            return problem;
        }

        var result = await _decisions.RejectAsync(
            await CommandContextAsync(cancellationToken), repairRequestId, rowVersion, request?.Reason, cancellationToken);
        return CommandResult(result, ResourceType, RepairRequestResponses.ToResponse, dto => dto.RowVersion);
    }

    /// <summary>
    /// RR-API-008 Return for Correction (ST-RR-006) of an UNDER_REVIEW request by its assigned approver, with the required
    /// reason. If-Match is required. Success returns the request as DRAFT with a fresh ETag; the owner can edit and resubmit it.
    /// </summary>
    [HttpPost("{repairRequestId:guid}/return-for-correction")]
    [Authorize(Policy = AuthorizationPolicies.RepairRequestReview)]
    [ProducesResponseType<RepairRequestDraftResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ReturnForCorrection(
        Guid repairRequestId,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] ReturnForCorrectionRequest? request,
        CancellationToken cancellationToken)
    {
        if (!TryReadIfMatch(out var rowVersion, out var problem))
        {
            return problem;
        }

        var result = await _decisions.ReturnForCorrectionAsync(
            await CommandContextAsync(cancellationToken), repairRequestId, rowVersion, request?.Reason, cancellationToken);
        return CommandResult(result, ResourceType, RepairRequestResponses.ToResponse, dto => dto.RowVersion);
    }

    /// <summary>
    /// RR-API-009 Cancel (ST-RR-007) by the owning Requester of a DRAFT, SUBMITTED, UNDER_REVIEW or APPROVED request, with the
    /// required reason. If-Match is required. Success returns the CANCELLED request with a fresh ETag.
    /// </summary>
    [HttpPost("{repairRequestId:guid}/cancel")]
    [Authorize(Policy = AuthorizationPolicies.RepairRequestDraft)]
    [ProducesResponseType<RepairRequestDraftResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Cancel(
        Guid repairRequestId,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] CancelRepairRequestRequest? request,
        CancellationToken cancellationToken)
    {
        if (!TryReadIfMatch(out var rowVersion, out var problem))
        {
            return problem;
        }

        var result = await _cancels.CancelAsync(
            await CommandContextAsync(cancellationToken), repairRequestId, rowVersion, request?.Reason, cancellationToken);
        return CommandResult(result, ResourceType, RepairRequestResponses.ToResponse, dto => dto.RowVersion);
    }

    /// <summary>
    /// RR-API-010 Convert (ST-RR-008) by a Coordinator of an APPROVED request within their Site scope. If-Match is
    /// required and there is no body. Success creates exactly one Work Order (OPEN) and returns it directly (S2-001's
    /// WorkOrderResponse shape) with a fresh ETag, so the caller can open it immediately.
    /// </summary>
    [HttpPost("{repairRequestId:guid}/convert-to-work-order")]
    [Authorize(Policy = AuthorizationPolicies.RepairRequestConvert)]
    [ProducesResponseType<WorkOrderResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ConvertToWorkOrder(Guid repairRequestId, CancellationToken cancellationToken)
    {
        if (!TryReadIfMatch(out var rowVersion, out var problem))
        {
            return problem;
        }

        var context = await CommandContextAsync(cancellationToken);
        var result = await _converts.ConvertAsync(context, repairRequestId, rowVersion, cancellationToken);
        if (!result.Succeeded)
        {
            return ProblemFor(result.Error!, ResourceType);
        }

        var workOrder = await _workOrders.GetAsync(context.User, result.Value, cancellationToken);
        return DetailResult(workOrder, "WorkOrder", WorkOrderResponses.ToResponse, dto => dto.RowVersion);
    }
}
