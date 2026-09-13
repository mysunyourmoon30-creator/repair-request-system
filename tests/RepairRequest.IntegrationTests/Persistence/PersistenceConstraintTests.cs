using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using RepairRequest.Domain.Auditing;
using RepairRequest.Domain.Files;
using RepairRequest.Domain.MasterData;
using RepairRequest.Domain.RepairRequests;
using RepairRequest.Infrastructure.Identity;
using RepairRequest.Infrastructure.Persistence;
using RepairRequestAggregate = RepairRequest.Domain.RepairRequests.RepairRequest;

namespace RepairRequest.IntegrationTests.Persistence;

/// <summary>
/// Verifies relationship, scope, uniqueness, state-code and concurrency constraints
/// against the migrated LocalDB schema. Every test uses fresh tenants so tests are independent.
/// </summary>
[Collection(PersistenceDatabaseCollection.Name)]
public class PersistenceConstraintTests
{
    private const int UniqueIndexViolation = 2601;
    private const int ConstraintViolation = 547;
    private const string ValidHash = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";

    private readonly PersistenceDatabaseFixture _database;

    public PersistenceConstraintTests(PersistenceDatabaseFixture database)
    {
        _database = database;
    }

    private sealed record Hierarchy(Guid TenantId, ApplicationUser User, Customer Customer, Site Site, Equipment Equipment);

    private static string NewCode(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..MasterDataEntity.CodeMaxLength];

    private static ApplicationUser NewUser(Guid tenantId)
    {
        var userName = $"user-{Guid.NewGuid():N}";
        return new ApplicationUser
        {
            TenantId = tenantId,
            UserName = userName,
            NormalizedUserName = userName.ToUpperInvariant(),
            SecurityStamp = Guid.NewGuid().ToString()
        };
    }

    private async Task<Hierarchy> SeedHierarchyAsync(Guid? tenantId = null)
    {
        var tenant = tenantId ?? Guid.NewGuid();
        await using var context = _database.CreateContext();

        var user = NewUser(tenant);
        var customer = new Customer(tenant, NewCode("C"));
        context.Users.Add(user);
        context.Customers.Add(customer);

        var site = new Site(tenant, customer.Id, NewCode("S"));
        context.Sites.Add(site);

        var equipment = new Equipment(tenant, site.Id, NewCode("E"));
        context.Equipment.Add(equipment);

        await context.SaveChangesAsync();
        return new Hierarchy(tenant, user, customer, site, equipment);
    }

    private static RepairRequestAggregate AddDraft(
        RepairRequestDbContext context,
        Guid tenantId,
        Guid createdBy,
        Action<EntityEntry<RepairRequestAggregate>>? configure = null)
    {
        var request = RepairRequestAggregate.CreateDraft(tenantId, createdBy);
        var entry = context.RepairRequests.Add(request);
        configure?.Invoke(entry);
        return request;
    }

    private static async Task AssertSaveRejectedAsync(RepairRequestDbContext context, int sqlErrorNumber, string constraintName)
    {
        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        var sqlException = Assert.IsType<SqlException>(exception.InnerException);
        Assert.Equal(sqlErrorNumber, sqlException.Number);
        Assert.Contains(constraintName, sqlException.Message);
    }

    private static async Task AssertSqlRejectedAsync(Func<Task> action, string constraintName)
    {
        var sqlException = await Assert.ThrowsAsync<SqlException>(action);
        Assert.Equal(ConstraintViolation, sqlException.Number);
        Assert.Contains(constraintName, sqlException.Message);
    }

    [Fact]
    public async Task Migrations_AreAllApplied()
    {
        await using var context = _database.CreateContext();

        var applied = await context.Database.GetAppliedMigrationsAsync();

        Assert.Contains(applied, id => id.EndsWith("_InitialCreate"));
        Assert.Contains(applied, id => id.EndsWith("_AddDomainAndIdentityFoundation"));
        Assert.Contains(applied, id => id.EndsWith("_AddRefreshTokenAndRoleSeed"));
        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
    }

