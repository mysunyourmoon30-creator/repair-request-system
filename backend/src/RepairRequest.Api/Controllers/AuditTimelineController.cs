using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using RepairRequest.Api.Contracts.Audit;
using RepairRequest.Api.Http;
using RepairRequest.Application.Audit;
using RepairRequest.Application.Security;

namespace RepairRequest.Api.Controllers;

/// <summary>
/// AUD-API-001 (`docs/09`; UC-WO-002; FR-09) — the Work Order timeline, read-only. V1 accepts only
/// <c>entityType = WORK_ORDER</c> (`docs/15` D1/D7); the timeline merges the Work Order's own events with its Visits',
/// Work Sessions', Corrective Actions' and the originating request's convert event (D2). Role capability is the
/// <c>Audit.TimelineRead</c> policy (D3); tenant/Site/ownership scope and the cursor checks are the Application service's
/// (`docs/15` §5 validation order: 401, 403, 400 pageSize/cursor syntax, 400 cursor HMAC/binding, 404). No write path exists:
/// other verbs are 405, nothing is cached and a GET never writes an audit row (D8).
/// </summary>
[ApiController]
[Route("api/v1/entities")]
public sealed class AuditTimelineController : CommandControllerBase
{
    private const string ResourceType = "AuditTimeline";

    private readonly AuditTimelineService _timeline;
    private readonly PagingOptions _paging;

    public AuditTimelineController(AuditTimelineService timeline, ICurrentUserAccessor currentUserAccessor, IOptions<PagingOptions> paging)
        : base(currentUserAccessor)
    {
        _timeline = timeline;
        _paging = paging.Value;
    }

    [HttpGet("{entityType}/{entityId:guid}/timeline")]
    [Authorize(Policy = AuthorizationPolicies.AuditTimelineRead)]
    [ProducesResponseType<AuditTimelineResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Timeline(string entityType, Guid entityId, [FromQuery] AuditTimelineRequest request, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        if (!KeysetPageRequests.TryCreate(request.PageSize, _paging, HttpContext, out var pageSize, out var problem))
        {
            return problem;
        }

        var result = await _timeline.GetWorkOrderTimelineAsync(
            await CallerAsync(cancellationToken), entityType, entityId, pageSize, request.Cursor, cancellationToken);

        return result.Outcome switch
        {
            AuditTimelineOutcome.Ok => Ok(AuditTimelineResponses.ToResponse(result.Page!)),

            // One generic message for every cursor failure: nothing says whether it was malformed, tampered or bound elsewhere.
            AuditTimelineOutcome.BadCursor => ApiProblemResults.BadRequest(HttpContext, "cursor is not valid."),

            // Unsupported entity type, missing and out-of-scope Work Orders share one identical 404 body.
            _ => ApiProblemResults.ResourceNotFound(HttpContext, ResourceType),
        };
    }
}
