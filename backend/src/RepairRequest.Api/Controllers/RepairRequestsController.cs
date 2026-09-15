using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using RepairRequest.Api.Contracts.RepairRequests;
using RepairRequest.Application.RepairRequests;
using RepairRequest.Application.Security;

namespace RepairRequest.Api.Controllers;

/// <summary>
/// Repair Request endpoints (RR-API-001 / RR-API-002 / RR-API-004 / RR-API-005; UC-RR-001/002). Create, edit and submit use
/// the RepairRequest.Draft policy (REQUESTER); detail uses RepairRequest.Read within the S1-003 scope. List, review and
/// cancel actions are later tickets.
/// </summary>
[ApiController]
[Route("api/v1/repair-requests")]
public sealed class RepairRequestsController : CommandControllerBase
{
    private const string ResourceType = "RepairRequest";

    private readonly RepairRequestDraftService _drafts;
    private readonly RepairRequestSubmitService _submits;

    public RepairRequestsController(
        RepairRequestDraftService drafts,
        RepairRequestSubmitService submits,
        ICurrentUserAccessor currentUserAccessor)
        : base(currentUserAccessor)
    {
        _drafts = drafts;
        _submits = submits;
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
}
