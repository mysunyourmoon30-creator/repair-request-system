using Microsoft.AspNetCore.Authorization;
using RepairRequest.Application.Security;

namespace RepairRequest.Api.Authorization;

/// <summary>The token's subject must resolve to an existing user; otherwise the caller is treated as unauthenticated.</summary>
internal sealed class ResolvedUserRequirement : IAuthorizationRequirement
{
    private ResolvedUserRequirement()
    {
    }

    public static ResolvedUserRequirement Instance { get; } = new();
}

/// <summary>The caller's database-resolved roles must include at least one role allowed by the policy.</summary>
internal sealed class RoleCapabilityRequirement : IAuthorizationRequirement
{
    public RoleCapabilityRequirement(IReadOnlyCollection<string> allowedRoleCodes)
    {
        AllowedRoleCodes = allowedRoleCodes;
    }

    public IReadOnlyCollection<string> AllowedRoleCodes { get; }
}

internal sealed class ResolvedUserAuthorizationHandler : AuthorizationHandler<ResolvedUserRequirement>
{
    private readonly ICurrentUserAccessor _currentUserAccessor;

    public ResolvedUserAuthorizationHandler(ICurrentUserAccessor currentUserAccessor)
    {
        _currentUserAccessor = currentUserAccessor;
    }

    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, ResolvedUserRequirement requirement)
    {
        if (await _currentUserAccessor.GetAsync(RequestAborted(context)) is not null)
        {
            context.Succeed(requirement);
        }
    }

    internal static CancellationToken RequestAborted(AuthorizationHandlerContext context) =>
        (context.Resource as HttpContext)?.RequestAborted ?? CancellationToken.None;
}

internal sealed class RoleCapabilityAuthorizationHandler : AuthorizationHandler<RoleCapabilityRequirement>
{
    private readonly ICurrentUserAccessor _currentUserAccessor;

    public RoleCapabilityAuthorizationHandler(ICurrentUserAccessor currentUserAccessor)
    {
        _currentUserAccessor = currentUserAccessor;
    }

    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, RoleCapabilityRequirement requirement)
    {
        var currentUser = await _currentUserAccessor.GetAsync(ResolvedUserAuthorizationHandler.RequestAborted(context));
        if (currentUser is not null && currentUser.IsInAnyRole(requirement.AllowedRoleCodes))
        {
            context.Succeed(requirement);
        }
    }
}
