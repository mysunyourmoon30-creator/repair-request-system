using System.Buffers.Text;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using RepairRequest.Application.Authentication;
using RepairRequest.Domain.Security;
using RepairRequest.Infrastructure.Identity;
using RepairRequest.Infrastructure.Persistence;
using RepairRequest.IntegrationTests.Persistence;

namespace RepairRequest.IntegrationTests.Authentication;

/// <summary>
/// Login, lockout, JWT issuance and refresh-token rotation / reuse detection / revocation
/// against the real Identity stores and LocalDB schema (S1-002 decisions B1-B4, M1-M5).
/// </summary>
[Collection(PersistenceDatabaseCollection.Name)]
public sealed class AuthenticationServiceTests : IAsyncLifetime
{
    private const string WrongPassword = "Wrong-Password-99!";

    private readonly AuthenticationTestHost _host = new();

    public AuthenticationServiceTests(PersistenceDatabaseFixture database)
    {
        _ = database;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private async Task<AuthenticationResult> LoginAsync(string email, string password, Guid? correlationId = null)
    {
        await using var scope = _host.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IAuthenticationService>()
            .LoginAsync(email, password, correlationId ?? Guid.NewGuid(), CancellationToken.None);
    }

    private async Task<AuthenticationResult> RefreshAsync(string refreshToken)
    {
        await using var scope = _host.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IAuthenticationService>()
            .RefreshAsync(refreshToken, Guid.NewGuid(), CancellationToken.None);
    }

    private async Task RevokeAsync(string refreshToken)
    {
        await using var scope = _host.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IAuthenticationService>()
            .RevokeAsync(refreshToken, Guid.NewGuid(), CancellationToken.None);
    }

    private async Task<List<RefreshToken>> TokensOfAsync(Guid userId)
    {
        await using var scope = _host.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<RepairRequestDbContext>().RefreshTokens
            .AsNoTracking()
            .Where(token => token.UserId == userId)
            .ToListAsync();
    }

    private async Task<List<(string Code, string? Details)>> AuditOfAsync(Guid userId)
    {
        await using var scope = _host.CreateScope();
        var rows = await scope.ServiceProvider.GetRequiredService<RepairRequestDbContext>().AuditHistory
            .AsNoTracking()
            .Where(audit => audit.EntityType == AuthenticationEventCodes.AuditEntityType && audit.EntityId == userId)
            .Select(audit => new { audit.ActionCode, audit.NewValueJson })
            .ToListAsync();
        return rows.Select(row => (row.ActionCode, row.NewValueJson)).ToList();
    }

    private async Task<ApplicationUser> ReloadUserAsync(Guid userId)
    {
        await using var scope = _host.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByIdAsync(userId.ToString()))!;
    }

    private static byte[] HashOf(string refreshToken) => SHA256.HashData(Base64Url.DecodeFromChars(refreshToken));

    // ---------------- Login / JWT ----------------