    // ---------------- DEC-PS1-015: scoped code uniqueness ----------------

    [Fact]
    public async Task CustomerCode_DuplicateWithinTenant_IsRejected()
    {
        var seed = await SeedHierarchyAsync();
        await using var context = _database.CreateContext();

        context.Customers.Add(new Customer(seed.TenantId, seed.Customer.CustomerCode));

        await AssertSaveRejectedAsync(context, UniqueIndexViolation, "UQ_customer_tenant_id_customer_code");
    }

    [Fact]
    public async Task CustomerCode_SameCodeInAnotherTenant_IsAllowed()
    {
        var seed = await SeedHierarchyAsync();
        await using var context = _database.CreateContext();

        context.Customers.Add(new Customer(Guid.NewGuid(), seed.Customer.CustomerCode));

        Assert.Equal(1, await context.SaveChangesAsync());
    }

    [Fact]
    public async Task SiteCode_DuplicateWithinCustomer_IsRejected()
    {
        var seed = await SeedHierarchyAsync();
        await using var context = _database.CreateContext();

        context.Sites.Add(new Site(seed.TenantId, seed.Customer.Id, seed.Site.SiteCode));

        await AssertSaveRejectedAsync(context, UniqueIndexViolation, "UQ_site_customer_id_site_code");
    }

    [Fact]
    public async Task SiteCode_SameCodeUnderAnotherCustomer_IsAllowed()
    {
        var seed = await SeedHierarchyAsync();
        await using var context = _database.CreateContext();

        var otherCustomer = new Customer(seed.TenantId, NewCode("C"));
        context.Customers.Add(otherCustomer);
        context.Sites.Add(new Site(seed.TenantId, otherCustomer.Id, seed.Site.SiteCode));

        Assert.Equal(2, await context.SaveChangesAsync());
    }

    [Fact]
    public async Task EquipmentCode_DuplicateWithinSite_IsRejected()
    {
        var seed = await SeedHierarchyAsync();
        await using var context = _database.CreateContext();

        context.Equipment.Add(new Equipment(seed.TenantId, seed.Site.Id, seed.Equipment.EquipmentCode));

        await AssertSaveRejectedAsync(context, UniqueIndexViolation, "UQ_equipment_site_id_equipment_code");
    }

    [Fact]
    public async Task EquipmentCode_SameCodeUnderAnotherSite_IsAllowed()
    {
        var seed = await SeedHierarchyAsync();
        await using var context = _database.CreateContext();

        var otherSite = new Site(seed.TenantId, seed.Customer.Id, NewCode("S"));
        context.Sites.Add(otherSite);
        context.Equipment.Add(new Equipment(seed.TenantId, otherSite.Id, seed.Equipment.EquipmentCode));

        Assert.Equal(2, await context.SaveChangesAsync());
    }

    // ---------------- DEC-PS1-013 hierarchy + D-11 tenant isolation ----------------

    [Fact]
    public async Task Site_UnderCustomerOfAnotherTenant_IsRejected()
    {
        var seed = await SeedHierarchyAsync();
        await using var context = _database.CreateContext();

        context.Sites.Add(new Site(Guid.NewGuid(), seed.Customer.Id, NewCode("S")));

        await AssertSaveRejectedAsync(context, ConstraintViolation, "FK_site_customer");
    }

    [Fact]
    public async Task Equipment_UnderSiteOfAnotherTenant_IsRejected()
    {
        var seed = await SeedHierarchyAsync();
        await using var context = _database.CreateContext();

        context.Equipment.Add(new Equipment(Guid.NewGuid(), seed.Site.Id, NewCode("E")));

        await AssertSaveRejectedAsync(context, ConstraintViolation, "FK_equipment_site");
    }

