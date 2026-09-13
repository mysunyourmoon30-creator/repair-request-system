using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using RepairRequest.Api.Contracts.MasterData;
using RepairRequest.Api.Http;
using RepairRequest.Application.MasterData;
using RepairRequest.Application.Security;

namespace RepairRequest.Api.Controllers;

/// <summary>
/// Equipment master endpoints (DEC-PS1-003). Equipment is listed and created under its Site; the Site id comes
/// from the route and must be within the caller's scope. Reads use MasterData.Read; changes use MasterData.Manage.
/// </summary>
[ApiController]
[Route("api/v1")]
public sealed class EquipmentController : MasterDataControllerBase
{
    private const string ResourceType = "Equipment";

    private readonly EquipmentService _equipment;

    public EquipmentController(EquipmentService equipment, ICurrentUserAccessor currentUserAccessor, IOptions<PagingOptions> paging)
        : base(currentUserAccessor, paging)
    {
        _equipment = equipment;
    }

    [HttpGet("sites/{siteId:guid}/equipment")]
    [Authorize(Policy = AuthorizationPolicies.MasterDataRead)]
    [ProducesResponseType<PagedResponse<EquipmentResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(Guid siteId, [FromQuery] MasterDataListRequest request, CancellationToken cancellationToken)
    {
        if (!TryCreateListQuery(request, out var query, out var problem))
        {
            return problem;
        }

        var page = await _equipment.ListAsync(await CallerAsync(cancellationToken), siteId, query, cancellationToken);
        return page is null
            ? ApiProblemResults.ResourceNotFound(HttpContext, "Site")
            : PageResult(page, MasterDataResponses.ToResponse);
    }

    [HttpPost("sites/{siteId:guid}/equipment")]
    [Authorize(Policy = AuthorizationPolicies.MasterDataManage)]
    [ProducesResponseType<EquipmentResponse>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(Guid siteId, [FromBody] EquipmentCodeRequest request, CancellationToken cancellationToken)
    {
        var result = await _equipment.CreateAsync(await CommandContextAsync(cancellationToken), siteId, request.EquipmentCode, cancellationToken);
        return CommandResult(result, "Site", MasterDataResponses.ToResponse, dto => dto.RowVersion, dto => $"/api/v1/equipment/{dto.Id}");
    }

    [HttpGet("equipment/{equipmentId:guid}")]
    [Authorize(Policy = AuthorizationPolicies.MasterDataRead)]
    [ProducesResponseType<EquipmentResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid equipmentId, CancellationToken cancellationToken)
    {
        var equipment = await _equipment.GetAsync(await CallerAsync(cancellationToken), equipmentId, cancellationToken);
        return DetailResult(equipment, ResourceType, MasterDataResponses.ToResponse, dto => dto.RowVersion);
    }

    [HttpPatch("equipment/{equipmentId:guid}")]
    [Authorize(Policy = AuthorizationPolicies.MasterDataManage)]
    [ProducesResponseType<EquipmentResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(Guid equipmentId, [FromBody] EquipmentCodeRequest request, CancellationToken cancellationToken)
    {
        if (!TryReadIfMatch(out var rowVersion, out var problem))
        {
            return problem;
        }

        var result = await _equipment.UpdateAsync(
            await CommandContextAsync(cancellationToken), equipmentId, rowVersion, request.EquipmentCode, cancellationToken);
        return CommandResult(result, ResourceType, MasterDataResponses.ToResponse, dto => dto.RowVersion);
    }

    [HttpPost("equipment/{equipmentId:guid}/activate")]
    [Authorize(Policy = AuthorizationPolicies.MasterDataManage)]
    [ProducesResponseType<EquipmentResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Activate(Guid equipmentId, CancellationToken cancellationToken)
    {
        if (!TryReadIfMatch(out var rowVersion, out var problem))
        {
            return problem;
        }

        var result = await _equipment.ActivateAsync(await CommandContextAsync(cancellationToken), equipmentId, rowVersion, cancellationToken);
        return CommandResult(result, ResourceType, MasterDataResponses.ToResponse, dto => dto.RowVersion);
    }

    [HttpPost("equipment/{equipmentId:guid}/deactivate")]
    [Authorize(Policy = AuthorizationPolicies.MasterDataManage)]
    [ProducesResponseType<EquipmentResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Deactivate(Guid equipmentId, [FromBody] DeactivateRequest request, CancellationToken cancellationToken)
    {
        if (!TryReadIfMatch(out var rowVersion, out var problem))
        {
            return problem;
        }

        var result = await _equipment.DeactivateAsync(
            await CommandContextAsync(cancellationToken), equipmentId, rowVersion, request.Reason, cancellationToken);
        return CommandResult(result, ResourceType, MasterDataResponses.ToResponse, dto => dto.RowVersion);
    }
}
