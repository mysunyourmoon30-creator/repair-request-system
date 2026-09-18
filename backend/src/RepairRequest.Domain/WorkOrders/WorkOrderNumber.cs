using System.Globalization;

namespace RepairRequest.Domain.WorkOrders;

/// <summary>
/// Work Order No. format <c>WO-{yyyy}-{000000}</c>: UTC year of Convert and a per-tenant, per-year sequence.
/// WO-003 itself only says "Unique generated" with no format specified; this mirrors the approved Request No.
/// format (<see cref="RepairRequests.RequestNumber"/>, `RR-{yyyy}-{000000}`) rather than inventing a new shape.
/// The sequence itself is allocated transactionally by the persistence layer.
/// </summary>
public static class WorkOrderNumber
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
            throw new InvalidOperationException($"The Work Order No. sequence for {year} is exhausted.");
        }

        return string.Create(CultureInfo.InvariantCulture, $"WO-{year:D4}-{sequence:D6}");
    }
}
