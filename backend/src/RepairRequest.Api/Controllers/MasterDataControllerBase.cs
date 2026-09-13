using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using RepairRequest.Api.Contracts.MasterData;
using RepairRequest.Api.Http;
using RepairRequest.Application.MasterData;
using RepairRequest.Application.Security;

namespace RepairRequest.Api.Controllers;

/// <summary>
/// HTTP mapping shared by the master-data controllers: caller resolution, paging, If-Match / ETag and the
/// RR-API-001 error contract. It holds no authorization or business logic; role capability comes from the
/// action policies and scope/rules from the Application services.
/// </summary>
public abstract class MasterDataControllerBase : ControllerBase
{
    private readonly ICurrentUserAccessor _currentUserAccessor;
    private readonly PagingOptions _paging;

    protected MasterDataControllerBase(ICurrentUserAccessor currentUserAccessor, IOptions<PagingOptions> paging)
    {
        _currentUserAccessor = currentUserAccessor;
        _paging = paging.Value;
    }

    /// <summary>The fallback policy has already resolved the caller from the identity store.</summary>
    protected async Task<CurrentUser> CallerAsync(CancellationToken cancellationToken) =>
        await _currentUserAccessor.GetAsync(cancellationToken)
        ?? throw new InvalidOperationException("The authorization policy guarantees a resolved caller.");

    protected async Task<MasterDataCommandContext> CommandContextAsync(CancellationToken cancellationToken) =>
        new(await CallerAsync(cancellationToken), CorrelationIdResolver.Resolve(HttpContext));

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

    /// <summary>State-changing/update commands require one strong If-Match rowversion token.</summary>
    protected bool TryReadIfMatch(out byte[] rowVersion, [NotNullWhen(false)] out ObjectResult? problem)
    {
        if (ETagHeader.TryReadIfMatch(Request, out rowVersion))
        {
            problem = null;
            return true;
        }

        problem = ApiProblemResults.BadRequest(HttpContext, "A single If-Match header with the current ETag is required.");
        return false;
    }

    protected OkObjectResult PageResult<TDto, TResponse>(PagedResult<TDto> page, Func<TDto, TResponse> map) =>
        Ok(new PagedResponse<TResponse>(page.Items.Select(map).ToList(), page.Page, page.PageSize, page.TotalCount));

    protected IActionResult DetailResult<TDto, TResponse>(TDto? dto, string resourceType, Func<TDto, TResponse> map, Func<TDto, byte[]> rowVersion)
        where TDto : class
    {
        if (dto is null)
        {
            return ApiProblemResults.ResourceNotFound(HttpContext, resourceType);
        }

        Response.Headers.ETag = ETagHeader.Format(rowVersion(dto));
        return Ok(map(dto));
    }

    protected IActionResult CommandResult<TDto, TResponse>(
        MasterDataResult<TDto> result,
        string resourceType,
        Func<TDto, TResponse> map,
        Func<TDto, byte[]> rowVersion,
        Func<TDto, string>? createdLocation = null)
    {
        if (result.Succeeded)
        {
            var value = result.Value!;
            Response.Headers.ETag = ETagHeader.Format(rowVersion(value));
            return createdLocation is null ? Ok(map(value)) : Created(createdLocation(value), map(value));
        }

        var error = result.Error!;
        return error.Failure switch
        {
            MasterDataFailure.NotFound => ApiProblemResults.ResourceNotFound(HttpContext, resourceType),
            MasterDataFailure.ValidationFailed => ApiProblemResults.ValidationFailed(HttpContext, error.Errors, error.Message),
            MasterDataFailure.StateConflict => ApiProblemResults.Conflict(HttpContext, ApiProblemResults.StateConflictCode, error.Message, error.ActiveChildCount),
            _ => ApiProblemResults.Conflict(HttpContext, ApiProblemResults.ConcurrencyConflictCode, error.Message)
        };
    }
}
