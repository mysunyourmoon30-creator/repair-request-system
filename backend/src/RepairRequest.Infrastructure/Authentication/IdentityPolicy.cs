using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace RepairRequest.Infrastructure.Authentication;

/// <summary>
/// Approved S1-002 identity policy. Values are explicit; framework defaults are not relied upon.
/// </summary>
internal static class IdentityPolicy
{
    public static void Configure(IdentityOptions options)
    {
        // B1 - password policy.
        options.Password.RequiredLength = 12;
        options.Password.RequireUppercase = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireDigit = true;
        options.Password.RequireNonAlphanumeric = true;
        options.Password.RequiredUniqueChars = 4;

        // B2 - lockout policy.
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        options.Lockout.AllowedForNewUsers = true;

        // M3 - email is the login identifier and must identify exactly one account.
        options.User.RequireUniqueEmail = true;
    }
}

/// <summary>
/// Performs a password hash verification against a throwaway hash when no real account can be
/// checked (unknown email, locked account), so response timing does not reveal account state.
/// Uses the same hasher options as ASP.NET Core Identity.
/// </summary>
internal sealed class DummyPasswordHash
{
    private static readonly Identity.ApplicationUser PlaceholderUser = new();

    private readonly PasswordHasher<Identity.ApplicationUser> _hasher;
    private readonly string _hash;

    public DummyPasswordHash(IOptions<PasswordHasherOptions> hasherOptions)
    {
        _hasher = new PasswordHasher<Identity.ApplicationUser>(hasherOptions);
        _hash = _hasher.HashPassword(PlaceholderUser, Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
    }

    public void Verify(string password) => _ = _hasher.VerifyHashedPassword(PlaceholderUser, _hash, password);
}
