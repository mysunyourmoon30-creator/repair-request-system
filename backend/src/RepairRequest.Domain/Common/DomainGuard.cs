namespace RepairRequest.Domain.Common;

/// <summary>
/// Argument guards shared by domain constructors. Validation messages are
/// intentionally generic; user-facing validation envelopes are produced by the
/// Application/API layers (RR-API-001 section 2, 422 VALIDATION_FAILED).
/// </summary>
internal static class DomainGuard
{
    public static Guid NotEmpty(Guid value, string paramName) =>
        value == Guid.Empty
            ? throw new ArgumentException("Value must not be an empty identifier.", paramName)
            : value;

    public static string RequiredText(string? value, int maxLength, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value is required and must not be blank.", paramName);
        }

        return MaxLength(value, maxLength, paramName);
    }

    public static string? OptionalText(string? value, int maxLength, string paramName) =>
        value is null ? null : MaxLength(value, maxLength, paramName);

    /// <summary>RR-DD-001 section 2: timestamps are stored in UTC.</summary>
    public static DateTime Utc(DateTime value, string paramName) =>
        value.Kind == DateTimeKind.Utc
            ? value
            : throw new ArgumentException("Timestamp must be expressed in UTC.", paramName);

    private static string MaxLength(string value, int maxLength, string paramName) =>
        value.Length > maxLength
            ? throw new ArgumentException($"Value must not exceed {maxLength} characters.", paramName)
            : value;
}
