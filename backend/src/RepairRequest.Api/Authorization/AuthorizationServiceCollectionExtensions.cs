using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using RepairRequest.Application.Security;

namespace RepairRequest.Api.Authorization;

/// <summary>
/// S1-003 authorization composition: deny by default (every endpoint requires an authenticated caller
/// that resolves to an existing user unless explicitly anonymous), plus one named policy per role
/// capability from <see cref="AuthorizationPolicies"/>. Roles are evaluated from the identity store.
/// </summary>
public static class AuthorizationServiceCollectionExtensions
{
    public static IServiceCollection AddRepairRequestAuthorization(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUserAccessor, HttpCurrentUserAccessor>();
        services.AddScoped<IAuthorizationHandler, ResolvedUserAuthorizationHandler>();
        services.AddScoped<IAuthorizationHandler, RoleCapabilityAuthorizationHandler>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, ProblemAuthorizationResultHandler>();

        var authorization = services.AddAuthorizationBuilder()
            .SetDefaultPolicy(ResolvedUserPolicy())
            .SetFallbackPolicy(ResolvedUserPolicy());

        foreach (var (policyName, allowedRoleCodes) in AuthorizationPolicies.AllowedRoles)
        {
            authorization.AddPolicy(policyName, policy => policy
                .RequireAuthenticatedUser()
                .AddRequirements(ResolvedUserRequirement.Instance, new RoleCapabilityRequirement(allowedRoleCodes)));
        }

        return services;
    }

    private static AuthorizationPolicy ResolvedUserPolicy() =>
        new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .AddRequirements(ResolvedUserRequirement.Instance)
            .Build();
}
