namespace RepairRequest.Api.Http;

/// <summary>
/// Resolves the request correlation id used for audit/log linkage (RR-API-001 section 2:
/// "X-Correlation-ID - caller UUID or server-generated"). A caller value is accepted only
/// when it is a non-empty UUID; the resolved id is echoed on the response.
/// </summary>
internal static class CorrelationIdResolver
{
    public const string HeaderName = "X-Correlation-ID";

    private static readonly object ItemKey = new();

    public static Guid Resolve(HttpContext httpContext)
    {
        if (httpContext.Items.TryGetValue(ItemKey, out var existing) && existing is Guid resolved)
        {
            return resolved;
        }

        var correlationId =
            httpContext.Request.Headers.TryGetValue(HeaderName, out var values)
            && Guid.TryParse(values.ToString(), out var supplied)
            && supplied != Guid.Empty
                ? supplied
                : Guid.NewGuid();

        httpContext.Items[ItemKey] = correlationId;
        httpContext.Response.Headers[HeaderName] = correlationId.ToString();
        return correlationId;
    }
}
