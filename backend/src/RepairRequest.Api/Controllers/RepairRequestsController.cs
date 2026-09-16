using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using RepairRequest.Api.Contracts.RepairRequests;
using RepairRequest.Application.Approvals;
using RepairRequest.Application.RepairRequests;
using RepairRequest.Application.Security;

namespace RepairRequest.Api.Controllers;

/// <summary>
/// Repair Request endpoints (RR-API-001 / RR-API-002 / RR-API-004 / RR-API-005 / RR-API-006 / RR-API-007; UC-RR-001/002/003).
/// Create, edit and submit use the RepairRequest.Draft policy (REQUESTER); detail uses RepairRequest.Read within the S1-003
/// scope; Approve and Reject use RepairRequest.Review (APPROVER) plus the assigned-approver and self-decision rules; Cancel
/// (RR-API-009) uses RepairRequest.Draft plus ownership. List, Return for Correction and Convert are later tickets.
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

    public RepairRequestsController(
        RepairRequestDraftService drafts,
        RepairRequestSubmitService submits,
        RepairRequestDecisionService decisions,
        RepairRequestCancelService cancels,
        ICurrentUserAccessor currentUserAccessor)
        : base(currentUserAccessor)
    {
        _drafts = drafts;
        _submits = submits;
        _decisions = decisions;
        _cancels = cancels;
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
}
