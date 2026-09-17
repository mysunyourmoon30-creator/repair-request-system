using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace RepairRequest.ApiTests;

/// <summary>
/// No secret is committed, and the host refuses to start without a valid signing key (decision M6).
/// </summary>
public class ConfigurationSecurityTests
{
    private static string RepositoryPath(params string[] relativeParts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RepairRequest.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine([directory.FullName, .. relativeParts]);
    }

    [Theory]
    [InlineData("appsettings.json")]
    [InlineData("appsettings.Development.json.example")]
    public void CommittedSettings_ContainNoSigningKeyOrCredentials(string fileName)
    {
        var path = RepositoryPath("backend", "src", "RepairRequest.Api", fileName);
        var text = File.ReadAllText(path);

        Assert.DoesNotContain("SigningKey", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Password=", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("User ID=", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CommittedAppSettings_DefineNonSecretAuthenticationSettings()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(RepositoryPath("backend", "src", "RepairRequest.Api", "appsettings.json")));
        var authentication = json.RootElement.GetProperty("Authentication");

        Assert.Equal("00:15:00", authentication.GetProperty("Jwt").GetProperty("AccessTokenLifetime").GetString());
        Assert.Equal("7.00:00:00", authentication.GetProperty("RefreshToken").GetProperty("Lifetime").GetString());
        Assert.False(string.IsNullOrWhiteSpace(authentication.GetProperty("Jwt").GetProperty("Issuer").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(authentication.GetProperty("Jwt").GetProperty("Audience").GetString()));
        Assert.Equal(string.Empty, json.RootElement.GetProperty("ConnectionStrings").GetProperty("DefaultConnection").GetString());
    }

    public static TheoryData<string?> InvalidSigningKeys => new()
    {
        null,
        "not base64 !!",
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))
    };

    [Theory]
    [MemberData(nameof(InvalidSigningKeys))]
    public void Startup_WithMissingOrWeakSigningKey_Fails_WithoutEchoingTheKey(string? signingKey)
    {
        using var factory = new SigningKeyOverrideFactory(signingKey);

        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        var validation = FindInner<OptionsValidationException>(exception);
        Assert.NotNull(validation);
        Assert.Contains("SigningKey", validation.Message);
        if (!string.IsNullOrEmpty(signingKey))
        {
            Assert.DoesNotContain(signingKey, validation.Message);
        }
    }

    private static TException? FindInner<TException>(Exception? exception)
        where TException : Exception
    {
        while (exception is not null)
        {
            if (exception is TException match)
            {
                return match;
            }

            if (exception is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    if (FindInner<TException>(inner) is { } nested)
                    {
                        return nested;
                    }
                }
            }

            exception = exception.InnerException;
        }

        return null;
    }

    private sealed class SigningKeyOverrideFactory(string? signingKey) : ApiHostFactory
    {
        protected override IDictionary<string, string?> TestSettings => new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = ConnectionString,
            ["Authentication:Jwt:SigningKey"] = signingKey
        };
    }
}
