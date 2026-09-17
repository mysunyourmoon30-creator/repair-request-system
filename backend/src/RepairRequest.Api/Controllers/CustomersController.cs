using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using RepairRequest.Api.Contracts.MasterData;
using RepairRequest.Api.Http;
using RepairRequest.Application.MasterData;
using RepairRequest.Application.Security;

namespace RepairRequest.Api.Controllers;

/// <summary>Customer master endpoints (DEC-PS1-001). Reads use MasterData.Read; every change uses MasterData.Manage.</summary>
[ApiController]
[Route("api/v1/customers")]
public sealed class CustomersController : MasterDataControllerBase
{
    private const string ResourceType = "Customer";

    private readonly CustomerService _customers;

    public CustomersController(CustomerService customers, ICurrentUserAccessor currentUserAccessor, IOptions<PagingOptions> paging)
        : base(currentUserAccessor, paging)
    {
        _customers = customers;
    }

    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.MasterDataRead)]
    [ProducesResponseType<PagedResponse<CustomerResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] MasterDataListRequest request, CancellationToken cancellationToken)
    {
        if (!TryCreateListQuery(request, out var query, out var problem))
        {
            return problem;
        }

        var page = await _customers.ListAsync(await CallerAsync(cancellationToken), query, cancellationToken);
        return PageResult(page, MasterDataResponses.ToResponse);
    }

    [HttpGet("{customerId:guid}")]
    [Authorize(Policy = AuthorizationPolicies.MasterDataRead)]
    [ProducesResponseType<CustomerResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid customerId, CancellationToken cancellationToken)
    {
        var customer = await _customers.GetAsync(await CallerAsync(cancellationToken), customerId, cancellationToken);
        return DetailResult(customer, ResourceType, MasterDataResponses.ToResponse, dto => dto.RowVersion);
    }

    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.MasterDataManage)]
    [ProducesResponseType<CustomerResponse>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create([FromBody] CustomerCodeRequest request, CancellationToken cancellationToken)
    {
        var result = await _customers.CreateAsync(await CommandContextAsync(cancellationToken), request.CustomerCode, cancellationToken);
        return CommandResult(result, ResourceType, MasterDataResponses.ToResponse, dto => dto.RowVersion, dto => $"/api/v1/customers/{dto.Id}");
    }

    [HttpPatch("{customerId:guid}")]
    [Authorize(Policy = AuthorizationPolicies.MasterDataManage)]
    [ProducesResponseType<CustomerResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(Guid customerId, [FromBody] CustomerCodeRequest request, CancellationToken cancellationToken)
    {
        if (!TryReadIfMatch(out var rowVersion, out var problem))
        {
            return problem;
        }

        var result = await _customers.UpdateAsync(
            await CommandContextAsync(cancellationToken), customerId, rowVersion, request.CustomerCode, cancellationToken);
        return CommandResult(result, ResourceType, MasterDataResponses.ToResponse, dto => dto.RowVersion);
    }

    [HttpPost("{customerId:guid}/activate")]
    [Authorize(Policy = AuthorizationPolicies.MasterDataManage)]
    [ProducesResponseType<CustomerResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Activate(Guid customerId, CancellationToken cancellationToken)
    {
        if (!TryReadIfMatch(out var rowVersion, out var problem))
        {
            return problem;
        }

        var result = await _customers.ActivateAsync(await CommandContextAsync(cancellationToken), customerId, rowVersion, cancellationToken);
        return CommandResult(result, ResourceType, MasterDataResponses.ToResponse, dto => dto.RowVersion);
    }

    [HttpPost("{customerId:guid}/deactivate")]
    [Authorize(Policy = AuthorizationPolicies.MasterDataManage)]
    [ProducesResponseType<CustomerResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Deactivate(Guid customerId, [FromBody] DeactivateRequest request, CancellationToken cancellationToken)
    {
        if (!TryReadIfMatch(out var rowVersion, out var problem))
        {
            return problem;
        }

        var result = await _customers.DeactivateAsync(
            await CommandContextAsync(cancellationToken), customerId, rowVersion, request.Reason, cancellationToken);
        return CommandResult(result, ResourceType, MasterDataResponses.ToResponse, dto => dto.RowVersion);
    }
}
