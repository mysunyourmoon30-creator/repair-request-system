namespace RepairRequest.Infrastructure.Authentication;

/// <summary>
/// JWT access token settings (S1-002 decisions B3/M6). Issuer, audience and lifetime are
/// non-secret configuration; <see cref="SigningKey"/> must come from user-secrets (local)
/// or a secret store (deployed) and is never committed.
/// </summary>
public sealed class JwtOptions
{
    public const string SectionName = "Authentication:Jwt";

    public string Issuer { get; set; } = string.Empty;

    public string Audience { get; set; } = string.Empty;

    /// <summary>Base64-encoded HS256 key of at least 32 bytes (256 bits).</summary>
    public string SigningKey { get; set; } = string.Empty;

    public TimeSpan AccessTokenLifetime { get; set; }
}

/// <summary>Refresh token settings (S1-002 decision B4).</summary>
public sealed class RefreshTokenOptions
{
    public const string SectionName = "Authentication:RefreshToken";

    public TimeSpan Lifetime { get; set; }
}
