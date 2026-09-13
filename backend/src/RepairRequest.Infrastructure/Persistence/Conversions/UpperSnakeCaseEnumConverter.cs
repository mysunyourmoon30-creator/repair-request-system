using System.Text;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace RepairRequest.Infrastructure.Persistence.Conversions;

/// <summary>
/// Persists a domain enum as its baseline UPPER_SNAKE_CASE state code
/// (e.g. <c>UnderReview</c> -> <c>UNDER_REVIEW</c>), matching RR-DBD-001 section 1
/// "State: varchar code + CHECK". The same code list feeds the CHECK constraints,
/// so the database and the converter cannot drift apart.
/// </summary>
internal sealed class UpperSnakeCaseEnumConverter<TEnum> : ValueConverter<TEnum, string>
    where TEnum : struct, Enum
{
    private static readonly Dictionary<TEnum, string> CodeByValue =
        Enum.GetValues<TEnum>().ToDictionary(value => value, value => ToUpperSnakeCase(value.ToString()));

    private static readonly Dictionary<string, TEnum> ValueByCode =
        CodeByValue.ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal);

    public UpperSnakeCaseEnumConverter()
        : base(value => ToCode(value), code => FromCode(code))
    {
    }

    public static IReadOnlyCollection<string> Codes => CodeByValue.Values;

    public static string ToCode(TEnum value) => CodeByValue[value];

    public static TEnum FromCode(string code) =>
        ValueByCode.TryGetValue(code, out var value)
            ? value
            : throw new InvalidOperationException($"'{code}' is not a valid {typeof(TEnum).Name} code.");

    private static string ToUpperSnakeCase(string name)
    {
        var builder = new StringBuilder(name.Length + 4);

        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]))
            {
                builder.Append('_');
            }

            builder.Append(char.ToUpperInvariant(name[i]));
        }

        return builder.ToString();
    }
}