    [Fact]
    public async Task Login_WithValidCredentials_IssuesHs256AccessTokenWithOnlyApprovedClaims()
    {
        var user = await _host.CreateUserAsync(RoleCodes.Requester, RoleCodes.Approver);

        var result = await LoginAsync(user.Email!, AuthenticationTestHost.ValidPassword);

        Assert.True(result.Succeeded);
        var validation = await new JsonWebTokenHandler().ValidateTokenAsync(result.Tokens.AccessToken, _host.ValidationParameters);
        Assert.True(validation.IsValid, validation.Exception?.Message);

        var jwt = Assert.IsType<JsonWebToken>(validation.SecurityToken);
        Assert.Equal("HS256", jwt.Alg);
        Assert.Equal(user.Id.ToString(), jwt.Subject);
        Assert.Equal(AuthenticationTestHost.Issuer, jwt.Issuer);
        Assert.Equal(TimeSpan.FromMinutes(15), jwt.ValidTo - jwt.IssuedAt);
        Assert.Equal(
            [RoleCodes.Approver, RoleCodes.Requester],
            jwt.Claims.Where(claim => claim.Type == "role").Select(claim => claim.Value).Order());
        Assert.Empty(jwt.Claims.Select(claim => claim.Type).Distinct()
            .Except(["sub", "jti", "iat", "nbf", "exp", "iss", "aud", "role"]));
        Assert.DoesNotContain(jwt.Claims, claim => claim.Value.Contains(user.Email!, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Login_PersistsOnlySha256OfRandomRefreshToken_WithSevenDayLifetime()
    {
        var user = await _host.CreateUserAsync();

        var result = await LoginAsync(user.Email!, AuthenticationTestHost.ValidPassword);

        Assert.True(result.Succeeded);
        Assert.Equal(32, Base64Url.DecodeFromChars(result.Tokens.RefreshToken).Length);

        var stored = Assert.Single(await TokensOfAsync(user.Id));
        Assert.Equal(HashOf(result.Tokens.RefreshToken), stored.TokenHash);
        Assert.Equal(user.TenantId, stored.TenantId);
        Assert.Equal(TimeSpan.FromDays(7), stored.ExpiresAt - stored.CreatedAt);
        Assert.Equal(result.Tokens.RefreshTokenExpiresAt.UtcDateTime, stored.ExpiresAt);
        Assert.Null(stored.RevokedAt);
        Assert.Null(stored.ReplacedByTokenId);
    }

    [Fact]
    public async Task Login_Twice_IssuesDistinctTokensInSeparateFamilies()
    {
        var user = await _host.CreateUserAsync();

        var first = await LoginAsync(user.Email!, AuthenticationTestHost.ValidPassword);
        var second = await LoginAsync(user.Email!, AuthenticationTestHost.ValidPassword);

        Assert.NotEqual(first.Tokens!.RefreshToken, second.Tokens!.RefreshToken);
        Assert.NotEqual(first.Tokens.AccessToken, second.Tokens.AccessToken);
        Assert.Equal(2, (await TokensOfAsync(user.Id)).Select(token => token.FamilyId).Distinct().Count());
    }

    [Fact]
    public async Task Login_EmailComparisonIsCaseInsensitive()
    {
        var user = await _host.CreateUserAsync();

        var result = await LoginAsync(user.Email!.ToUpperInvariant(), AuthenticationTestHost.ValidPassword);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Login_WithWrongPassword_FailsAndRecordsAuditEvent()
    {
        var user = await _host.CreateUserAsync();

        var result = await LoginAsync(user.Email!, WrongPassword);

        Assert.False(result.Succeeded);
        Assert.Null(result.Tokens);
        Assert.Equal(1, (await ReloadUserAsync(user.Id)).AccessFailedCount);
        Assert.Empty(await TokensOfAsync(user.Id));

        var audit = Assert.Single(await AuditOfAsync(user.Id));
        Assert.Equal(AuthenticationEventCodes.LoginFailed, audit.Code);
        Assert.Contains("\"result\":\"FAILURE\"", audit.Details);
    }

    [Fact]
    public async Task Login_WithUnknownEmail_FailsAndLogsEventCodeWithoutIdentity()
    {
        var email = $"missing-{Guid.NewGuid():N}@example.test";

        var result = await LoginAsync(email, AuthenticationTestHost.ValidPassword);

        Assert.False(result.Succeeded);
        Assert.Contains(_host.Logs.Entries, entry => entry.Contains(AuthenticationEventCodes.LoginFailed));
        Assert.DoesNotContain(_host.Logs.Entries, entry => entry.Contains(email, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Login_FifthFailedAttempt_LocksAccountForFifteenMinutes_AndRejectsCorrectPassword()
    {
        var user = await _host.CreateUserAsync();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            Assert.False((await LoginAsync(user.Email!, WrongPassword)).Succeeded);
        }

        var locked = await ReloadUserAsync(user.Id);
        Assert.NotNull(locked.LockoutEnd);
        var remaining = locked.LockoutEnd.Value - DateTimeOffset.UtcNow;
        Assert.InRange(remaining, TimeSpan.FromMinutes(14), TimeSpan.FromMinutes(15.5));

        Assert.False((await LoginAsync(user.Email!, AuthenticationTestHost.ValidPassword)).Succeeded);

        var codes = (await AuditOfAsync(user.Id)).Select(audit => audit.Code).ToList();
        Assert.Equal(5, codes.Count(code => code == AuthenticationEventCodes.LoginFailed));
        Assert.Single(codes, AuthenticationEventCodes.AccountLockedOut);
        Assert.Single(codes, AuthenticationEventCodes.LoginRejectedLockedOut);
        Assert.Empty(await TokensOfAsync(user.Id));
    }

    [Fact]
    public async Task Login_AfterLockoutEnds_Succeeds()
    {
        var user = await _host.CreateUserAsync();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await LoginAsync(user.Email!, WrongPassword);
        }

        await using (var scope = _host.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var tracked = (await userManager.FindByIdAsync(user.Id.ToString()))!;
            await userManager.SetLockoutEndDateAsync(tracked, DateTimeOffset.UtcNow.AddMinutes(-5));
        }

        Assert.True((await LoginAsync(user.Email!, AuthenticationTestHost.ValidPassword)).Succeeded);
    }

    [Fact]
    public async Task Login_Success_ResetsFailedAccessCount()
    {
        var user = await _host.CreateUserAsync();
        await LoginAsync(user.Email!, WrongPassword);
        await LoginAsync(user.Email!, WrongPassword);
        Assert.Equal(2, (await ReloadUserAsync(user.Id)).AccessFailedCount);

        Assert.True((await LoginAsync(user.Email!, AuthenticationTestHost.ValidPassword)).Succeeded);

        Assert.Equal(0, (await ReloadUserAsync(user.Id)).AccessFailedCount);
        Assert.Contains((await AuditOfAsync(user.Id)).Select(audit => audit.Code), code => code == AuthenticationEventCodes.LoginSucceeded);
    }

    // ---------------- Refresh / rotation / reuse ----------------

    [Fact]
    public async Task Refresh_WithValidToken_RotatesWithinTheSameFamily()
    {
        var user = await _host.CreateUserAsync(RoleCodes.Technician);
        var login = await LoginAsync(user.Email!, AuthenticationTestHost.ValidPassword);

        var refreshed = await RefreshAsync(login.Tokens!.RefreshToken);

        Assert.True(refreshed.Succeeded);
        Assert.NotEqual(login.Tokens.RefreshToken, refreshed.Tokens.RefreshToken);
        Assert.True((await new JsonWebTokenHandler().ValidateTokenAsync(refreshed.Tokens.AccessToken, _host.ValidationParameters)).IsValid);

        var tokens = await TokensOfAsync(user.Id);
        var original = tokens.Single(token => token.TokenHash.SequenceEqual(HashOf(login.Tokens.RefreshToken)));
        var replacement = tokens.Single(token => token.TokenHash.SequenceEqual(HashOf(refreshed.Tokens.RefreshToken)));

        Assert.NotNull(original.RevokedAt);
        Assert.Equal(replacement.Id, original.ReplacedByTokenId);
        Assert.Null(replacement.RevokedAt);
        Assert.Equal(original.FamilyId, replacement.FamilyId);
        Assert.Contains((await AuditOfAsync(user.Id)).Select(audit => audit.Code), code => code == AuthenticationEventCodes.TokenRefreshed);
    }

    [Fact]
    public async Task Refresh_WithAlreadyRotatedToken_IsReuse_AndRevokesTheWholeFamily()
    {
        var user = await _host.CreateUserAsync();
        var login = await LoginAsync(user.Email!, AuthenticationTestHost.ValidPassword);
        var rotated = await RefreshAsync(login.Tokens!.RefreshToken);
        Assert.True(rotated.Succeeded);

        var replay = await RefreshAsync(login.Tokens.RefreshToken);

        Assert.False(replay.Succeeded);
        Assert.False((await RefreshAsync(rotated.Tokens.RefreshToken)).Succeeded);
        Assert.All(await TokensOfAsync(user.Id), token => Assert.NotNull(token.RevokedAt));

        var reuse = Assert.Single(await AuditOfAsync(user.Id), audit => audit.Code == AuthenticationEventCodes.RefreshTokenReuseDetected);
        Assert.Contains("\"result\":\"FAILURE\"", reuse.Details);
    }

    [Fact]
    public async Task Refresh_WithRevokedToken_IsRejected()
    {
        var user = await _host.CreateUserAsync();
        var login = await LoginAsync(user.Email!, AuthenticationTestHost.ValidPassword);
        await RevokeAsync(login.Tokens!.RefreshToken);

        var result = await RefreshAsync(login.Tokens.RefreshToken);

        Assert.False(result.Succeeded);
        var rejected = Assert.Single(await AuditOfAsync(user.Id), audit => audit.Code == AuthenticationEventCodes.RefreshRejected);
        Assert.Contains("\"reason\":\"REVOKED\"", rejected.Details);
    }

    [Fact]
    public async Task Refresh_WithExpiredToken_IsRejected()
    {
        var user = await _host.CreateUserAsync();
        var login = await LoginAsync(user.Email!, AuthenticationTestHost.ValidPassword);

        _host.Clock.Advance(TimeSpan.FromDays(7));

        Assert.False((await RefreshAsync(login.Tokens!.RefreshToken)).Succeeded);
        var rejected = Assert.Single(await AuditOfAsync(user.Id), audit => audit.Code == AuthenticationEventCodes.RefreshRejected);
        Assert.Contains("\"reason\":\"EXPIRED\"", rejected.Details);
    }

    [Fact]
    public async Task Refresh_JustBeforeExpiry_Succeeds()
    {
        var user = await _host.CreateUserAsync();
        var login = await LoginAsync(user.Email!, AuthenticationTestHost.ValidPassword);

        _host.Clock.Advance(TimeSpan.FromDays(7) - TimeSpan.FromSeconds(1));

        Assert.True((await RefreshAsync(login.Tokens!.RefreshToken)).Succeeded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-refresh-token")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task Refresh_WithMalformedOrUnknownToken_IsRejected(string refreshToken)
    {
        Assert.False((await RefreshAsync(refreshToken)).Succeeded);
    }

    // ---------------- Revoke ----------------

    [Fact]
    public async Task Revoke_RevokesOnlyTheCurrentSessionFamily()
    {
        var user = await _host.CreateUserAsync();
        var sessionA = await LoginAsync(user.Email!, AuthenticationTestHost.ValidPassword);
        var sessionB = await LoginAsync(user.Email!, AuthenticationTestHost.ValidPassword);

        await RevokeAsync(sessionA.Tokens!.RefreshToken);

        Assert.False((await RefreshAsync(sessionA.Tokens.RefreshToken)).Succeeded);
        Assert.True((await RefreshAsync(sessionB.Tokens!.RefreshToken)).Succeeded);

        var revoked = Assert.Single(await AuditOfAsync(user.Id), audit => audit.Code == AuthenticationEventCodes.SessionRevoked);
        Assert.Contains("\"revokedTokenCount\":1", revoked.Details);
    }

    [Fact]
    public async Task Revoke_WithUnknownToken_IsIgnored()
    {
        await RevokeAsync(Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32)));
        await RevokeAsync("garbage");
    }

    // ---------------- Secret handling ----------------

    [Fact]
    public async Task LogsAndAudit_NeverContainPasswordsTokensOrEmail()
    {
        var user = await _host.CreateUserAsync(RoleCodes.Supervisor);
        var failed = await LoginAsync(user.Email!, WrongPassword);
        var login = await LoginAsync(user.Email!, AuthenticationTestHost.ValidPassword);
        var rotated = await RefreshAsync(login.Tokens!.RefreshToken);
        await RefreshAsync(login.Tokens.RefreshToken);
        await RevokeAsync(rotated.Tokens!.RefreshToken);
        Assert.False(failed.Succeeded);

        string[] secrets =
        [
            AuthenticationTestHost.ValidPassword,
            WrongPassword,
            login.Tokens.AccessToken,
            login.Tokens.RefreshToken,
            rotated.Tokens.AccessToken,
            rotated.Tokens.RefreshToken,
            _host.SigningKey,
            user.Email!
        ];

        var auditText = string.Join("\n", (await AuditOfAsync(user.Id)).Select(audit => audit.Details));

        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(_host.Logs.Entries, entry => entry.Contains(secret, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(secret, auditText, StringComparison.OrdinalIgnoreCase);
        }
    }
}
