using System.Diagnostics.CodeAnalysis;

namespace RepairRequest.Application.Authentication;

/// <summary>Outcome of a login or refresh. A failure carries no reason by design.</summary>
public sealed class AuthenticationResult
{
    private AuthenticationResult(IssuedTokens? tokens)
    {
        Tokens = tokens;
    }

    public static AuthenticationResult Failed { get; } = new(null);

    public static AuthenticationResult Success(IssuedTokens tokens) =>
        new(tokens ?? throw new ArgumentNullException(nameof(tokens)));

    [MemberNotNullWhen(true, nameof(Tokens))]
    public bool Succeeded => Tokens is not null;

    public IssuedTokens? Tokens { get; }
}

/// <summary>
/// Token pair returned to the API layer. The refresh token is plaintext only in memory
/// so the API can place it in an HttpOnly cookie; only its hash is persisted.
/// </summary>
public sealed record IssuedTokens(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt)
{
    /// <summary>Never render token values (e.g. when an instance reaches a log or debugger).</summary>
    public override string ToString() =>
        $"IssuedTokens {{ AccessTokenExpiresAt = {AccessTokenExpiresAt:O}, RefreshTokenExpiresAt = {RefreshTokenExpiresAt:O} }}";
}