    [Fact]
    public async Task Customer_WithSites_CannotBeDeleted_NoCascade()
    {
        var seed = await SeedHierarchyAsync();
        await using var context = _database.CreateContext();

        context.Customers.Remove(await context.Customers.SingleAsync(customer => customer.Id == seed.Customer.Id));

        await AssertSaveRejectedAsync(context, ConstraintViolation, "FK_site_customer");
    }

    [Fact]
    public async Task DeactivatingSite_DoesNotDeactivateItsEquipment()
    {
        var seed = await SeedHierarchyAsync();

        await using (var context = _database.CreateContext())
        {
            var site = await context.Sites.SingleAsync(s => s.Id == seed.Site.Id);
            site.Deactivate("Site closed");
            await context.SaveChangesAsync();
        }

        await using var verify = _database.CreateContext();
        var equipmentStatus = await verify.Equipment.AsNoTracking()
            .Where(equipment => equipment.Id == seed.Equipment.Id)
            .Select(equipment => equipment.Status)
            .SingleAsync();

        Assert.Equal(MasterDataStatus.Active, equipmentStatus);
    }

    // ---------------- DEC-PS1-014: deactivate reason ----------------

    [Fact]
    public async Task Deactivate_WithReason_PersistsInactiveCodeAndReason()
    {
        var seed = await SeedHierarchyAsync();

        await using (var context = _database.CreateContext())
        {
            var customer = await context.Customers.SingleAsync(c => c.Id == seed.Customer.Id);
            customer.Deactivate("Contract ended");
            await context.SaveChangesAsync();
        }

        await using var verify = _database.CreateContext();
        var storedStatus = await verify.Database
            .SqlQuery<string>($"SELECT [status] AS [Value] FROM [customer] WHERE [customer_id] = {seed.Customer.Id}")
            .SingleAsync();
        var customerReloaded = await verify.Customers.AsNoTracking().SingleAsync(c => c.Id == seed.Customer.Id);

        Assert.Equal("INACTIVE", storedStatus);
        Assert.Equal("Contract ended", customerReloaded.DeactivateReason);
    }

    [Theory]
    [InlineData("customer", null)]
    [InlineData("site", "   ")]
    [InlineData("equipment", null)]
    public async Task InactiveMasterWithoutReason_IsRejectedByDatabase(string table, string? reason)
    {
        var seed = await SeedHierarchyAsync();
        await using var context = _database.CreateContext();

        // Fixed statements per table: identifiers cannot be parameterized, values are.
        Func<Task> deactivateWithoutReason = table switch
        {
            "customer" => () => context.Database.ExecuteSqlAsync(
                $"UPDATE [customer] SET [status] = 'INACTIVE', [deactivate_reason] = {reason} WHERE [customer_id] = {seed.Customer.Id}"),
            "site" => () => context.Database.ExecuteSqlAsync(
                $"UPDATE [site] SET [status] = 'INACTIVE', [deactivate_reason] = {reason} WHERE [site_id] = {seed.Site.Id}"),
            _ => () => context.Database.ExecuteSqlAsync(
                $"UPDATE [equipment] SET [status] = 'INACTIVE', [deactivate_reason] = {reason} WHERE [equipment_id] = {seed.Equipment.Id}")
        };

        await AssertSqlRejectedAsync(deactivateWithoutReason, $"CK_{table}_deactivate_reason");
    }

    // ---------------- Repair Request relationships and state codes ----------------

