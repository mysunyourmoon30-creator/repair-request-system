using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using RepairRequest.Api.Contracts.Approvals;
using RepairRequest.Api.Contracts.MasterData;
using RepairRequest.Api.Http;
using RepairRequest.Application.Approvals;
using RepairRequest.Application.Security;

namespace RepairRequest.Api.Controllers;

/// <summary>
/// Approval route configuration (DEC-PRE-S1-007R-04; addendum endpoints, not in the RR-API-001 catalog). Every action
/// uses MasterData.Manage (ADMINISTRATOR) within the caller's tenant; other tenants' routes are 404. Configuration never
/// grants Approve/Reject.
/// </summary>
[ApiController]
[Route("api/v1/approval-routes")]
public sealed class ApprovalRoutesController : MasterDataControllerBase
{
    private const string ResourceType = "ApprovalRoute";

    private readonly ApprovalRouteService _routes;

    public ApprovalRoutesController(ApprovalRouteService routes, ICurrentUserAccessor currentUserAccessor, IOptions<PagingOptions> paging)
        : base(currentUserAccessor, paging)
    {
        _routes = routes;
    }

    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.MasterDataManage)]
    [ProducesResponseType<PagedResponse<ApprovalRouteResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] MasterDataListRequest request, CancellationToken cancellationToken)
    {
        if (!TryCreateListQuery(request, out var query, out var problem))
        {
            return problem;
        }

        var page = await _routes.ListAsync(await CallerAsync(cancellationToken), query, cancellationToken);
        return PageResult(page, ApprovalRoutingResponses.ToResponse);
    }

    [HttpGet("{approvalRouteId:guid}")]
    [Authorize(Policy = AuthorizationPolicies.MasterDataManage)]
    [ProducesResponseType<ApprovalRouteResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid approvalRouteId, CancellationToken cancellationToken)
    {
        var route = await _routes.GetAsync(await CallerAsync(cancellationToken), approvalRouteId, cancellationToken);
        return DetailResult(route, ResourceType, ApprovalRoutingResponses.ToResponse, dto => dto.RowVersion);
    }

    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.MasterDataManage)]
    [ProducesResponseType<ApprovalRouteResponse>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create([FromBody] ApprovalRouteRequest request, CancellationToken cancellationToken)
    {
        var result = await _routes.CreateAsync(await CommandContextAsync(cancellationToken), request.ToFields(), cancellationToken);
        return CommandResult(result, ResourceType, ApprovalRoutingResponses.ToResponse, dto => dto.RowVersion, dto => $"/api/v1/approval-routes/{dto.Id}");
    }

    [HttpPost("{approvalRouteId:guid}/activate")]
    [Authorize(Policy = AuthorizationPolicies.MasterDataManage)]
    [ProducesResponseType<ApprovalRouteResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Activate(Guid approvalRouteId, CancellationToken cancellationToken)
    {
        if (!TryReadIfMatch(out var rowVersion, out var problem))
        {
            return problem;
        }

        var result = await _routes.ActivateAsync(await CommandContextAsync(cancellationToken), approvalRouteId, rowVersion, cancellationToken);
        return CommandResult(result, ResourceType, ApprovalRoutingResponses.ToResponse, dto => dto.RowVersion);
    }

    [HttpPost("{approvalRouteId:guid}/deactivate")]
    [Authorize(Policy = AuthorizationPolicies.MasterDataManage)]
    [ProducesResponseType<ApprovalRouteResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Deactivate(Guid approvalRouteId, CancellationToken cancellationToken)
    {
        if (!TryReadIfMatch(out var rowVersion, out var problem))
        {
            return problem;
        }

        var result = await _routes.DeactivateAsync(await CommandContextAsync(cancellationToken), approvalRouteId, rowVersion, cancellationToken);
        return CommandResult(result, ResourceType, ApprovalRoutingResponses.ToResponse, dto => dto.RowVersion);
    }
}
