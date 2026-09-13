using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.JsonWebTokens;
using RepairRequest.Application.Security;

namespace RepairRequest.Api.Http;

/// <summary>
/// Uniform, non-leaking problem responses for authorization outcomes (RR-API-001 section 2;
/// S1-003 decision 3): 401 UNAUTHENTICATED, 403 ACCESS_DENIED for a role that can never perform
/// the action, and one identical 404 NOT_FOUND for out-of-scope and nonexistent objects.
/// Security logs record the event code, caller subject and route template only; never the raw
/// path, attempted record ids, tokens, headers or cookies.
/// </summary>
public static class ApiProblemResults
{
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

        return new ObjectResult(Create(StatusCodes.Status404NotFound, correlationId))
        {
            StatusCode = StatusCodes.Status404NotFound,
            ContentTypes = { "application/problem+json" }
        };
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

    private static ProblemDetails Create(int statusCode, Guid correlationId)
    {
        var (title, type, code) = statusCode switch
        {
            StatusCodes.Status401Unauthorized => ("Authentication required.", "https://tools.ietf.org/html/rfc9110#section-15.5.2", "UNAUTHENTICATED"),
            StatusCodes.Status403Forbidden => ("Access denied.", "https://tools.ietf.org/html/rfc9110#section-15.5.4", "ACCESS_DENIED"),
            StatusCodes.Status404NotFound => ("Resource not found.", "https://tools.ietf.org/html/rfc9110#section-15.5.5", "NOT_FOUND"),
            _ => throw new ArgumentOutOfRangeException(nameof(statusCode))
        };

        var problem = new ProblemDetails { Status = statusCode, Title = title, Type = type };
        problem.Extensions["code"] = code;
        problem.Extensions["correlationId"] = correlationId;
        return problem;
    }

    private static ILogger CreateLogger(HttpContext httpContext) =>
        httpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(LoggerCategory);
}
