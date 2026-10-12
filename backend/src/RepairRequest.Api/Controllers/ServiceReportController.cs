using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RepairRequest.Api.Contracts.WorkEvidence;
using RepairRequest.Api.Http;
using RepairRequest.Application.Security;
using RepairRequest.Application.WorkEvidence;

namespace RepairRequest.Api.Controllers;

/// <summary>
/// SVR-API-001 Service Report (`docs/13` §4.28; `docs/16` D1, OD4/OD5) — a read-only operational completion record assembled from existing
/// persisted facts; there is no Service Report table and no write path (other verbs are 405). Role capability is the
/// <c>ServiceReport.Read</c> policy; the audience (assigned Technician, in-scope Team Lead/Supervisor, exactly the Acceptance Contact) is
/// resolved by the Application service, and every other caller — including a Requester who is not the exact contact — gets the same
/// non-leaking 404 as a missing Work Order. The response type is chosen by audience: only the Team Lead/Supervisor type has a cost section;
/// the Technician and Acceptance Contact type has no cost member at all. <c>Cache-Control: no-store</c>.
/// </summary>
[ApiController]
[Route("api/v1/work-orders/{workOrderId:guid}/service-report")]
public sealed class ServiceReportController : CommandControllerBase
{
    private const string ResourceType = "ServiceReport";

    private readonly ServiceReportService _reports;

    public ServiceReportController(ServiceReportService reports, ICurrentUserAccessor currentUserAccessor)
        : base(currentUserAccessor)
    {
        _reports = reports;
    }

    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.ServiceReportRead)]
    [ProducesResponseType<ServiceReportFullResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid workOrderId, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        var result = await _reports.GetAsync(await CallerAsync(cancellationToken), workOrderId, cancellationToken);
        return result is null
            ? ApiProblemResults.ResourceNotFound(HttpContext, ResourceType)
            : Ok(ServiceReportResponses.ToResponse(result));
    }
}
