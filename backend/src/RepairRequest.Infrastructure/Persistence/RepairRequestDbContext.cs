using Microsoft.EntityFrameworkCore;

namespace RepairRequest.Infrastructure.Persistence;

/// <summary>
/// Unit of Work for the Repair Request platform (RR-ARCH-001 section 9).
/// Sprint 0: no baseline entities are mapped yet. This context exists to prove
/// EF Core / SQL Server connectivity and migration flow; entity sets are added
/// only once the corresponding data model is approved from RR-DBD-001 / RR-DD-001.
/// </summary>
public class RepairRequestDbContext : DbContext
{
    public RepairRequestDbContext(DbContextOptions<RepairRequestDbContext> options)
        : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
    }
}
