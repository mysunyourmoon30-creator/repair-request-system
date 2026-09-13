using RepairRequest.Infrastructure.Persistence.Conversions;

namespace RepairRequest.Infrastructure.Persistence.Configurations;

/// <summary>Builds CHECK constraint SQL from the same code lists used by the value converters.</summary>
internal static class SqlCheck
{
    public static string In<TEnum>(string column)
        where TEnum : struct, Enum =>
        $"[{column}] IN ({string.Join(", ", UpperSnakeCaseEnumConverter<TEnum>.Codes.Select(code => $"'{code}'"))})";

    public static string Code<TEnum>(TEnum value)
        where TEnum : struct, Enum =>
        UpperSnakeCaseEnumConverter<TEnum>.ToCode(value);

    public static string NullOrJson(string column) =>
        $"[{column}] IS NULL OR ISJSON([{column}]) = 1";
}
