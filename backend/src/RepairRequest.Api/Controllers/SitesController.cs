using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using RepairRequest.Api.Contracts.MasterData;
using RepairRequest.Api.Http;
using RepairRequest.Application.MasterData;
using RepairRequest.Application.Security;

namespace RepairRequest.Api.Controllers;

/// <summary>
/// Site master endpoints (DEC-PS1-002). Sites are listed and created under their Customer; the Customer id comes
/// from the route and must be within the caller's scope. Reads use MasterData.Read; changes use MasterData.Manage.
/// </summary>
[ApiController]
[Route("api/v1")]
public sealed class SitesController : MasterDataControllerBase
{
    private const string ResourceType = "Site";

    private readonly SiteService _sites;

    public SitesController(SiteService sites, ICurrentUserAccessor currentUserAccessor, IOptions<PagingOptions> paging)
        : base(currentUserAccessor, paging)
    {
        _sites = sites;
    }

    [HttpGet("customers/{customerId:guid}/sites")]
    [Authorize(Policy = AuthorizationPolicies.MasterDataRead)]
    [ProducesResponseType<PagedResponse<SiteResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(Guid customerId, [FromQuery] MasterDataListRequest request, CancellationToken cancellationToken)
    {
        if (!TryCreateListQuery(request, out var query, out var problem))
        {
            return problem;
        }

        var page = await _sites.ListAsync(await CallerAsync(cancellationToken), customerId, query, cancellationToken);
        return page is null
            ? ApiProblemResults.ResourceNotFound(HttpContext, "Customer")
            : PageResult(page, MasterDataResponses.ToResponse);
    }

    [HttpPost("customers/{customerId:guid}/sites")]
    [Authorize(Policy = AuthorizationPolicies.MasterDataManage)]
    [ProducesResponseType<SiteResponse>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(Guid customerId, [FromBody] SiteCodeRequest request, CancellationToken cancellationToken)
    {
        var result = await _sites.CreateAsync(await CommandContextAsync(cancellationToken), customerId, request.SiteCode, cancellationToken);
        return CommandResult(result, "Customer", MasterDataResponses.ToResponse, dto => dto.RowVersion, dto => $"/api/v1/sites/{dto.Id}");
    }

    [HttpGet("sites/{siteId:guid}")]
    [Authorize(Policy = AuthorizationPolicies.MasterDataRead)]
    [ProducesResponseType<SiteResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid siteId, CancellationToken cancellationToken)
    {
        var site = await _sites.GetAsync(await CallerAsync(cancellationToken), siteId, cancellationToken);
        return DetailResult(site, ResourceType, MasterDataResponses.ToResponse, dto => dto.RowVersion);
    }

    [HttpPatch("sites/{siteId:guid}")]
    [Authorize(Policy = AuthorizationPolicies.MasterDataManage)]
    [ProducesResponseType<SiteResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(Guid siteId, [FromBody] SiteCodeRequest request, CancellationToken cancellationToken)
    {
        if (!TryReadIfMatch(out var rowVersion, out var problem))
        {
            return problem;
        }

        var result = await _sites.UpdateAsync(await CommandContextAsync(cancellationToken), siteId, rowVersion, request.SiteCode, cancellationToken);
        return CommandResult(result, ResourceType, MasterDataResponses.ToResponse, dto => dto.RowVersion);
    }

    [HttpPost("sites/{siteId:guid}/activate")]
    [Authorize(Policy = AuthorizationPolicies.MasterDataManage)]
    [ProducesResponseType<SiteResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Activate(Guid siteId, CancellationToken cancellationToken)
    {
        if (!TryReadIfMatch(out var rowVersion, out var problem))
        {
            return problem;
        }

        var result = await _sites.ActivateAsync(await CommandContextAsync(cancellationToken), siteId, rowVersion, cancellationToken);
        return CommandResult(result, ResourceType, MasterDataResponses.ToResponse, dto => dto.RowVersion);
    }

    [HttpPost("sites/{siteId:guid}/deactivate")]
    [Authorize(Policy = AuthorizationPolicies.MasterDataManage)]
    [ProducesResponseType<SiteResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Deactivate(Guid siteId, [FromBody] DeactivateRequest request, CancellationToken cancellationToken)
    {
        if (!TryReadIfMatch(out var rowVersion, out var problem))
        {
            return problem;
        }

        var result = await _sites.DeactivateAsync(await CommandContextAsync(cancellationToken), siteId, rowVersion, request.Reason, cancellationToken);
        return CommandResult(result, ResourceType, MasterDataResponses.ToResponse, dto => dto.RowVersion);
    }
}
