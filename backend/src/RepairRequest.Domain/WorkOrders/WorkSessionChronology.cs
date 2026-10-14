namespace RepairRequest.Domain.WorkOrders;

/// <summary>One pause interval of a Work Session as seen by the chronology rules; <see cref="ResumedAt"/> is null while the pause is open.</summary>
public sealed record PausePeriod(Guid Id, DateTime PausedAt, DateTime? ResumedAt);

/// <summary>
/// The chronology of a Work Session (`docs/17` P2, approved 13 October 2026): <c>check_in ≤</c> every pause start; each
/// pause starts no later than it ends; consecutive pauses do not overlap (a pause must be closed before the next one
/// starts); every pause end and start is no later than <c>check_out</c> when the session is checked out, which also
/// means a checked-out session has no open pause. Equality is allowed (non-decreasing order). Pure and stateless: it
/// evaluates the intervals it is given, so request and approval re-evaluate the *current* facts each time.
/// </summary>
public static class WorkSessionChronology
{
    /// <summary>Null when the intervals are chronological, otherwise a short description of the first violation.</summary>
    public static string? Violation(DateTime checkInAt, DateTime? checkOutAt, IReadOnlyCollection<PausePeriod> pauses)
    {
        ArgumentNullException.ThrowIfNull(pauses);

        if (checkOutAt is { } checkOut && checkOut < checkInAt)
        {
            return "The check-out time would be before the check-in time.";
        }

        PausePeriod? previous = null;
        foreach (var pause in pauses.OrderBy(item => item.PausedAt).ThenBy(item => item.Id))
        {
            if (pause.PausedAt < checkInAt)
            {
                return "A pause would start before the check-in time.";
            }

            if (pause.ResumedAt is { } resumedAt && resumedAt < pause.PausedAt)
            {
                return "A pause would end before it starts.";
            }

            if (checkOutAt is { } checkOutTime)
            {
                if (pause.PausedAt > checkOutTime || (pause.ResumedAt is { } endedAt && endedAt > checkOutTime))
                {
                    return "A pause would extend beyond the check-out time.";
                }

                if (pause.ResumedAt is null)
                {
                    return "A checked-out Work Session cannot have an open pause.";
                }
            }

            if (previous is not null)
            {
                if (previous.ResumedAt is null)
                {
                    return "A pause cannot start while an earlier pause is still open.";
                }

                if (previous.ResumedAt > pause.PausedAt)
                {
                    return "Pauses would overlap.";
                }
            }

            previous = pause;
        }

        return null;
    }
}
