using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using RepairRequest.Domain.Approvals;
using RepairRequest.Domain.Auditing;
using RepairRequest.Domain.Files;
using RepairRequest.Domain.MasterData;
using RepairRequest.Domain.RepairRequests;
using RepairRequest.Domain.WorkOrders;
using RepairRequest.Infrastructure.Identity;
using RepairRequest.Infrastructure.Persistence.Conversions;
using RepairRequest.Infrastructure.RepairRequests;
using RepairRequest.Infrastructure.WorkOrders;
using RepairRequestAggregate = RepairRequest.Domain.RepairRequests.RepairRequest;

namespace RepairRequest.Infrastructure.Persistence;

/// <summary>
/// Unit of Work for the Repair Request platform (RR-ARCH-001 section 9).
/// Hosts the ASP.NET Core Identity store (DEC-PS1-004) together with the Sprint 1
/// domain foundation. Mapping details live in <c>Persistence/Configurations</c>.
/// No lazy loading and no navigation properties: reads use explicit projections
/// (RR-ARCH-001 section 15).
/// </summary>
public class RepairRequestDbContext : IdentityDbContext<ApplicationUser, ApplicationRole, Guid>
{
    public RepairRequestDbContext(DbContextOptions<RepairRequestDbContext> options)
        : base(options)
    {
    }

    public DbSet<Customer> Customers => Set<Customer>();

    public DbSet<Site> Sites => Set<Site>();

    public DbSet<Equipment> Equipment => Set<Equipment>();

    public DbSet<RepairRequestAggregate> RepairRequests => Set<RepairRequestAggregate>();

    public DbSet<RepairRequestAttachment> RepairRequestAttachments => Set<RepairRequestAttachment>();

    public DbSet<FileAsset> FileAssets => Set<FileAsset>();

    public DbSet<AuditHistory> AuditHistory => Set<AuditHistory>();

    public DbSet<UserSiteScope> UserSiteScopes => Set<UserSiteScope>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<RequestCategory> RequestCategories => Set<RequestCategory>();

    public DbSet<RequestPriority> RequestPriorities => Set<RequestPriority>();

    public DbSet<RequestNumberCounter> RequestNumberCounters => Set<RequestNumberCounter>();

    public DbSet<ApprovalRoute> ApprovalRoutes => Set<ApprovalRoute>();

    public DbSet<ApprovalRouteStep> ApprovalRouteSteps => Set<ApprovalRouteStep>();

    public DbSet<RepairRequestApproval> RepairRequestApprovals => Set<RepairRequestApproval>();

    public DbSet<WorkOrder> WorkOrders => Set<WorkOrder>();

    public DbSet<WorkOrderNumberCounter> WorkOrderNumberCounters => Set<WorkOrderNumberCounter>();

    public DbSet<ServiceVisit> ServiceVisits => Set<ServiceVisit>();

    public DbSet<WorkSession> WorkSessions => Set<WorkSession>();

    public DbSet<WorkSessionPause> WorkSessionPauses => Set<WorkSessionPause>();

    public DbSet<WorkSummary> WorkSummaries => Set<WorkSummary>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(RepairRequestDbContext).Assembly);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);

        // RR-DBD-001 section 1: UTC datetime2(3).
        configurationBuilder.Properties<DateTime>()
            .HaveConversion<UtcDateTimeConverter>()
            .HavePrecision(3);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnsureAuditHistoryIsAppendOnly();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        EnsureAuditHistoryIsAppendOnly();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>RR-DD-001 section 2 / RR-DBD-001 section 1: audit history is append-only.</summary>
    private void EnsureAuditHistoryIsAppendOnly()
    {
        foreach (var entry in ChangeTracker.Entries<AuditHistory>())
        {
            if (entry.State is EntityState.Modified or EntityState.Deleted)
            {
                throw new InvalidOperationException(
                    "Audit history is append-only; existing audit records cannot be modified or deleted.");
            }
        }
    }
}
