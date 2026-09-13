using Microsoft.IdentityModel.JsonWebTokens;
using RepairRequest.Application.Security;

namespace RepairRequest.Api.Authorization;

/// <summary>
/// Resolves the caller from the validated token's subject, then loads tenant and roles from the
/// identity store once per request. Role claims carried in the token are deliberately ignored.
/// </summary>
internal sealed class HttpCurrentUserAccessor : ICurrentUserAccessor
{
    private static readonly object CacheKey = new();

    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ICurrentUserStore _currentUserStore;

    public HttpCurrentUserAccessor(IHttpContextAccessor httpContextAccessor, ICurrentUserStore currentUserStore)
    {
        _httpContextAccessor = httpContextAccessor;
        _currentUserStore = currentUserStore;
    }

    public async Task<CurrentUser?> GetAsync(CancellationToken cancellationToken)
    {
        var httpContext = _httpContextAccessor.HttpContext;
        if (httpContext?.User.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        if (httpContext.Items.TryGetValue(CacheKey, out var cached))
        {
            return cached as CurrentUser;
        }

        var subject = httpContext.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        var currentUser = Guid.TryParse(subject, out var userId)
            ? await _currentUserStore.FindAsync(userId, cancellationToken)
            : null;

        httpContext.Items[CacheKey] = currentUser;
        return currentUser;
    }
}
