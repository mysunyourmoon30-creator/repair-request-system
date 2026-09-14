using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Mvc;
using RepairRequest.Api.Http;
using RepairRequest.Application.Common;
using RepairRequest.Application.Security;

namespace RepairRequest.Api.Controllers;

/// <summary>
/// HTTP mapping shared by command-style controllers: caller resolution, If-Match / ETag and the RR-API-001 error
/// contract. It holds no authorization or business logic; role capability comes from the action policies and
/// scope/rules from the Application services.
/// </summary>
public abstract class CommandControllerBase : ControllerBase
{
    private readonly ICurrentUserAccessor _currentUserAccessor;

    protected CommandControllerBase(ICurrentUserAccessor currentUserAccessor)
    {
        _currentUserAccessor = currentUserAccessor;
    }

    /// <summary>The fallback policy has already resolved the caller from the identity store.</summary>
    protected async Task<CurrentUser> CallerAsync(CancellationToken cancellationToken) =>
        await _currentUserAccessor.GetAsync(cancellationToken)
        ?? throw new InvalidOperationException("The authorization policy guarantees a resolved caller.");

    protected async Task<CommandContext> CommandContextAsync(CancellationToken cancellationToken) =>
        new(await CallerAsync(cancellationToken), CorrelationIdResolver.Resolve(HttpContext));

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

    /// <summary>Maps a command result; <paramref name="rowVersion"/> is null for resources without an ETag.</summary>
    protected IActionResult CommandResult<TDto, TResponse>(
        CommandResult<TDto> result,
        string resourceType,
        Func<TDto, TResponse> map,
        Func<TDto, byte[]>? rowVersion,
        Func<TDto, string>? createdLocation = null)
    {
        if (result.Succeeded)
        {
            var value = result.Value!;
            if (rowVersion is not null)
            {
                Response.Headers.ETag = ETagHeader.Format(rowVersion(value));
            }

            return createdLocation is null ? Ok(map(value)) : Created(createdLocation(value), map(value));
        }

        return ProblemFor(result.Error!, resourceType);
    }

    /// <summary>RR-API-001 section 2 problem response for a controlled command failure.</summary>
    protected ObjectResult ProblemFor(CommandError error, string resourceType) =>
        error.Failure switch
        {
            CommandFailure.NotFound => ApiProblemResults.ResourceNotFound(HttpContext, resourceType),
            CommandFailure.ValidationFailed => ApiProblemResults.ValidationFailed(HttpContext, error.Errors, error.Message),
            CommandFailure.StateConflict => ApiProblemResults.Conflict(HttpContext, ApiProblemResults.StateConflictCode, error.Message, error.ActiveChildCount),
            _ => ApiProblemResults.Conflict(HttpContext, ApiProblemResults.ConcurrencyConflictCode, error.Message)
        };
}
