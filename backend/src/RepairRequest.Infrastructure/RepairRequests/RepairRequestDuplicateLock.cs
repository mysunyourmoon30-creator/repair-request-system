using System.Globalization;
using RepairRequest.Application.RepairRequests;

namespace RepairRequest.Infrastructure.RepairRequests;

/// <summary>
/// SQL Server application lock that serializes Submit per BR-14 duplicate key (DEC-PRE-S1-007-09). It is held by the
/// Submit transaction (<c>sp_getapplock</c>, owner Transaction), so it works across API instances and is released by
/// SQL Server on commit, rollback or connection loss. Only Submits with the same tenant, Site, Category and Equipment (or
/// likewise no Equipment) wait on each other.
/// </summary>
public static class RepairRequestDuplicateLock
{
    /// <summary>
    /// Lock resource of the duplicate key: the canonical stored Category code and "-" for no Equipment, so an empty
    /// Equipment never shares a key with a selected one. Location is not in Sprint 1 (DEC-PRE-S1-007-05); when it is
    /// introduced into the duplicate match it must be added to this key. At most 136 characters (limit 255).
    /// </summary>
    public static string Resource(DuplicateQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var equipment = query.EquipmentId is { } equipmentId ? equipmentId.ToString("N", CultureInfo.InvariantCulture) : "-";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"RR-DUP|{query.TenantId:N}|{query.SiteId:N}|{query.RequestCategoryCode}|{equipment}");
    }
}

/// <summary>Wait limit for the duplicate-key lock. A Submit that cannot get the lock in time returns 409 and writes nothing.</summary>
public sealed class RepairRequestDuplicateLockOptions
{
    public const int DefaultTimeoutMilliseconds = 10_000;

    public int TimeoutMilliseconds { get; init; } = DefaultTimeoutMilliseconds;
}
