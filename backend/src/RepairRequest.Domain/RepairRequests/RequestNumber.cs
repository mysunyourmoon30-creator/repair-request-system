using System.Globalization;

namespace RepairRequest.Domain.RepairRequests;

/// <summary>
/// Request No format <c>RR-{yyyy}-{000000}</c>: UTC year of Submit and a per-tenant, per-year sequence
/// (DEC-PRE-S1-007-11). The sequence itself is allocated transactionally by the persistence layer.
/// </summary>
public static class RequestNumber
{
    public const int MaxSequence = 999_999;

    public static string Format(int year, int sequence)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(year, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(year, 9999);
        ArgumentOutOfRangeException.ThrowIfLessThan(sequence, 1);

        // A seventh digit would break the approved format; fail closed instead of generating an off-format number.
        if (sequence > MaxSequence)
        {
            throw new InvalidOperationException($"The Request No sequence for {year} is exhausted.");
        }

        return string.Create(CultureInfo.InvariantCulture, $"RR-{year:D4}-{sequence:D6}");
    }
}
