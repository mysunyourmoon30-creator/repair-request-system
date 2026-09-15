using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using RepairRequest.Infrastructure.Persistence;

namespace RepairRequest.Infrastructure.RepairRequests;

/// <summary>
/// Idempotent seeding of the tenant-scoped Category and Priority lookups (DEC-PRE-S1-007-01/02/03). The values are portfolio
/// placeholder demo/reference data only; they can be replaced as data without a code change and no application rule depends
/// on them. One set-based INSERT ... WHERE NOT EXISTS per table, with key-range locks so concurrent seeders cannot insert the
/// same row twice. A tenant exists only as a tenant_id on users, so "all tenants" means every tenant with at least one user.
/// </summary>
public sealed class RequestLookupSeeder
{
    public static IReadOnlyList<(string Code, string Name)> CategorySeed { get; } =
    [
        ("ELECTRICAL", "Electrical"),
        ("MECHANICAL", "Mechanical"),
        ("PLUMBING", "Plumbing"),
        ("HVAC", "HVAC"),
        ("IT", "IT"),
        ("OTHER", "Other")
    ];

    public static IReadOnlyList<(string Code, string Name)> PrioritySeed { get; } =
    [
        ("LOW", "Low"),
        ("MEDIUM", "Medium"),
        ("HIGH", "High"),
        ("URGENT", "Urgent")
    ];

    private readonly RepairRequestDbContext _db;

    public RequestLookupSeeder(RepairRequestDbContext db)
    {
        _db = db;
    }

    /// <summary>Seeds every tenant that has users and is missing rows. Returns the number of inserted rows.</summary>
    public Task<int> SeedAllTenantsAsync(CancellationToken cancellationToken) =>
        SeedAsync("SELECT DISTINCT [TenantId] FROM [AspNetUsers]", null, cancellationToken);

    /// <summary>Seeds one tenant (for example when a tenant is provisioned). Returns the number of inserted rows.</summary>
    public Task<int> SeedTenantAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("Tenant is required.", nameof(tenantId));
        }

        return SeedAsync("SELECT @tenant_id AS [TenantId]", tenantId, cancellationToken);
    }

    private async Task<int> SeedAsync(string tenantSource, Guid? tenantId, CancellationToken cancellationToken)
    {
        var sql = Insert("request_category", "request_category_code", tenantSource, CategorySeed)
                  + Insert("request_priority", "priority_code", tenantSource, PrioritySeed);

        // Seed values are compile-time constants (no client input); the tenant is always a parameter.
        var parameters = tenantId is { } id ? new object[] { new SqlParameter("@tenant_id", id) } : [];
        return await _db.Database.ExecuteSqlRawAsync(sql, parameters, cancellationToken);
    }

    private static string Insert(string table, string codeColumn, string tenantSource, IReadOnlyList<(string Code, string Name)> seed)
    {
        var values = new StringBuilder();
        foreach (var (code, name) in seed)
        {
            if (values.Length > 0)
            {
                values.Append(", ");
            }

            values.Append("('").Append(code.Replace("'", "''")).Append("', N'").Append(name.Replace("'", "''")).Append("')");
        }

        return $"""
            INSERT INTO [{table}] ([tenant_id], [{codeColumn}], [name], [status])
            SELECT [tenants].[TenantId], [seed].[code], [seed].[name], 'ACTIVE'
            FROM ({tenantSource}) AS [tenants]
            CROSS JOIN (VALUES {values}) AS [seed] ([code], [name])
            WHERE NOT EXISTS (
                SELECT 1 FROM [{table}] AS [existing] WITH (UPDLOCK, HOLDLOCK)
                WHERE [existing].[tenant_id] = [tenants].[TenantId] AND [existing].[{codeColumn}] = [seed].[code]);

            """;
    }
}
