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
/// Time Correction decision endpoints (ST-TC-002/003; UC-TIME-001; `docs/17`): the Supervisor's pending review list
/// (<see cref="Pending"/>, TIME-API-ADD-001 — a technical routing addition, not in `docs/09`'s catalog, because a Supervisor
/// otherwise cannot discover PENDING requests), Approve (TIME-API-002) and Reject (TIME-API-003). Supervisor only, within their Site
/// scope — another Site's, cross-tenant or nonexistent request is the same non-leaking 404. If-Match is checked against the
/// correction's own RowVersion. Approve and Reject refuse the requester (403). Request (TIME-API-001) lives on
/// <see cref="WorkSessionsController"/> since it acts on a Work Session.
/// </summary>
[ApiController]
[Route("api/v1/time-corrections")]
public sealed class TimeCorrectionsController : CommandControllerBase
{
    private const string ResourceType = "TimeCorrection";

    private readonly TimeCorrectionService _timeCorrections;
    private readonly PagingOptions _paging;

    public TimeCorrectionsController(TimeCorrectionService timeCorrections, ICurrentUserAccessor currentUserAccessor, IOptions<PagingOptions> paging)
        : base(currentUserAccessor)
    {
        _timeCorrections = timeCorrections;
        _paging = paging.Value;
    }

    /// <summary>TIME-API-ADD-001: the caller's site-scoped PENDING Time Corrections, oldest-first — the Supervisor's review queue.</summary>
    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.TimeCorrectionDecide)]
    [ProducesResponseType<PagedResponse<TimeCorrectionResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Pending([FromQuery] PendingTimeCorrectionsListRequest request, CancellationToken cancellationToken)
    {
        if (!PageRequests.TryCreate(request.Page, request.PageSize, _paging, HttpContext, out var paging, out var problem))
        {
            return problem;
        }

        var page = await _timeCorrections.ListPendingAsync(await CallerAsync(cancellationToken), new PendingTimeCorrectionsQuery(paging), cancellationToken);
        return Ok(new PagedResponse<TimeCorrectionResponse>(
            page.Items.Select(TimeCorrectionResponses.ToResponse).ToList(), page.Page, page.PageSize, page.TotalCount));
    }

    /// <summary>TIME-API-002 Approve (ST-TC-002): a PENDING request only, within the Supervisor's Site scope. Empty body.</summary>
    [HttpPost("{timeCorrectionId:guid}/approve")]
    [Authorize(Policy = AuthorizationPolicies.TimeCorrectionDecide)]
    [ProducesResponseType<TimeCorrectionResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Approve(Guid timeCorrectionId, CancellationToken cancellationToken)
    {
        if (!TryReadIfMatch(out var rowVersion, out var problem))
        {
            return problem;
        }

        var context = await CommandContextAsync(cancellationToken);
        var result = await _timeCorrections.ApproveAsync(context, timeCorrectionId, rowVersion, cancellationToken);
        if (!result.Succeeded)
        {
            return ProblemFor(result.Error!, ResourceType);
        }

        var correction = await _timeCorrections.GetAsync(context.User, result.Value, cancellationToken);
        return DetailResult(correction, ResourceType, TimeCorrectionResponses.ToResponse, dto => dto.RowVersion);
    }

    /// <summary>TIME-API-003 Reject (ST-TC-003): a PENDING request only, within the Supervisor's Site scope. A decision reason is required.</summary>
    [HttpPost("{timeCorrectionId:guid}/reject")]
    [Authorize(Policy = AuthorizationPolicies.TimeCorrectionDecide)]
    [ProducesResponseType<TimeCorrectionResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Reject(Guid timeCorrectionId, [FromBody] RejectTimeCorrectionRequest request, CancellationToken cancellationToken)
    {
        if (!TryReadIfMatch(out var rowVersion, out var problem))
        {
            return problem;
        }

        var context = await CommandContextAsync(cancellationToken);
        var result = await _timeCorrections.RejectAsync(context, timeCorrectionId, rowVersion, request.DecisionReason, cancellationToken);
        if (!result.Succeeded)
        {
            return ProblemFor(result.Error!, ResourceType);
        }

        var correction = await _timeCorrections.GetAsync(context.User, result.Value, cancellationToken);
        return DetailResult(correction, ResourceType, TimeCorrectionResponses.ToResponse, dto => dto.RowVersion);
    }
}
