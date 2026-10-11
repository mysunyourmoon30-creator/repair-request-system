using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RepairRequest.Api.Contracts.Sla;
using RepairRequest.Api.Http;
using RepairRequest.Application.Security;
using RepairRequest.Application.Sla;

namespace RepairRequest.Api.Controllers;

/// <summary>
/// SLA-API-001 (`docs/09`; `docs/16` D5-D7) — the SLA record of one Repair Request, read-only. Role capability is the existing
/// <c>RepairRequest.Read</c> policy (ADMINISTRATOR is 403); tenant, Site and Requester-ownership scope are the Application
/// service's (<c>IDataScope.RepairRequests</c>). A missing, out-of-scope or never-submitted Repair Request returns one identical
/// 404. A previously submitted request without a record is <c>NOT_TRACKED_LEGACY</c>. No write path exists: other verbs are
/// 405, nothing is cached and a GET never writes (no backfill, no audit row).
/// </summary>
[ApiController]
[Route("api/v1/sla-records")]
public sealed class SlaRecordsController : CommandControllerBase
{
    private const string ResourceType = "SlaRecord";

    private readonly SlaRecordReadService _sla;

    public SlaRecordsController(SlaRecordReadService sla, ICurrentUserAccessor currentUserAccessor)
        : base(currentUserAccessor)
    {
        _sla = sla;
    }

    [HttpGet("{repairRequestId:guid}")]
    [Authorize(Policy = AuthorizationPolicies.RepairRequestRead)]
    [ProducesResponseType<SlaRecordResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid repairRequestId, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        var dto = await _sla.GetAsync(await CallerAsync(cancellationToken), repairRequestId, cancellationToken);
        return dto is null
            ? ApiProblemResults.ResourceNotFound(HttpContext, ResourceType)
            : Ok(SlaRecordResponses.ToResponse(dto));
    }
}