    [Fact]
    public async Task RepairRequestDraft_RoundTripsWithCanonicalStatusAndUtcTimes()
    {
        var seed = await SeedHierarchyAsync();
        var preferredStart = new DateTime(2026, 9, 14, 1, 2, 3, 456, DateTimeKind.Utc);
        Guid requestId;

        await using (var context = _database.CreateContext())
        {
            var request = AddDraft(context, seed.TenantId, seed.User.Id, entry =>
            {
                entry.Property(r => r.SiteId).CurrentValue = seed.Site.Id;
                entry.Property(r => r.EquipmentId).CurrentValue = seed.Equipment.Id;
                entry.Property(r => r.PreferredStartAt).CurrentValue = preferredStart;
                entry.Property(r => r.PreferredEndAt).CurrentValue = preferredStart.AddHours(2);
            });
            await context.SaveChangesAsync();
            requestId = request.Id;
        }

        await using var verify = _database.CreateContext();
        var storedStatus = await verify.Database
            .SqlQuery<string>($"SELECT [status] AS [Value] FROM [repair_request] WHERE [repair_request_id] = {requestId}")
            .SingleAsync();
        var reloaded = await verify.RepairRequests.AsNoTracking().SingleAsync(r => r.Id == requestId);

        Assert.Equal("DRAFT", storedStatus);
        Assert.Equal(RepairRequestStatus.Draft, reloaded.Status);
        Assert.NotEmpty(reloaded.RowVersion);
        var storedPreferredStart = Assert.NotNull(reloaded.PreferredStartAt);
        Assert.Equal(DateTimeKind.Utc, storedPreferredStart.Kind);
        Assert.Equal(preferredStart, storedPreferredStart);
    }

    [Fact]
    public async Task RepairRequest_ReturnedStatusCode_IsRejectedByDatabase()
    {
        var seed = await SeedHierarchyAsync();
        await using var context = _database.CreateContext();
        var request = AddDraft(context, seed.TenantId, seed.User.Id);
        await context.SaveChangesAsync();

        // RR-STS-001: there is no RETURNED state; Return for Correction goes back to DRAFT.
        await AssertSqlRejectedAsync(
            () => context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE [repair_request] SET [status] = 'RETURNED' WHERE [repair_request_id] = {request.Id}"),
            "CK_repair_request_status");
    }

    [Fact]
    public async Task RepairRequest_SiteOfAnotherTenant_IsRejected()
    {
        var seed = await SeedHierarchyAsync();
        var otherTenant = await SeedHierarchyAsync();
        await using var context = _database.CreateContext();

        AddDraft(context, seed.TenantId, seed.User.Id, entry => entry.Property(r => r.SiteId).CurrentValue = otherTenant.Site.Id);

        await AssertSaveRejectedAsync(context, ConstraintViolation, "FK_repair_request_site");
    }

    [Fact]
    public async Task RepairRequest_EquipmentFromAnotherSite_IsRejected()
    {
        var seed = await SeedHierarchyAsync();
        await using var context = _database.CreateContext();

        var otherSite = new Site(seed.TenantId, seed.Customer.Id, NewCode("S"));
        context.Sites.Add(otherSite);
        await context.SaveChangesAsync();

        AddDraft(context, seed.TenantId, seed.User.Id, entry =>
        {
            entry.Property(r => r.SiteId).CurrentValue = otherSite.Id;
            entry.Property(r => r.EquipmentId).CurrentValue = seed.Equipment.Id;
        });

        await AssertSaveRejectedAsync(context, ConstraintViolation, "FK_repair_request_equipment");
    }

    [Fact]
    public async Task RepairRequest_EquipmentWithoutSite_IsRejected()
    {
        var seed = await SeedHierarchyAsync();
        await using var context = _database.CreateContext();

        AddDraft(context, seed.TenantId, seed.User.Id, entry => entry.Property(r => r.EquipmentId).CurrentValue = seed.Equipment.Id);

        await AssertSaveRejectedAsync(context, ConstraintViolation, "CK_repair_request_equipment_requires_site");
    }

    [Fact]
    public async Task RepairRequest_RequesterFromAnotherTenant_IsRejected()
    {
        var seed = await SeedHierarchyAsync();
        var otherTenant = await SeedHierarchyAsync();
        await using var context = _database.CreateContext();

        AddDraft(context, seed.TenantId, otherTenant.User.Id);

        await AssertSaveRejectedAsync(context, ConstraintViolation, "FK_repair_request_created_by_user");
    }

