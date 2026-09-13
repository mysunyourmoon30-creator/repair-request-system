namespace RepairRequest.Application.Authentication;

/// <summary>
/// Authentication port (DEC-PS1-004): ASP.NET Core Identity credential check,
/// JWT access token issuance and refresh-token rotation/revocation.
/// Every failure is reported as a single generic outcome so callers cannot
/// distinguish unknown accounts, wrong passwords, lockout or token state.
/// </summary>
public interface IAuthenticationService
{
    Task<AuthenticationResult> LoginAsync(string email, string password, Guid correlationId, CancellationToken cancellationToken);

    /// <summary>Rotates a valid refresh token. Presenting an already-rotated token revokes its whole family.</summary>
    Task<AuthenticationResult> RefreshAsync(string refreshToken, Guid correlationId, CancellationToken cancellationToken);

    /// <summary>Revokes the token family (session) of the presented refresh token only. Unknown tokens are ignored.</summary>
    Task RevokeAsync(string refreshToken, Guid correlationId, CancellationToken cancellationToken);
}
