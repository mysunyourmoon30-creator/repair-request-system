using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using RepairRequest.Application.Common;

namespace RepairRequest.Api.Http;

/// <summary>Paged list response shape shared by list endpoints.</summary>
public sealed record PagedResponse<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

/// <summary>
/// Builds a bounded <see cref="PageRequest"/> from query values (RR-API-001 sections 7/9): page and pageSize must be 1 or
/// greater (otherwise 400 BAD_REQUEST); pageSize defaults to the configured default and is clamped to the configured maximum.
/// </summary>
public static class PageRequests
{
    public static bool TryCreate(
        int? page,
        int? pageSize,
        PagingOptions options,
        HttpContext httpContext,
        [NotNullWhen(true)] out PageRequest? request,
        [NotNullWhen(false)] out ObjectResult? problem)
    {
        request = null;
        problem = null;

        var requestedPage = page ?? 1;
        var requestedPageSize = pageSize ?? options.DefaultPageSize;

        if (requestedPage < 1)
        {
            problem = ApiProblemResults.BadRequest(httpContext, "page must be 1 or greater.");
            return false;
        }

        if (requestedPageSize < 1)
        {
            problem = ApiProblemResults.BadRequest(httpContext, "pageSize must be 1 or greater.");
            return false;
        }

        request = new PageRequest(requestedPage, Math.Min(requestedPageSize, options.MaxPageSize));
        return true;
    }
}

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