    [Fact]
    public async Task RepairRequest_PreferredEndBeforeStart_IsRejected()
    {
        var seed = await SeedHierarchyAsync();
        await using var context = _database.CreateContext();
        var start = new DateTime(2026, 9, 14, 9, 0, 0, DateTimeKind.Utc);

        AddDraft(context, seed.TenantId, seed.User.Id, entry =>
        {
            entry.Property(r => r.PreferredStartAt).CurrentValue = start;
            entry.Property(r => r.PreferredEndAt).CurrentValue = start.AddMinutes(-1);
        });

        await AssertSaveRejectedAsync(context, ConstraintViolation, "CK_repair_request_preferred_window");
    }

    [Fact]
    public async Task RequestNo_IsUniqueOnceAssigned_WhileDraftsHaveNone()
    {
        var seed = await SeedHierarchyAsync();
        var requestNo = $"RR-{Guid.NewGuid():N}"[..RepairRequestAggregate.RequestNoMaxLength];
        await using var context = _database.CreateContext();

        AddDraft(context, seed.TenantId, seed.User.Id);
        AddDraft(context, seed.TenantId, seed.User.Id);
        AddDraft(context, seed.TenantId, seed.User.Id, entry => entry.Property(r => r.RequestNo).CurrentValue = requestNo);
        Assert.Equal(3, await context.SaveChangesAsync());

        AddDraft(context, seed.TenantId, seed.User.Id, entry => entry.Property(r => r.RequestNo).CurrentValue = requestNo);

        await AssertSaveRejectedAsync(context, UniqueIndexViolation, "UQ_repair_request_request_no");
    }

    [Fact]
    public async Task StaleRowVersion_IsRejectedWithoutPartialWrite()
    {
        var seed = await SeedHierarchyAsync();
        await using var first = _database.CreateContext();
        await using var second = _database.CreateContext();

        var firstCopy = await first.Customers.SingleAsync(c => c.Id == seed.Customer.Id);
        var staleCopy = await second.Customers.SingleAsync(c => c.Id == seed.Customer.Id);

        firstCopy.Deactivate("First writer");
        await first.SaveChangesAsync();

        staleCopy.Deactivate("Stale writer");
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());

