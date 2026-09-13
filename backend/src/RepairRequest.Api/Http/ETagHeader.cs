namespace RepairRequest.Api.Http;

/// <summary>
/// Maps the SQL Server rowversion to a strong ETag and reads the If-Match precondition
/// (RR-API-001 section 2; RR-ARCH-001 section 7). Only one strong tag carrying a full rowversion is accepted;
/// "*", weak tags and lists are rejected because they would bypass the optimistic concurrency check.
/// </summary>
internal static class ETagHeader
{
    private const int RowVersionLength = 8;

    public static string Format(byte[] rowVersion) => $"\"{Convert.ToBase64String(rowVersion)}\"";

    public static bool TryReadIfMatch(HttpRequest request, out byte[] rowVersion)
    {
        rowVersion = [];

        var values = request.Headers.IfMatch;
        if (values.Count != 1)
        {
            return false;
        }

        var value = values[0]?.Trim();
        if (value is null || value.Length < 3 || value[0] != '"' || value[^1] != '"')
        {
            return false;
        }

        var buffer = new byte[RowVersionLength];
        if (!Convert.TryFromBase64String(value[1..^1], buffer, out var written) || written != RowVersionLength)
        {
            return false;
        }

        rowVersion = buffer;
        return true;
    }
}
