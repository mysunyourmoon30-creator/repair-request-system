using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using RepairRequest.Api.Contracts.Approvals;
using RepairRequest.Api.Http;
using RepairRequest.Application.Approvals;
using RepairRequest.Application.Security;

namespace RepairRequest.Api.Controllers;

/// <summary>
/// ADMINISTRATOR routing recovery (DEC-PRE-S1-007R-07/08/10; addendum endpoints). The routing-issue list is a narrow,
/// tenant-scoped exception to the S1-003 no-business-read rule and returns routing metadata only. Retry Routing re-runs
/// the server-side routing rules with If-Match; the caller never chooses a route or approver. Neither endpoint grants
/// Repair Request detail, Approve or Reject.
/// </summary>
[ApiController]
public sealed class RoutingRecoveryController : CommandControllerBase
{
    private const string ResourceType = "RepairRequest";

    private readonly RepairRequestRoutingService _routing;
    private readonly PagingOptions _paging;

    public RoutingRecoveryController(RepairRequestRoutingService routing, ICurrentUserAccessor currentUserAccessor, IOptions<PagingOptions> paging)
        : base(currentUserAccessor)
    {
        _routing = routing;
        _paging = paging.Value;
    }

    [HttpGet("api/v1/routing-issues")]
    [Authorize(Policy = AuthorizationPolicies.RoutingRecovery)]
    [ProducesResponseType<PagedResponse<RoutingIssueResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ListIssues([FromQuery] RoutingIssueListRequest request, CancellationToken cancellationToken)
    {
        if (!PageRequests.TryCreate(request.Page, request.PageSize, _paging, HttpContext, out var paging, out var problem))
        {
            return problem;
        }

        var page = await _routing.ListIssuesAsync(await CallerAsync(cancellationToken), paging, cancellationToken);
        return Ok(new PagedResponse<RoutingIssueResponse>(
            page.Items.Select(ApprovalRoutingResponses.ToResponse).ToList(), page.Page, page.PageSize, page.TotalCount));
    }

    [HttpPost("api/v1/repair-requests/{repairRequestId:guid}/retry-routing")]
    [Authorize(Policy = AuthorizationPolicies.RoutingRecovery)]
    [ProducesResponseType<RoutingResultResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> RetryRouting(Guid repairRequestId, CancellationToken cancellationToken)
    {
        if (!TryReadIfMatch(out var rowVersion, out var problem))
        {
            return problem;
        }

        var result = await _routing.RetryAsync(await CommandContextAsync(cancellationToken), repairRequestId, rowVersion, cancellationToken);
        return CommandResult(result, ResourceType, ApprovalRoutingResponses.ToResponse, dto => dto.RowVersion);
    }
}
