using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using RepairRequest.Api.Contracts.WorkOrders;
using RepairRequest.Api.Http;
using RepairRequest.Application.Common;
using RepairRequest.Application.Security;
using RepairRequest.Application.WorkOrders;
using RepairRequest.Domain.WorkOrders;

namespace RepairRequest.Api.Controllers;

/// <summary>
/// Work Order List/Detail endpoints (S2-001; DEC-S2-001-01..05, `docs/12` Section 11). Both actions use the
/// WorkOrder.Read policy within the caller's data scope (<see cref="IDataScope.WorkOrders"/>). Convert, Schedule
/// and Reassign are out of scope; there is no write action on this controller.
/// </summary>
[ApiController]
[Route("api/v1/work-orders")]
public sealed class WorkOrdersController : CommandControllerBase
{
    private const string ResourceType = "WorkOrder";

    private readonly WorkOrderService _workOrders;
    private readonly PagingOptions _paging;

    public WorkOrdersController(WorkOrderService workOrders, ICurrentUserAccessor currentUserAccessor, IOptions<PagingOptions> paging)
        : base(currentUserAccessor)
    {
        _workOrders = workOrders;
        _paging = paging.Value;
    }

    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.WorkOrderRead)]
    [ProducesResponseType<PagedResponse<WorkOrderResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] WorkOrderListRequest request, CancellationToken cancellationToken)
    {
        if (!PageRequests.TryCreate(request.Page, request.PageSize, _paging, HttpContext, out var paging, out var problem))
        {
            return problem;
        }

        WorkOrderStatus? status = null;
        if (request.Status is not null)
        {
            if (!WorkOrderStatusCodes.TryParse(request.Status, out var parsed))
            {
                return ApiProblemResults.BadRequest(HttpContext, "status is not a recognised Work Order status.");
            }

            status = parsed;
        }

        var page = await _workOrders.ListAsync(await CallerAsync(cancellationToken), new WorkOrderListQuery(paging, status), cancellationToken);
        return Ok(new PagedResponse<WorkOrderResponse>(
            page.Items.Select(WorkOrderResponses.ToResponse).ToList(), page.Page, page.PageSize, page.TotalCount));
    }

    [HttpGet("{workOrderId:guid}")]
    [Authorize(Policy = AuthorizationPolicies.WorkOrderRead)]
    [ProducesResponseType<WorkOrderResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid workOrderId, CancellationToken cancellationToken)
    {
        var workOrder = await _workOrders.GetAsync(await CallerAsync(cancellationToken), workOrderId, cancellationToken);
        return DetailResult(workOrder, ResourceType, WorkOrderResponses.ToResponse, dto => dto.RowVersion);
    }
}
