using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using RepairRequest.Api.Contracts.MasterData;
using RepairRequest.Api.Http;
using RepairRequest.Application.MasterData;
using RepairRequest.Application.Security;

namespace RepairRequest.Api.Controllers;

/// <summary>
/// Master-data list paging on top of the shared command/ETag/error mapping in <see cref="CommandControllerBase"/>.
/// It holds no authorization or business logic; role capability comes from the action policies and scope/rules
/// from the Application services.
/// </summary>
public abstract class MasterDataControllerBase : CommandControllerBase
{
    private readonly PagingOptions _paging;

    protected MasterDataControllerBase(ICurrentUserAccessor currentUserAccessor, IOptions<PagingOptions> paging)
        : base(currentUserAccessor)
    {
        _paging = paging.Value;
    }

    protected bool TryCreateListQuery(
        MasterDataListRequest request,
        [NotNullWhen(true)] out MasterDataListQuery? query,
        [NotNullWhen(false)] out ObjectResult? problem)
    {
        query = null;
        problem = null;

        var page = request.Page ?? 1;
        var pageSize = request.PageSize ?? _paging.DefaultPageSize;

        if (page < 1)
        {
            problem = ApiProblemResults.BadRequest(HttpContext, "page must be 1 or greater.");
            return false;
        }

        if (pageSize < 1)
        {
            problem = ApiProblemResults.BadRequest(HttpContext, "pageSize must be 1 or greater.");
            return false;
        }

        Domain.MasterData.MasterDataStatus? status = null;
        if (request.Status is not null)
        {
            if (!MasterDataStatusCodes.TryParse(request.Status, out var parsed))
            {
                problem = ApiProblemResults.BadRequest(HttpContext, "status must be ACTIVE or INACTIVE.");
                return false;
            }

            status = parsed;
        }

        query = new MasterDataListQuery(new PageRequest(page, Math.Min(pageSize, _paging.MaxPageSize)), status);
        return true;
    }

    protected OkObjectResult PageResult<TDto, TResponse>(PagedResult<TDto> page, Func<TDto, TResponse> map) =>
        Ok(new PagedResponse<TResponse>(page.Items.Select(map).ToList(), page.Page, page.PageSize, page.TotalCount));
}
