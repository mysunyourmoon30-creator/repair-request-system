using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using RepairRequest.Domain.Auditing;
using RepairRequest.Domain.Files;
using RepairRequest.Domain.MasterData;
using RepairRequest.Domain.RepairRequests;
using RepairRequest.Infrastructure.Identity;
using RepairRequest.Infrastructure.Persistence;
using RepairRequest.Infrastructure.RepairRequests;
using RepairRequestAggregate = RepairRequest.Domain.RepairRequests.RepairRequest;

namespace RepairRequest.IntegrationTests.Persistence;

/// <summary>
/// Model-level checks that need no database connection: migration drift, baseline
/// naming, bounded columns, delete behavior, concurrency tokens, state codes and indexes.
/// </summary>
public class PersistenceModelTests
{
    private static readonly Type[] BusinessEntityTypes =
    [
        typeof(Customer), typeof(Site), typeof(Equipment), typeof(RepairRequestAggregate),
        typeof(RepairRequestAttachment), typeof(FileAsset), typeof(AuditHistory), typeof(UserSiteScope),
        typeof(RefreshToken), typeof(RequestCategory), typeof(RequestPriority), typeof(RequestNumberCounter)
    ];

    private static RepairRequestDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<RepairRequestDbContext>()
            .UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=RepairRequestDb_ModelOnly;Trusted_Connection=True")
            .Options);

    private static IModel DesignTimeModel(RepairRequestDbContext context) =>
        context.GetService<IDesignTimeModel>().Model;

    private static IEntityType Entity<T>(IModel model) => model.FindEntityType(typeof(T))!;

    [Fact]
    public void Migrations_AreUpToDateWithModel()
    {
        using var context = CreateContext();

        Assert.False(context.Database.HasPendingModelChanges());
    }

    [Theory]
    [InlineData(typeof(Customer), "customer")]
    [InlineData(typeof(Site), "site")]
    [InlineData(typeof(Equipment), "equipment")]
    [InlineData(typeof(RepairRequestAggregate), "repair_request")]
    [InlineData(typeof(RepairRequestAttachment), "request_attachment")]
    [InlineData(typeof(FileAsset), "file_asset")]
    [InlineData(typeof(AuditHistory), "audit_history")]
    [InlineData(typeof(UserSiteScope), "user_site_scope")]
    [InlineData(typeof(RefreshToken), "refresh_token")]
    [InlineData(typeof(RequestCategory), "request_category")]
    [InlineData(typeof(RequestPriority), "request_priority")]
    [InlineData(typeof(RequestNumberCounter), "request_no_counter")]
    public void BusinessEntities_MapToBaselineTableNames(Type clrType, string tableName)
    {
        using var context = CreateContext();

        Assert.Equal(tableName, DesignTimeModel(context).FindEntityType(clrType)!.GetTableName());
    }

    [Fact]
    public void BusinessStringColumns_AreBounded_ExceptAuditJsonDocuments()
    {
        using var context = CreateContext();
        var model = DesignTimeModel(context);
        string[] unboundedByDesign = ["old_value_json", "new_value_json"];

        var unbounded = BusinessEntityTypes
            .Select(model.FindEntityType)
            .SelectMany(entityType => entityType!.GetProperties())
            .Where(property => property.ClrType == typeof(string) && property.GetMaxLength() is null)
            .Select(property => property.GetColumnName())
            .Except(unboundedByDesign);

        Assert.Empty(unbounded);
    }

    [Fact]
    public void BusinessForeignKeys_NeverCascade()
    {
        using var context = CreateContext();

        var foreignKeys = BusinessEntityTypes
            .Select(DesignTimeModel(context).FindEntityType)
            .SelectMany(entityType => entityType!.GetForeignKeys())
            .ToList();

        Assert.NotEmpty(foreignKeys);
        Assert.All(foreignKeys, foreignKey => Assert.Equal(DeleteBehavior.Restrict, foreignKey.DeleteBehavior));
    }

    [Theory]
    [InlineData(typeof(Customer))]
    [InlineData(typeof(Site))]
    [InlineData(typeof(Equipment))]
    [InlineData(typeof(RepairRequestAggregate))]
    public void MutableAggregates_UseSqlRowVersionConcurrencyToken(Type clrType)
    {
        using var context = CreateContext();
        var rowVersion = DesignTimeModel(context).FindEntityType(clrType)!.FindProperty("RowVersion")!;

        Assert.True(rowVersion.IsConcurrencyToken);
        Assert.Equal(ValueGenerated.OnAddOrUpdate, rowVersion.ValueGenerated);
        Assert.Equal("row_version", rowVersion.GetColumnName());
    }

    [Fact]
    public void RepairRequestStatus_PersistsCanonicalCodes()
    {
        using var context = CreateContext();
        var entityType = Entity<RepairRequestAggregate>(DesignTimeModel(context));
        var converter = entityType.FindProperty(nameof(RepairRequestAggregate.Status))!.GetValueConverter()!;

        var codes = Enum.GetValues<RepairRequestStatus>().Select(status => converter.ConvertToProvider(status));

        Assert.Equal(["DRAFT", "SUBMITTED", "UNDER_REVIEW", "APPROVED", "REJECTED", "CANCELLED", "CONVERTED"], codes);

        var statusCheck = entityType.GetCheckConstraints().Single(check => check.Name == "CK_repair_request_status").Sql;
        Assert.Contains("'UNDER_REVIEW'", statusCheck);
        Assert.DoesNotContain("RETURNED", statusCheck);
    }

    [Fact]
    public void MasterDataAndScanStatus_PersistCanonicalCodes()
    {
        using var context = CreateContext();
        var model = DesignTimeModel(context);

        var masterConverter = Entity<Customer>(model).FindProperty(nameof(Customer.Status))!.GetValueConverter()!;
        var scanConverter = Entity<FileAsset>(model).FindProperty(nameof(FileAsset.MalwareScanStatus))!.GetValueConverter()!;

        Assert.Equal(["ACTIVE", "INACTIVE"], Enum.GetValues<MasterDataStatus>().Select(value => masterConverter.ConvertToProvider(value)));
        Assert.Equal(["PENDING", "CLEAN", "FAILED"], Enum.GetValues<MalwareScanStatus>().Select(value => scanConverter.ConvertToProvider(value)));
    }

    [Theory]
    [InlineData(typeof(Customer), "UQ_customer_tenant_id_customer_code", new[] { "tenant_id", "customer_code" })]
    [InlineData(typeof(Site), "UQ_site_customer_id_site_code", new[] { "customer_id", "site_code" })]
    [InlineData(typeof(Equipment), "UQ_equipment_site_id_equipment_code", new[] { "site_id", "equipment_code" })]
    [InlineData(typeof(RepairRequestAggregate), "UQ_repair_request_tenant_id_request_no", new[] { "tenant_id", "request_no" })]
    public void UniqueIndexes_MatchBaselineScope(Type clrType, string indexName, string[] columns)
    {
        using var context = CreateContext();
        var index = DesignTimeModel(context).FindEntityType(clrType)!.GetIndexes().Single(i => i.GetDatabaseName() == indexName);

        Assert.True(index.IsUnique);
        Assert.Equal(columns, index.Properties.Select(property => property.GetColumnName()));
    }

    [Fact]
    public void RequestListSearchIndex_FollowsRrDbd001AccessPath()
    {
        using var context = CreateContext();
        var index = Entity<RepairRequestAggregate>(DesignTimeModel(context)).GetIndexes()
            .Single(i => i.GetDatabaseName() == "IX_repair_request_list_search");

        Assert.Equal(["tenant_id", "site_id", "status", "submitted_at"], index.Properties.Select(property => property.GetColumnName()));
    }

    [Fact]
    public void AuditTimelineIndex_OrdersOccurredAtDescending()
    {
        using var context = CreateContext();
        var index = Entity<AuditHistory>(DesignTimeModel(context)).GetIndexes()
            .Single(i => i.GetDatabaseName() == "IX_audit_history_timeline");

        Assert.Equal(["tenant_id", "entity_type", "entity_id", "occurred_at"], index.Properties.Select(property => property.GetColumnName()));
        Assert.Equal([false, false, false, true], index.IsDescending);
    }

    [Fact]
    public void BusinessTimestamps_AreUtcDatetime2Precision3()
    {
        using var context = CreateContext();

        var timestamps = BusinessEntityTypes
            .Select(DesignTimeModel(context).FindEntityType)
            .SelectMany(entityType => entityType!.GetProperties())
            .Where(property => property.ClrType == typeof(DateTime) || property.ClrType == typeof(DateTime?))
            .ToList();

        Assert.NotEmpty(timestamps);
        Assert.All(timestamps, property =>
        {
            Assert.Equal(3, property.GetPrecision());
            Assert.NotNull(property.GetValueConverter());
        });
    }

    [Fact]
    public void RefreshToken_StoresOnlyFixedLengthHash_AndIsIndexedForLookupAndFamilyRevocation()
    {
        using var context = CreateContext();
        var entityType = Entity<RefreshToken>(DesignTimeModel(context));

        Assert.DoesNotContain(entityType.GetProperties(), property => property.ClrType == typeof(string));
        Assert.Equal("binary(32)", entityType.FindProperty(nameof(RefreshToken.TokenHash))!.GetColumnType());

        var hashIndex = entityType.GetIndexes().Single(i => i.GetDatabaseName() == "UQ_refresh_token_token_hash");
        Assert.True(hashIndex.IsUnique);
        Assert.Equal(["token_hash"], hashIndex.Properties.Select(property => property.GetColumnName()));

        var familyIndex = entityType.GetIndexes().Single(i => i.GetDatabaseName() == "IX_refresh_token_family_id");
        Assert.Equal(["family_id"], familyIndex.Properties.Select(property => property.GetColumnName()));
    }

    [Fact]
    public void ApplicationUser_EmailLoginIdentifierIsUnique()
    {
        using var context = CreateContext();
        var emailIndex = Entity<ApplicationUser>(DesignTimeModel(context)).GetIndexes()
            .Single(i => i.GetDatabaseName() == "EmailIndex");

        Assert.True(emailIndex.IsUnique);
        Assert.Equal([nameof(ApplicationUser.NormalizedEmail)], emailIndex.Properties.Select(property => property.Name));
    }

    [Fact]
    public void ApplicationUser_CarriesRequiredTenantScope()
    {
        using var context = CreateContext();
        var user = Entity<ApplicationUser>(DesignTimeModel(context));

        Assert.False(user.FindProperty(nameof(ApplicationUser.TenantId))!.IsNullable);
        Assert.Contains(user.GetKeys(), key => key.Properties.Select(p => p.Name).SequenceEqual([nameof(ApplicationUser.TenantId), nameof(ApplicationUser.Id)]));
    }
}
