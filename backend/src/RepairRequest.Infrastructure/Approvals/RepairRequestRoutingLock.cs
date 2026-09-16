using System.Globalization;

namespace RepairRequest.Infrastructure.Approvals;

/// <summary>
/// SQL Server application lock that serializes ST-RR-003 routing per Repair Request (DEC-PRE-S1-007R-07). It is held by the
/// routing transaction (<c>sp_getapplock</c>, owner Transaction), so it works across API instances and is released by SQL
/// Server on commit, rollback or connection loss. The key is the request itself, so routing attempts for other requests
/// never wait on it, and the wait is bounded by <see cref="RepairRequestRoutingLockOptions"/>.
/// </summary>
public static class RepairRequestRoutingLock
{
    /// <summary>Lock resource of one Repair Request's routing attempt. 41 characters (limit 255).</summary>
    public static string Resource(Guid repairRequestId) =>
        string.Create(CultureInfo.InvariantCulture, $"RR-ROUTE|{repairRequestId:N}");
}

/// <summary>
/// Wait limit for the routing lock. An attempt that cannot get the lock in time writes nothing and returns the approved
/// concurrency conflict (409), so Admin Retry never surfaces lock contention as an uncontrolled failure.
/// </summary>
public sealed class RepairRequestRoutingLockOptions
{
    public const int DefaultTimeoutMilliseconds = 5_000;

    /// <summary>
    /// Upper bound of the wait. It stays below the default 30 s SqlClient command timeout, so the bounded lock wait (a
    /// controlled 409) always decides before a command timeout (an uncontrolled failure) could fire.
    /// </summary>
    public const int MaxTimeoutMilliseconds = 29_999;

    private readonly int _timeoutMilliseconds = DefaultTimeoutMilliseconds;

    /// <summary>Always bounded: 1..<see cref="MaxTimeoutMilliseconds"/>. Zero and negative values (-1 waits forever) are rejected.</summary>
    public int TimeoutMilliseconds
    {
        get => _timeoutMilliseconds;
        init => _timeoutMilliseconds = value is > 0 and <= MaxTimeoutMilliseconds
            ? value
            : throw new ArgumentOutOfRangeException(nameof(TimeoutMilliseconds), value, $"The routing lock wait must be between 1 and {MaxTimeoutMilliseconds} milliseconds.");
    }
}
