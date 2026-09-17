using Microsoft.Extensions.Options;

namespace RepairRequest.Infrastructure.Authentication;

/// <summary>
/// Fails startup when JWT configuration is missing or unsafe. Messages name the
/// offending key but never echo configured values.
/// </summary>
internal sealed class JwtOptionsValidator : IValidateOptions<JwtOptions>
{
    public const int MinimumSigningKeyBytes = 32;

    public ValidateOptionsResult Validate(string? name, JwtOptions options)
    {
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.Issuer))
        {
            failures.Add($"{JwtOptions.SectionName}:Issuer is required.");
        }

        if (string.IsNullOrWhiteSpace(options.Audience))
        {
            failures.Add($"{JwtOptions.SectionName}:Audience is required.");
        }

        if (!JwtTokenValidation.TryDecodeSigningKey(options.SigningKey, out var keyBytes))
        {
            failures.Add(
                $"{JwtOptions.SectionName}:SigningKey is missing or is not valid Base64. " +
                "Configure it through user-secrets (development) or a secret store (deployment).");
        }
        else if (keyBytes.Length < MinimumSigningKeyBytes)
        {
            failures.Add($"{JwtOptions.SectionName}:SigningKey must decode to at least {MinimumSigningKeyBytes} bytes (256 bits).");
        }

        if (options.AccessTokenLifetime <= TimeSpan.Zero)
        {
            failures.Add($"{JwtOptions.SectionName}:AccessTokenLifetime must be a positive duration.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

internal sealed class RefreshTokenOptionsValidator : IValidateOptions<RefreshTokenOptions>
{
    public ValidateOptionsResult Validate(string? name, RefreshTokenOptions options) =>
        options.Lifetime > TimeSpan.Zero
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail($"{RefreshTokenOptions.SectionName}:Lifetime must be a positive duration.");
}