        await using var verify = _database.CreateContext();
        var stored = await verify.Customers.AsNoTracking().SingleAsync(c => c.Id == seed.Customer.Id);
        Assert.Equal("First writer", stored.DeactivateReason);
    }

    // ---------------- Attachments / files ----------------

    [Fact]
    public async Task Attachment_LinksRequestToPendingFileAsset()
    {
        var seed = await SeedHierarchyAsync();
        await using var context = _database.CreateContext();

        var request = AddDraft(context, seed.TenantId, seed.User.Id);
        var file = new FileAsset(seed.TenantId, "photo.png", "image/png", 2048, ValidHash, "private/ref/photo", seed.User.Id, DateTime.UtcNow);
        context.FileAssets.Add(file);
        context.RepairRequestAttachments.Add(new RepairRequestAttachment(request.Id, file.Id, "REQUEST_SITE"));

        Assert.Equal(3, await context.SaveChangesAsync());

        await using var verify = _database.CreateContext();
        var storedScanStatus = await verify.Database
            .SqlQuery<string>($"SELECT [malware_scan_status] AS [Value] FROM [file_asset] WHERE [file_asset_id] = {file.Id}")
            .SingleAsync();
        Assert.Equal("PENDING", storedScanStatus);
    }

    [Fact]
    public async Task FileAsset_UploadedByUserOfAnotherTenant_IsRejected()
    {
        var seed = await SeedHierarchyAsync();
        var otherTenant = await SeedHierarchyAsync();
        await using var context = _database.CreateContext();

        context.FileAssets.Add(new FileAsset(seed.TenantId, "doc.pdf", "application/pdf", 10, ValidHash, "private/ref/doc", otherTenant.User.Id, DateTime.UtcNow));

        await AssertSaveRejectedAsync(context, ConstraintViolation, "FK_file_asset_uploaded_by_user");
    }

    [Fact]
    public async Task FileAsset_UnknownScanStatus_IsRejectedByDatabase()
    {
        var seed = await SeedHierarchyAsync();
        await using var context = _database.CreateContext();
        var file = new FileAsset(seed.TenantId, "doc.pdf", "application/pdf", 10, ValidHash, "private/ref/doc", seed.User.Id, DateTime.UtcNow);
        context.FileAssets.Add(file);
        await context.SaveChangesAsync();

        await AssertSqlRejectedAsync(
            () => context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE [file_asset] SET [malware_scan_status] = 'SKIPPED' WHERE [file_asset_id] = {file.Id}"),
            "CK_file_asset_malware_scan_status");
    }

    // ---------------- Identity data scope (DEC-PS1-004) ----------------

    [Fact]
    public async Task UserSiteScope_WithinTenant_IsAllowed()
    {
        var seed = await SeedHierarchyAsync();
        await using var context = _database.CreateContext();

        context.UserSiteScopes.Add(new UserSiteScope(seed.TenantId, seed.User.Id, seed.Site.Id));

        Assert.Equal(1, await context.SaveChangesAsync());
    }

    [Fact]
    public async Task UserSiteScope_SiteOfAnotherTenant_IsRejected()
    {
        var seed = await SeedHierarchyAsync();
        var otherTenant = await SeedHierarchyAsync();
        await using var context = _database.CreateContext();

        context.UserSiteScopes.Add(new UserSiteScope(seed.TenantId, seed.User.Id, otherTenant.Site.Id));

        await AssertSaveRejectedAsync(context, ConstraintViolation, "FK_user_site_scope_site");
    }

    // ---------------- Audit history (append-only) ----------------

    private static AuditHistory NewAudit(Guid tenantId, string? newValueJson = "{\"status\":\"SUBMITTED\"}") =>
        new(
            tenantId,
            "REPAIR_REQUEST",
            Guid.NewGuid(),
            "SUBMIT",
            "DRAFT",
            "SUBMITTED",
            "{\"status\":\"DRAFT\"}",
            newValueJson,
            null,
            Guid.NewGuid(),
            new DateTime(2026, 9, 13, 1, 2, 3, 456, DateTimeKind.Utc),
            Guid.NewGuid());

    [Fact]
    public async Task AuditHistory_Insert_RoundTripsUtcTimestamp()
    {
        var audit = NewAudit(Guid.NewGuid());

        await using (var context = _database.CreateContext())
        {
            context.AuditHistory.Add(audit);
            await context.SaveChangesAsync();
        }

        await using var verify = _database.CreateContext();
        var stored = await verify.AuditHistory.AsNoTracking().SingleAsync(a => a.Id == audit.Id);

        Assert.Equal(audit.OccurredAt, stored.OccurredAt);
        Assert.Equal(DateTimeKind.Utc, stored.OccurredAt.Kind);
    }

    [Fact]
    public async Task AuditHistory_UpdateOrDelete_IsRejected()
    {
        var audit = NewAudit(Guid.NewGuid());
        await using var context = _database.CreateContext();
        context.AuditHistory.Add(audit);
        await context.SaveChangesAsync();

        context.Entry(audit).State = EntityState.Modified;
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());

        context.Entry(audit).State = EntityState.Deleted;
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task AuditHistory_InvalidJson_IsRejected()
    {
        await using var context = _database.CreateContext();

        context.AuditHistory.Add(NewAudit(Guid.NewGuid(), newValueJson: "not-json"));

        await AssertSaveRejectedAsync(context, ConstraintViolation, "CK_audit_history_new_value_json");
    }
}
