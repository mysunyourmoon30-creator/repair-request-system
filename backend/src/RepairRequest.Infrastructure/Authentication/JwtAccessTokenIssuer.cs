using System.Security.Claims;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace RepairRequest.Infrastructure.Authentication;

/// <summary>
/// Issues HS256 access tokens (S1-002 decisions B3/M6). Claims are limited to identity and
/// role: sub, jti, iat/nbf/exp, iss/aud and role codes. No email, tenant, site or other
/// business data is placed in the token; data scope stays server-side (RR-ARCH-001 section 10).
/// </summary>
internal sealed class JwtAccessTokenIssuer
{
    private readonly JsonWebTokenHandler _handler = new() { SetDefaultTimesOnTokenCreation = false };
    private readonly JwtOptions _options;
    private readonly SigningCredentials _signingCredentials;
    private readonly TimeProvider _timeProvider;

    public JwtAccessTokenIssuer(IOptions<JwtOptions> options, TimeProvider timeProvider)
    {
        _options = options.Value;
        _signingCredentials = new SigningCredentials(JwtTokenValidation.CreateSigningKey(_options), SecurityAlgorithms.HmacSha256);
        _timeProvider = timeProvider;
    }

    public IssuedAccessToken Issue(Guid userId, IReadOnlyCollection<string> roleCodes)
    {
        // JWT time claims have one-second resolution; align the reported expiry with the token.
        var issuedAt = DateTimeOffset.FromUnixTimeSeconds(_timeProvider.GetUtcNow().ToUnixTimeSeconds());
        var expiresAt = issuedAt + _options.AccessTokenLifetime;

        var subject = new ClaimsIdentity(
        [
            new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        ]);

        foreach (var roleCode in roleCodes)
        {
            subject.AddClaim(new Claim(JwtTokenValidation.RoleClaimType, roleCode));
        }

        var token = _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            Subject = subject,
            IssuedAt = issuedAt.UtcDateTime,
            NotBefore = issuedAt.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            SigningCredentials = _signingCredentials
        });

        return new IssuedAccessToken(token, expiresAt);
    }
}

internal readonly record struct IssuedAccessToken(string Token, DateTimeOffset ExpiresAt)
{
    public override string ToString() => $"IssuedAccessToken {{ ExpiresAt = {ExpiresAt:O} }}";
}
