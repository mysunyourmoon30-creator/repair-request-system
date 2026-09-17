using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using RepairRequest.Domain.Security;
using RepairRequest.Infrastructure.Authentication;

namespace RepairRequest.ApiTests;

/// <summary>JWT bearer validation as configured by the API host (decisions B3/M6).</summary>
public class AccessTokenValidationTests : IClassFixture<ApiHostFactory>
{
    private readonly ApiHostFactory _factory;

    public AccessTokenValidationTests(ApiHostFactory factory)
    {
        _factory = factory;
    }

    private JwtOptions Options => _factory.Services.GetRequiredService<IOptions<JwtOptions>>().Value;

    private string CreateToken(Action<SecurityTokenDescriptor>? customize = null)
    {
        var now = DateTime.UtcNow;
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Options.Issuer,
            Audience = Options.Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddMinutes(15),
            Subject = new ClaimsIdentity([new Claim("sub", Guid.NewGuid().ToString()), new Claim("role", RoleCodes.Requester)]),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Convert.FromBase64String(_factory.SigningKey)),
                SecurityAlgorithms.HmacSha256)
        };

        customize?.Invoke(descriptor);
        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(descriptor);
    }

    private Task<Microsoft.AspNetCore.Authentication.AuthenticateResult> AuthenticateAsync(string? token) =>
        BearerAuthentication.AuthenticateAsync(_factory.Services, token);

    [Fact]
    public async Task ValidToken_Authenticates_WithSubjectAndRole()
    {
        var result = await AuthenticateAsync(CreateToken());

        Assert.True(result.Succeeded);
        Assert.True(result.Principal!.IsInRole(RoleCodes.Requester));
        Assert.False(string.IsNullOrEmpty(result.Principal.Identity!.Name));
    }

    [Fact]
    public async Task ExpiredToken_IsRejected_WithNoClockSkewTolerance()
    {
        var token = CreateToken(descriptor =>
        {
            descriptor.IssuedAt = DateTime.UtcNow.AddMinutes(-16);
            descriptor.NotBefore = descriptor.IssuedAt;
            descriptor.Expires = DateTime.UtcNow.AddMinutes(-1);
        });

        Assert.False((await AuthenticateAsync(token)).Succeeded);
    }

    [Fact]
    public async Task TokenWithoutExpiry_IsRejected()
    {
        Assert.False((await AuthenticateAsync(CreateToken(descriptor => descriptor.Expires = null))).Succeeded);
    }

    [Fact]
    public async Task WrongIssuer_IsRejected()
    {
        Assert.False((await AuthenticateAsync(CreateToken(descriptor => descriptor.Issuer = "https://attacker.example"))).Succeeded);
    }

    [Fact]
    public async Task WrongAudience_IsRejected()
    {
        Assert.False((await AuthenticateAsync(CreateToken(descriptor => descriptor.Audience = "another-api"))).Succeeded);
    }

    [Fact]
    public async Task TokenSignedWithDifferentKey_IsRejected()
    {
        var token = CreateToken(descriptor => descriptor.SigningCredentials = new SigningCredentials(
            new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32)),
            SecurityAlgorithms.HmacSha256));

        Assert.False((await AuthenticateAsync(token)).Succeeded);
    }

    [Fact]
    public async Task TamperedPayload_IsRejected()
    {
        var parts = CreateToken().Split('.');
        var payload = JsonNode.Parse(Base64UrlEncoder.Decode(parts[1]))!.AsObject();
        payload["role"] = RoleCodes.Administrator;
        parts[1] = Base64UrlEncoder.Encode(payload.ToJsonString());

        Assert.False((await AuthenticateAsync(string.Join('.', parts))).Succeeded);
    }

    [Fact]
    public async Task UnsignedToken_IsRejected()
    {
        Assert.False((await AuthenticateAsync(CreateToken(descriptor => descriptor.SigningCredentials = null))).Succeeded);
    }

    [Fact]
    public async Task MalformedToken_IsRejected()
    {
        Assert.False((await AuthenticateAsync("not-a-jwt")).Succeeded);
    }

    [Fact]
    public async Task MissingAuthorizationHeader_ProducesNoResult()
    {
        Assert.True((await AuthenticateAsync(null)).None);
    }
}
