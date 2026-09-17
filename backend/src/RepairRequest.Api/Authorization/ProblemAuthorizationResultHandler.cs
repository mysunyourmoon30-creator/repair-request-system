using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using RepairRequest.Api.Http;
using RepairRequest.Application.Security;

namespace RepairRequest.Api.Authorization;

/// <summary>
/// Maps authorization failures to the S1-003 problem contract: an unauthenticated caller, or a token whose
/// subject no longer resolves, receives 401; an authenticated caller whose roles cannot perform the action
/// receives 403 before any record lookup happens. Object-level scope misses are returned by endpoints as 404.
/// </summary>
internal sealed class ProblemAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly ILogger<ProblemAuthorizationResultHandler> _logger;

    public ProblemAuthorizationResultHandler(ILogger<ProblemAuthorizationResultHandler> logger)
    {
        _logger = logger;
    }

    public async Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Succeeded)
        {
            await next(context);
            return;
        }

        var correlationId = CorrelationIdResolver.Resolve(context);
        var callerUnresolved =
            authorizeResult.Challenged
            || authorizeResult.AuthorizationFailure?.FailedRequirements.OfType<ResolvedUserRequirement>().Any() == true;

        if (callerUnresolved)
        {
            _logger.LogWarning(
                "Authorization event {EventCode} on {RouteTemplate}. CorrelationId: {CorrelationId}",
                AuthorizationEventCodes.Unauthenticated,
                ApiProblemResults.RouteTemplate(context),
                correlationId);

            await context.ChallengeAsync(JwtBearerDefaults.AuthenticationScheme);
            await ApiProblemResults.WriteAsync(context, StatusCodes.Status401Unauthorized);
            return;
        }

        _logger.LogWarning(
            "Authorization event {EventCode} for user {UserId} on {RouteTemplate}. CorrelationId: {CorrelationId}",
            AuthorizationEventCodes.AccessDenied,
            ApiProblemResults.SubjectOf(context),
            ApiProblemResults.RouteTemplate(context),
            correlationId);

        await ApiProblemResults.WriteAsync(context, StatusCodes.Status403Forbidden);
    }
}
