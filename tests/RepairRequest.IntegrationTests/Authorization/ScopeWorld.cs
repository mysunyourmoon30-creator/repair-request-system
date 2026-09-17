using Microsoft.EntityFrameworkCore;
using RepairRequest.Domain.MasterData;
using RepairRequest.Domain.WorkOrders;
using RepairRequest.Infrastructure.Identity;
using RepairRequest.Infrastructure.Persistence;
using RepairRequestAggregate = RepairRequest.Domain.RepairRequests.RepairRequest;

namespace RepairRequest.IntegrationTests.Authorization;

/// <summary>
/// Two tenants of master data for scope tests. Tenant T has Customer A (Sites A1, A2) and Customer B
/// (Site B1), each Site with one Equipment; the other tenant has one Customer, Site and Equipment.
/// </summary>
internal sealed class ScopeWorld
{
    public Guid TenantId { get; } = Guid.NewGuid();

    public Guid OtherTenantId { get; } = Guid.NewGuid();

    public Customer CustomerA { get; private set; } = null!;

    public Customer CustomerB { get; private set; } = null!;

    public Site SiteA1 { get; private set; } = null!;

    public Site SiteA2 { get; private set; } = null!;

    public Site SiteB1 { get; private set; } = null!;

    public Site OtherTenantSite { get; private set; } = null!;

    public Equipment EquipmentA1 { get; private set; } = null!;

    public Equipment EquipmentB1 { get; private set; } = null!;

    public Equipment OtherTenantEquipment { get; private set; } = null!;

    public static async Task<ScopeWorld> CreateAsync(RepairRequestDbContext db)
    {
        var world = new ScopeWorld();

        world.CustomerA = new Customer(world.TenantId, "CUST-A");
        world.CustomerB = new Customer(world.TenantId, "CUST-B");
        var otherCustomer = new Customer(world.OtherTenantId, "CUST-A");
        db.Customers.AddRange(world.CustomerA, world.CustomerB, otherCustomer);

        world.SiteA1 = new Site(world.TenantId, world.CustomerA.Id, "SITE-A1");
        world.SiteA2 = new Site(world.TenantId, world.CustomerA.Id, "SITE-A2");
        world.SiteB1 = new Site(world.TenantId, world.CustomerB.Id, "SITE-B1");
        world.OtherTenantSite = new Site(world.OtherTenantId, otherCustomer.Id, "SITE-A1");
        db.Sites.AddRange(world.SiteA1, world.SiteA2, world.SiteB1, world.OtherTenantSite);

        world.EquipmentA1 = new Equipment(world.TenantId, world.SiteA1.Id, "EQ-1");
        world.EquipmentB1 = new Equipment(world.TenantId, world.SiteB1.Id, "EQ-1");
        world.OtherTenantEquipment = new Equipment(world.OtherTenantId, world.OtherTenantSite.Id, "EQ-1");
        db.Equipment.AddRange(world.EquipmentA1, world.EquipmentB1, world.OtherTenantEquipment);

        await db.SaveChangesAsync();
        return world;
    }

    public static async Task AssignSitesAsync(RepairRequestDbContext db, Guid tenantId, Guid userId, params Site[] sites)
    {
        foreach (var site in sites)
        {
            db.UserSiteScopes.Add(new UserSiteScope(tenantId, userId, site.Id));
        }

        await db.SaveChangesAsync();
    }

    public static async Task<Guid> AddRequestAsync(RepairRequestDbContext db, Guid tenantId, Guid createdBy, Site? site)
    {
        var request = RepairRequestAggregate.CreateDraft(tenantId, createdBy);
        var entry = db.RepairRequests.Add(request);
        entry.Property(r => r.SiteId).CurrentValue = site?.Id;
        await db.SaveChangesAsync();
        return request.Id;
    }

    /// <summary>S2-001: seeds a Work Order directly (Convert is not implemented), linked to an existing Repair Request.</summary>
    public static async Task<Guid> AddWorkOrderAsync(
        RepairRequestDbContext db, Guid tenantId, Guid repairRequestId, string workOrderNo, DateTime createdAt)
    {
        var workOrder = WorkOrder.Create(tenantId, repairRequestId, workOrderNo, createdAt);
        db.WorkOrders.Add(workOrder);
        await db.SaveChangesAsync();
        return workOrder.Id;
    }

    public static Task DeactivateSiteAsync(RepairRequestDbContext db, Guid siteId) =>
        db.Sites.Where(site => site.Id == siteId).ExecuteUpdateAsync(setters => setters
            .SetProperty(site => site.Status, MasterDataStatus.Inactive)
            .SetProperty(site => site.DeactivateReason, "Closed"));
}
