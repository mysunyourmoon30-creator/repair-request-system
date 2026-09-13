using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RepairRequest.Api.Contracts.RepairRequests;
using RepairRequest.Application.RepairRequests;
using RepairRequest.Application.Security;

namespace RepairRequest.Api.Controllers;

/// <summary>
/// Repair Request Draft endpoints (RR-API-001 / RR-API-002 / RR-API-004; UC-RR-001). Create and edit use the
/// RepairRequest.Draft policy (REQUESTER); detail uses RepairRequest.Read within the S1-003 scope. Submit, list,
/// attachments and review actions are later tickets.
/// </summary>
[ApiController]
[Route("api/v1/repair-requests")]
public sealed class RepairRequestsController : CommandControllerBase
{
    private const string ResourceType = "RepairRequest";

    private readonly RepairRequestDraftService _drafts;

    public RepairRequestsController(RepairRequestDraftService drafts, ICurrentUserAccessor currentUserAccessor)
        : base(currentUserAccessor)
    {
        _drafts = drafts;
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
}
