using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RepairRequest.Api.Http;
using RepairRequest.Application.Security;

namespace RepairRequest.ApiTests.Authorization;

/// <summary>
/// Test-only endpoints showing how later controllers consume S1-003: a policy per action class,
/// the caller from <see cref="ICurrentUserAccessor"/>, and every lookup through <see cref="IDataScope"/>
/// so out-of-scope and nonexistent ids produce the same 404.
/// </summary>
[ApiController]
[Route("test/authorization")]
public sealed class ScopeProbeController : ControllerBase
{
    private readonly ICurrentUserAccessor _currentUserAccessor;
    private readonly IDataScope _dataScope;

    public ScopeProbeController(ICurrentUserAccessor currentUserAccessor, IDataScope dataScope)
    {
        _currentUserAccessor = currentUserAccessor;
        _dataScope = dataScope;
    }

    private async Task<CurrentUser> CallerAsync(CancellationToken cancellationToken) =>
        (await _currentUserAccessor.GetAsync(cancellationToken))!;

    /// <summary>Protected only by the fallback policy (authenticated, resolvable caller).</summary>
    [HttpGet("me")]
    public async Task<IActionResult> Me(CancellationToken cancellationToken)
    {
        var caller = await CallerAsync(cancellationToken);
        return Ok(new { caller.UserId, caller.TenantId, Roles = caller.Roles.Order().ToArray() });
    }

    [HttpGet("sites")]
    [Authorize(Policy = AuthorizationPolicies.MasterDataRead)]
    public async Task<IActionResult> ListSites(CancellationToken cancellationToken)
    {
        var caller = await CallerAsync(cancellationToken);
        var siteIds = await _dataScope.Sites(caller).AsNoTracking().Select(site => site.Id).ToListAsync(cancellationToken);
        return Ok(siteIds);
    }

    [HttpGet("sites/{siteId:guid}")]
    [Authorize(Policy = AuthorizationPolicies.MasterDataRead)]
    public async Task<IActionResult> GetSite(Guid siteId, CancellationToken cancellationToken)
    {
        var caller = await CallerAsync(cancellationToken);
        var site = await _dataScope.Sites(caller)
            .AsNoTracking()
            .Where(candidate => candidate.Id == siteId)
            .Select(candidate => new { candidate.Id, candidate.SiteCode })
            .SingleOrDefaultAsync(cancellationToken);

        return site is null ? ApiProblemResults.ResourceNotFound(HttpContext, "Site") : Ok(site);
    }

    [HttpGet("repair-requests/{repairRequestId:guid}")]
    [Authorize(Policy = AuthorizationPolicies.RepairRequestRead)]
    public async Task<IActionResult> GetRepairRequest(Guid repairRequestId, CancellationToken cancellationToken)
    {
        var caller = await CallerAsync(cancellationToken);
        var request = await _dataScope.RepairRequests(caller)
            .AsNoTracking()
            .Where(candidate => candidate.Id == repairRequestId)
            .Select(candidate => new { candidate.Id })
            .SingleOrDefaultAsync(cancellationToken);

        return request is null ? ApiProblemResults.ResourceNotFound(HttpContext, "RepairRequest") : Ok(request);
    }

    [HttpPost("master-data/manage")]
    [Authorize(Policy = AuthorizationPolicies.MasterDataManage)]
    public IActionResult ManageMasterData() => NoContent();

    /// <summary>
    /// Simulates a command DTO carrying tenant and site ids from the client. The tenant id is ignored in
    /// favour of the caller's tenant, and the site id is validated against scope.
    /// </summary>
    [HttpPost("repair-requests/site-check")]
    [Authorize(Policy = AuthorizationPolicies.RepairRequestDraft)]
    public async Task<IActionResult> CheckSite([FromBody] SiteSelectionRequest request, CancellationToken cancellationToken)
    {
        var caller = await CallerAsync(cancellationToken);
        if (!await _dataScope.IsSiteInScopeAsync(caller, request.SiteId, cancellationToken))
        {
            return ApiProblemResults.ResourceNotFound(HttpContext, "Site");
        }

        return Ok(new { TenantId = caller.TenantId, request.SiteId });
    }
}

public sealed record SiteSelectionRequest(Guid TenantId, Guid SiteId);
