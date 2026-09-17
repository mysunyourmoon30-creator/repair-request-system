using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RepairRequest.Infrastructure.Authentication;
using RepairRequest.Infrastructure.Identity;

namespace RepairRequest.IntegrationTests.Authentication;

/// <summary>
/// Approved identity policy values (B1/B2/M3) and fail-fast JWT configuration validation (M6).
/// No database access is required.
/// </summary>
public class AuthenticationConfigurationTests
{
    private static OptionsValidationException ResolveInvalid<TOptions>(Dictionary<string, string?> settings)
        where TOptions : class
    {
        using var provider = AuthenticationTestHost.BuildProvider(settings);
        return Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<TOptions>>().Value);
    }

    private static Dictionary<string, string?> ValidSettings() =>
        AuthenticationTestHost.CreateSettings(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));

    [Fact]
    public void ValidConfiguration_Resolves()
    {
        using var provider = AuthenticationTestHost.BuildProvider(ValidSettings());

        Assert.Equal(TimeSpan.FromMinutes(15), provider.GetRequiredService<IOptions<JwtOptions>>().Value.AccessTokenLifetime);
        Assert.Equal(TimeSpan.FromDays(7), provider.GetRequiredService<IOptions<RefreshTokenOptions>>().Value.Lifetime);
    }

    [Fact]
    public void MissingSigningKey_FailsValidation()
    {
        var exception = ResolveInvalid<JwtOptions>(AuthenticationTestHost.CreateSettings(signingKey: null));

        Assert.Contains("SigningKey", exception.Message);
    }

    [Fact]
    public void NonBase64SigningKey_FailsValidation_WithoutEchoingTheValue()
    {
        const string invalidKey = "this is not base64 !!";

        var exception = ResolveInvalid<JwtOptions>(AuthenticationTestHost.CreateSettings(invalidKey));

        Assert.Contains("SigningKey", exception.Message);
        Assert.DoesNotContain(invalidKey, exception.Message);
    }

    [Fact]
    public void SigningKeyShorterThan256Bits_FailsValidation_WithoutEchoingTheValue()
    {
        var shortKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));

        var exception = ResolveInvalid<JwtOptions>(AuthenticationTestHost.CreateSettings(shortKey));

        Assert.Contains("256 bits", exception.Message);
        Assert.DoesNotContain(shortKey, exception.Message);
    }

    [Theory]
    [InlineData("Authentication:Jwt:Issuer")]
    [InlineData("Authentication:Jwt:Audience")]
    public void MissingIssuerOrAudience_FailsValidation(string key)
    {
        var settings = ValidSettings();
        settings[key] = null;

        var exception = ResolveInvalid<JwtOptions>(settings);

        Assert.Contains(key, exception.Message);
    }

    [Fact]
    public void NonPositiveAccessTokenLifetime_FailsValidation()
    {
        var settings = ValidSettings();
        settings["Authentication:Jwt:AccessTokenLifetime"] = "00:00:00";

        Assert.Contains("AccessTokenLifetime", ResolveInvalid<JwtOptions>(settings).Message);
    }

    [Fact]
    public void NonPositiveRefreshTokenLifetime_FailsValidation()
    {
        var settings = ValidSettings();
        settings["Authentication:RefreshToken:Lifetime"] = "00:00:00";

        Assert.Contains("Lifetime", ResolveInvalid<RefreshTokenOptions>(settings).Message);
    }

    [Fact]
    public void PasswordAndLockoutPolicy_MatchApprovedDecisions()
    {
        using var provider = AuthenticationTestHost.BuildProvider(ValidSettings());
        var options = provider.GetRequiredService<IOptions<IdentityOptions>>().Value;

        Assert.Equal(12, options.Password.RequiredLength);
        Assert.True(options.Password.RequireUppercase);
        Assert.True(options.Password.RequireLowercase);
        Assert.True(options.Password.RequireDigit);
        Assert.True(options.Password.RequireNonAlphanumeric);
        Assert.Equal(4, options.Password.RequiredUniqueChars);

        Assert.Equal(5, options.Lockout.MaxFailedAccessAttempts);
        Assert.Equal(TimeSpan.FromMinutes(15), options.Lockout.DefaultLockoutTimeSpan);
        Assert.True(options.Lockout.AllowedForNewUsers);

        Assert.True(options.User.RequireUniqueEmail);
    }

    [Theory]
    [InlineData("Short-1a!")]
    [InlineData("no-uppercase-12!")]
    [InlineData("NO-LOWERCASE-12!")]
    [InlineData("No-Digits-Here-Ab!")]
    [InlineData("NoSpecialChars12345")]
    public async Task WeakPasswords_AreRejectedByIdentityPolicy(string password)
    {
        Assert.False(await ValidatePasswordAsync(password));
    }

    [Fact]
    public async Task PolicyCompliantPassword_IsAccepted()
    {
        Assert.True(await ValidatePasswordAsync(AuthenticationTestHost.ValidPassword));
    }

    private static async Task<bool> ValidatePasswordAsync(string password)
    {
        await using var provider = AuthenticationTestHost.BuildProvider(ValidSettings());
        await using var scope = provider.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        foreach (var validator in userManager.PasswordValidators)
        {
            if (!(await validator.ValidateAsync(userManager, new ApplicationUser(), password)).Succeeded)
            {
                return false;
            }
        }

        return true;
    }
}
