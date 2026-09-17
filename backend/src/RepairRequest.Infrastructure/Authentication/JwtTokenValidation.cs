using System.Diagnostics.CodeAnalysis;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace RepairRequest.Infrastructure.Authentication;

/// <summary>
/// Single source of JWT validation rules, shared by token issuance and the API's
/// JWT bearer handler so both always agree on algorithm, key, issuer and audience.
/// </summary>
public static class JwtTokenValidation
{
    public const string RoleClaimType = "role";

    public static TokenValidationParameters CreateParameters(JwtOptions options) => new()
    {
        ValidateIssuer = true,
        ValidIssuer = options.Issuer,
        ValidateAudience = true,
        ValidAudience = options.Audience,
        ValidateLifetime = true,
        RequireExpirationTime = true,

        // Issuer and validator share one clock; no tolerance beyond the approved 15-minute lifetime.
        ClockSkew = TimeSpan.Zero,

        ValidateIssuerSigningKey = true,
        RequireSignedTokens = true,
        IssuerSigningKey = CreateSigningKey(options),
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256],

        NameClaimType = JwtRegisteredClaimNames.Sub,
        RoleClaimType = RoleClaimType
    };

    internal static SymmetricSecurityKey CreateSigningKey(JwtOptions options) =>
        TryDecodeSigningKey(options.SigningKey, out var keyBytes)
            ? new SymmetricSecurityKey(keyBytes)
            : throw new InvalidOperationException($"{JwtOptions.SectionName}:SigningKey is not configured correctly.");

    internal static bool TryDecodeSigningKey(string? value, [NotNullWhen(true)] out byte[]? keyBytes)
    {
        keyBytes = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var buffer = new byte[value.Length * 3 / 4];
        if (!Convert.TryFromBase64String(value, buffer, out var written))
        {
            return false;
        }

        keyBytes = buffer[..written];
        return true;
    }
}
