using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.JsonWebTokens;
using RepairRequest.Application.Security;

namespace RepairRequest.Api.Http;

/// <summary>
/// Uniform, non-leaking problem responses for the RR-API-001 section 2 error contract: 400 BAD_REQUEST,
/// 401 UNAUTHENTICATED, 403 ACCESS_DENIED for a role that can never perform the action, one identical
/// 404 NOT_FOUND for out-of-scope and nonexistent objects, 409 STATE_CONFLICT / CONCURRENCY_CONFLICT and
/// 422 VALIDATION_FAILED. Security logs record the event code, caller subject and route template only;
/// never the raw path, attempted record ids, tokens, headers or cookies.
/// </summary>
public static class ApiProblemResults
{
    public const string StateConflictCode = "STATE_CONFLICT";
    public const string ConcurrencyConflictCode = "CONCURRENCY_CONFLICT";

    private const string LoggerCategory = "RepairRequest.Api.Authorization";

    /// <summary>
    /// Result for an object lookup that returned nothing through <see cref="IDataScope"/>. The same body
    /// is returned whether the record does not exist or is outside the caller's scope.
    /// </summary>
    public static ObjectResult ResourceNotFound(HttpContext httpContext, string resourceType)
    {
        var correlationId = CorrelationIdResolver.Resolve(httpContext);

        CreateLogger(httpContext).LogWarning(
            "Authorization event {EventCode}: {ResourceType} not found within caller scope on {RouteTemplate}. User: {UserId}. CorrelationId: {CorrelationId}",
            AuthorizationEventCodes.ResourceNotFound,
            resourceType,
            RouteTemplate(httpContext),
            SubjectOf(httpContext),
            correlationId);

        return ToResult(Create(StatusCodes.Status404NotFound, correlationId));
    }

    /// <summary>400 BAD_REQUEST for a malformed request, e.g. a missing or unusable If-Match header.</summary>
    public static ObjectResult BadRequest(HttpContext httpContext, string detail) =>
        ToResult(Create(StatusCodes.Status400BadRequest, CorrelationIdResolver.Resolve(httpContext), detail: detail));

    /// <summary>
    /// 422 VALIDATION_FAILED with a field -> messages map. A BR-14 duplicate warning adds <c>duplicateCount</c> only; no
    /// identifier of another Repair Request is ever returned (DEC-PRE-S1-007-10).
    /// </summary>
    public static ObjectResult ValidationFailed(HttpContext httpContext, IReadOnlyDictionary<string, string[]> errors, string? detail, int? duplicateCount = null)
    {
        var problem = Create(StatusCodes.Status422UnprocessableEntity, CorrelationIdResolver.Resolve(httpContext), detail: detail);
        problem.Extensions["errors"] = errors;
        if (duplicateCount is not null)
        {
            problem.Extensions["duplicateCount"] = duplicateCount;
        }

        return ToResult(problem);
    }

    /// <summary>409 STATE_CONFLICT or CONCURRENCY_CONFLICT; nothing was written.</summary>
    public static ObjectResult Conflict(HttpContext httpContext, string code, string? detail, int? activeChildCount = null)
    {
        var problem = Create(StatusCodes.Status409Conflict, CorrelationIdResolver.Resolve(httpContext), code, detail);
        if (activeChildCount is not null)
        {
            problem.Extensions["activeChildCount"] = activeChildCount;
        }

        return ToResult(problem);
    }

    internal static Task WriteAsync(HttpContext httpContext, int statusCode)
    {
        var correlationId = CorrelationIdResolver.Resolve(httpContext);
        httpContext.Response.StatusCode = statusCode;
        return httpContext.Response.WriteAsJsonAsync(
            Create(statusCode, correlationId),
            (JsonSerializerOptions?)null,
            "application/problem+json",
            httpContext.RequestAborted);
    }

    internal static string RouteTemplate(HttpContext httpContext) =>
        (httpContext.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "(unmatched)";

    internal static string? SubjectOf(HttpContext httpContext) =>
        httpContext.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;

    private static ProblemDetails Create(int statusCode, Guid correlationId, string? code = null, string? detail = null)
    {
        var (title, type, defaultCode) = statusCode switch
        {
            StatusCodes.Status400BadRequest => ("Malformed request.", "https://tools.ietf.org/html/rfc9110#section-15.5.1", "BAD_REQUEST"),
            StatusCodes.Status401Unauthorized => ("Authentication required.", "https://tools.ietf.org/html/rfc9110#section-15.5.2", "UNAUTHENTICATED"),
            StatusCodes.Status403Forbidden => ("Access denied.", "https://tools.ietf.org/html/rfc9110#section-15.5.4", "ACCESS_DENIED"),
            StatusCodes.Status404NotFound => ("Resource not found.", "https://tools.ietf.org/html/rfc9110#section-15.5.5", "NOT_FOUND"),
            StatusCodes.Status409Conflict => ("Conflict.", "https://tools.ietf.org/html/rfc9110#section-15.5.10", StateConflictCode),
            StatusCodes.Status422UnprocessableEntity => ("Validation failed.", "https://tools.ietf.org/html/rfc9110#section-15.5.21", "VALIDATION_FAILED"),
            _ => throw new ArgumentOutOfRangeException(nameof(statusCode))
        };

        var problem = new ProblemDetails { Status = statusCode, Title = title, Type = type, Detail = detail };
        problem.Extensions["code"] = code ?? defaultCode;
        problem.Extensions["correlationId"] = correlationId;
        return problem;
    }

    private static ObjectResult ToResult(ProblemDetails problem) =>
        new(problem)
        {
            StatusCode = problem.Status,
            ContentTypes = { "application/problem+json" }
        };

    private static ILogger CreateLogger(HttpContext httpContext) =>
        httpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(LoggerCategory);
}
