using Microsoft.Extensions.Options;

namespace RepairRequest.Api.Http;

/// <summary>
/// List paging limits. RR-API-001 sections 7/9: page/pageSize are technical conventions, the exact maximum page
/// size is deployment configuration, and the server clamps requests above it. Validated on start.
/// </summary>
public sealed class PagingOptions
{
    public const string SectionName = "Paging";

    public int DefaultPageSize { get; set; }

    public int MaxPageSize { get; set; }
}

internal sealed class PagingOptionsValidator : IValidateOptions<PagingOptions>
{
    public ValidateOptionsResult Validate(string? name, PagingOptions options)
    {
        if (options.DefaultPageSize < 1)
        {
            return ValidateOptionsResult.Fail($"{PagingOptions.SectionName}:DefaultPageSize must be 1 or greater.");
        }

        return options.MaxPageSize < options.DefaultPageSize
            ? ValidateOptionsResult.Fail($"{PagingOptions.SectionName}:MaxPageSize must be greater than or equal to DefaultPageSize.")
            : ValidateOptionsResult.Success;
    }
}

public static class PagingServiceCollectionExtensions
{
    public static IServiceCollection AddApiPaging(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<PagingOptions>()
            .Bind(configuration.GetSection(PagingOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<PagingOptions>, PagingOptionsValidator>();

        return services;
    }
}
