using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace RepairRequest.Infrastructure.Persistence.Conversions;

/// <summary>
/// All business timestamps are stored as UTC datetime2(3) (RR-DBD-001 section 1).
/// SQL Server does not persist DateTimeKind, so values read back are re-tagged as UTC.
/// Values are written unchanged: the domain guarantees UTC at construction.
/// </summary>
internal sealed class UtcDateTimeConverter : ValueConverter<DateTime, DateTime>
{
    public UtcDateTimeConverter()
        : base(value => value, value => DateTime.SpecifyKind(value, DateTimeKind.Utc))
    {
    }
}
