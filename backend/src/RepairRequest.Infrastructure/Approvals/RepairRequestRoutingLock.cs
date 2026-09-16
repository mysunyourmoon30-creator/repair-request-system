using System.Globalization;
using Microsoft.EntityFrameworkCore;
using RepairRequest.Infrastructure.Persistence;

namespace RepairRequest.Infrastructure.Approvals;

/// <summary>
/// SQL Server application lock that serializes the review workflow of one Repair Request: ST-RR-003 routing
/// (DEC-PRE-S1-007R-07) and the ST-RR-004/005 Approve/Reject decisions (S1-008) use the same key, so a decision never races
/// a routing attempt or another decision. It is held by the command transaction (<c>sp_getapplock</c>, owner Transaction),
/// so it works across API instances and is released by SQL Server on commit, rollback or connection loss. The key is the
/// request itself, so commands for other requests never wait on it, and the wait is bounded by
/// <see cref="RepairRequestRoutingLockOptions"/>.
/// </summary>
public static class RepairRequestRoutingLock
{
    /// <summary>Lock resource of one Repair Request's review workflow. 41 characters (limit 255).</summary>
    public static string Resource(Guid repairRequestId) =>
        string.Create(CultureInfo.InvariantCulture, $"RR-ROUTE|{repairRequestId:N}");

    /// <summary>
    /// Takes the exclusive, transaction-owned lock inside the caller's open transaction. False means the configured wait
    /// elapsed or the session was chosen as a deadlock victim; the caller then ends the command as a concurrency conflict
    /// without writing anything and without retrying.
    /// </summary>
    internal static async Task<bool> TryAcquireAsync(
        RepairRequestDbContext db,
        Guid repairRequestId,
        RepairRequestRoutingLockOptions options,
        CancellationToken cancellationToken)
    {
        var resource = Resource(repairRequestId);
        var timeout = options.TimeoutMilliseconds;

        var results = await db.Database.SqlQuery<int>($"""
            DECLARE @lock_result int;
            EXEC @lock_result = sp_getapplock @Resource = {resource}, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = {timeout};
            SELECT @lock_result AS [Value];
            """).ToListAsync(cancellationToken);

        return results.Single() >= 0;
    }
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
