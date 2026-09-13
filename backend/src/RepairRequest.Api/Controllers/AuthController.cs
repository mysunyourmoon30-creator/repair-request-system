using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RepairRequest.Api.Contracts.Authentication;
using RepairRequest.Api.Http;
using RepairRequest.Application.Authentication;

namespace RepairRequest.Api.Controllers;

/// <summary>
/// Authentication endpoints (DEC-PS1-004). All authentication failures return the same generic
/// 401 problem response. The refresh token travels only in an HttpOnly cookie scoped to this route.
/// No self-registration, password reset or tenant/site authorization is provided here.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/v1/auth")]
public sealed class AuthController : ControllerBase
{
    private const string RefreshTokenCookieName = "__Secure-rr-refresh-token";
    private const string RefreshTokenCookiePath = "/api/v1/auth";

    private readonly IAuthenticationService _authenticationService;

    public AuthController(IAuthenticationService authenticationService)
    {
        _authenticationService = authenticationService;
    }

    [HttpPost("login")]
    [ProducesResponseType<AccessTokenResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken cancellationToken)
    {
        var correlationId = CorrelationIdResolver.Resolve(HttpContext);
        PreventCaching();

        var result = await _authenticationService.LoginAsync(request.Email, request.Password, correlationId, cancellationToken);

        return result.Succeeded ? IssueTokens(result.Tokens) : AuthenticationFailed(correlationId);
    }

    [HttpPost("refresh")]
    [ProducesResponseType<AccessTokenResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Refresh(CancellationToken cancellationToken)
    {
        var correlationId = CorrelationIdResolver.Resolve(HttpContext);
        PreventCaching();

        if (!Request.Cookies.TryGetValue(RefreshTokenCookieName, out var refreshToken) || string.IsNullOrEmpty(refreshToken))
        {
            return AuthenticationFailed(correlationId);
        }

        var result = await _authenticationService.RefreshAsync(refreshToken, correlationId, cancellationToken);

        return result.Succeeded ? IssueTokens(result.Tokens) : AuthenticationFailed(correlationId);
    }

    [HttpPost("revoke")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Revoke(CancellationToken cancellationToken)
    {
        var correlationId = CorrelationIdResolver.Resolve(HttpContext);
        PreventCaching();

        if (Request.Cookies.TryGetValue(RefreshTokenCookieName, out var refreshToken) && !string.IsNullOrEmpty(refreshToken))
        {
            await _authenticationService.RevokeAsync(refreshToken, correlationId, cancellationToken);
        }

        ClearRefreshTokenCookie();
        return NoContent();
    }

    private OkObjectResult IssueTokens(IssuedTokens tokens)
    {
        Response.Cookies.Append(RefreshTokenCookieName, tokens.RefreshToken, CreateCookieOptions(tokens.RefreshTokenExpiresAt));
        return Ok(new AccessTokenResponse(tokens.AccessToken, "Bearer", tokens.AccessTokenExpiresAt));
    }

    private ObjectResult AuthenticationFailed(Guid correlationId)
    {
        ClearRefreshTokenCookie();

        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status401Unauthorized,
            Title = "Authentication failed.",
            Type = "https://tools.ietf.org/html/rfc9110#section-15.5.2"
        };
        problem.Extensions["code"] = "UNAUTHENTICATED";
        problem.Extensions["correlationId"] = correlationId;

        return new ObjectResult(problem)
        {
            StatusCode = StatusCodes.Status401Unauthorized,
            ContentTypes = { "application/problem+json" }
        };
    }

    private void ClearRefreshTokenCookie() =>
        Response.Cookies.Delete(RefreshTokenCookieName, CreateCookieOptions(expiresAt: null));

    private static CookieOptions CreateCookieOptions(DateTimeOffset? expiresAt) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = RefreshTokenCookiePath,
        Expires = expiresAt,
        IsEssential = true
    };

    private void PreventCaching()
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";
    }
}
