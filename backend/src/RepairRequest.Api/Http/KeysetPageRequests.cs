using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Mvc;

namespace RepairRequest.Api.Http;

/// <summary>
/// Bounds the page size of a keyset (cursor) list (RR-API-001 sections 7/9; `docs/15` D6): <c>pageSize</c> defaults to the
/// configured default, a value below 1 is 400 BAD_REQUEST and a value above the configured maximum is clamped to it — the
/// same convention as <see cref="PageRequests"/>, without a <c>page</c> number.
/// </summary>
public static class KeysetPageRequests
{
    public static bool TryCreate(
        int? pageSize,
        PagingOptions options,
        HttpContext httpContext,
        out int size,
        [NotNullWhen(false)] out ObjectResult? problem)
    {
        size = 0;
        problem = null;

        var requested = pageSize ?? options.DefaultPageSize;
        if (requested < 1)
        {
            problem = ApiProblemResults.BadRequest(httpContext, "pageSize must be 1 or greater.");
            return false;
        }

        size = Math.Min(requested, options.MaxPageSize);
        return true;
    }
}
