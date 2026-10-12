using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using RepairRequest.Domain.Files;
using RepairRequest.Domain.WorkEvidence;
using RepairRequest.Infrastructure.Persistence;
using RepairRequest.IntegrationTests.Authentication;
using RepairRequest.IntegrationTests.Persistence;

namespace RepairRequest.IntegrationTests.WorkEvidence;

/// <summary>
/// `work_evidence_upload` and `work_summary_evidence` against LocalDB (`docs/13` §4.28): required columns, tenant-consistent composite foreign
/// keys, uniqueness, the 1..10 slot rule, the closed evidence-type check and the additive no-backfill migration.
/// </summary>
[Collection(PersistenceDatabaseCollection.Name)]
public sealed class WorkEvidenceSchemaTests : IAsyncLifetime
{
    private const string PreviousMigration = "20261011122532_AddSlaRecord";
    private const string ValidHash = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";
    private const string ScratchConnection =
        "Server=(localdb)\\MSSQLLocalDB;Database=RepairRequestDb_WorkEvidenceMigrationTest;Trusted_Connection=True;TrustServerCertificate=True";

    private readonly AuthenticationTestHost _host = new();

    public WorkEvidenceSchemaTests(PersistenceDatabaseFixture database)
    {
        _ = database;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private async Task<T> WithDbAsync<T>(Func<RepairRequestDbContext, Task<T>> action)
    {
        await using var scope = _host.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<RepairRequestDbContext>());
    }

    private Task WithDbAsync(Func<RepairRequestDbContext, Task> action) =>
        WithDbAsync(async db =>
        {
            await action(db);
            return 0;
        });

    private async Task<Guid> AddFileAsync(Guid tenantId, Guid uploadedBy, bool clean = true)
    {
        return await WithDbAsync(async db =>
        {
            var file = new FileAsset(tenantId, "evidence.png", "image/png", 1024, ValidHash, $"{tenantId:N}/{Guid.NewGuid():N}", uploadedBy, DateTime.UtcNow);
            if (clean)
            {
                file.MarkClean();
            }

            db.FileAssets.Add(file);
            await db.SaveChangesAsync();
            return file.Id;
        });
    }

    private async Task<Guid> AddSummaryAsync(EvidenceWorld world, Guid? visitId = null)
    {
        return await WithDbAsync(async db =>
        {
            var summary = Domain.WorkOrders.WorkSummary.Create(
                world.Scope.TenantId, world.WorkOrderId, visitId ?? world.VisitId, "Replaced the bearing.", Domain.WorkOrders.RepairOutcomeCode.Repaired);
            db.WorkSummaries.Add(summary);
            await db.SaveChangesAsync();
            return summary.Id;
        });
    }

    private static async Task AssertViolationAsync(Func<Task> action, int number, string constraint)
    {
        var exception = await Assert.ThrowsAnyAsync<Exception>(action);
        var sql = exception as SqlException ?? exception.InnerException as SqlException ?? exception.InnerException?.InnerException as SqlException;
        Assert.NotNull(sql);
        Assert.Equal(number, sql.Number);
        Assert.Contains(constraint, sql.Message);
    }

    // ---------------- schema ----------------

    [Fact]
    public async Task Tables_HaveExactlyTheApprovedColumns_AllRequired()
    {
        var upload = await WithDbAsync(db => db.Database.SqlQuery<string>(
            $"SELECT [COLUMN_NAME] + ':' + [IS_NULLABLE] AS [Value] FROM [INFORMATION_SCHEMA].[COLUMNS] WHERE [TABLE_NAME] = 'work_evidence_upload'").ToListAsync());
        var bound = await WithDbAsync(db => db.Database.SqlQuery<string>(
            $"SELECT [COLUMN_NAME] + ':' + [IS_NULLABLE] AS [Value] FROM [INFORMATION_SCHEMA].[COLUMNS] WHERE [TABLE_NAME] = 'work_summary_evidence'").ToListAsync());

        Assert.Equal(
            new[]
            {
                "created_at:NO", "evidence_type_code:NO", "file_asset_id:NO", "service_visit_id:NO", "tenant_id:NO", "upload_seq:NO", "work_evidence_id:NO", "work_order_id:NO"
            },
            upload.Order(StringComparer.Ordinal));
        Assert.Equal(
            new[]
            {
                "bound_at:NO", "evidence_id:NO", "evidence_type_code:NO", "file_asset_id:NO", "service_visit_id:NO", "slot_no:NO", "tenant_id:NO",
                "work_order_id:NO", "work_summary_id:NO"
            },
            bound.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ForeignKeys_UniqueConstraints_AndChecks_ExistWithTheApprovedNames()
    {
        var foreignKeys = await WithDbAsync(db => db.Database.SqlQuery<string>(
            $"SELECT [name] AS [Value] FROM [sys].[foreign_keys] WHERE [parent_object_id] IN (OBJECT_ID('work_evidence_upload'), OBJECT_ID('work_summary_evidence'))").ToListAsync());
        Assert.Equal(
            new[]
            {
                "FK_work_evidence_upload_file_asset", "FK_work_evidence_upload_service_visit", "FK_work_evidence_upload_work_order",
                "FK_work_summary_evidence_upload", "FK_work_summary_evidence_work_summary"
            },
            foreignKeys.Order(StringComparer.Ordinal));

        var noCascade = await WithDbAsync(db => db.Database.SqlQuery<int>(
            $"SELECT COUNT(*) AS [Value] FROM [sys].[foreign_keys] WHERE [parent_object_id] IN (OBJECT_ID('work_evidence_upload'), OBJECT_ID('work_summary_evidence')) AND [delete_referential_action] <> 0").SingleAsync());
        Assert.Equal(0, noCascade);

        var alternateKeys = await WithDbAsync(db => db.Database.SqlQuery<string>(
            $"SELECT [name] AS [Value] FROM [sys].[key_constraints] WHERE [type] = 'UQ' AND [name] LIKE 'AK_%' AND [name] IN ('AK_work_order_tenant_id_work_order_id','AK_service_visit_work_order_id_service_visit_id','AK_file_asset_tenant_id_file_asset_id','AK_work_summary_round','AK_work_evidence_upload_snapshot')").ToListAsync());
        Assert.Equal(5, alternateKeys.Count);

        var uniqueIndexes = await WithDbAsync(db => db.Database.SqlQuery<string>(
            $"SELECT [name] AS [Value] FROM [sys].[indexes] WHERE [is_unique] = 1 AND [is_primary_key] = 0 AND [is_unique_constraint] = 0 AND [object_id] IN (OBJECT_ID('work_evidence_upload'), OBJECT_ID('work_summary_evidence'))").ToListAsync());
        Assert.Equal(
            new[] { "UQ_work_evidence_upload_file_asset_id", "UQ_work_summary_evidence_work_summary_id_slot_no" },
            uniqueIndexes.Order(StringComparer.Ordinal));

        var checks = await WithDbAsync(db => db.Database.SqlQuery<string>(
            $"SELECT [name] AS [Value] FROM [sys].[check_constraints] WHERE [parent_object_id] IN (OBJECT_ID('work_evidence_upload'), OBJECT_ID('work_summary_evidence'))").ToListAsync());
        Assert.Equal(
            new[] { "CK_work_evidence_upload_evidence_type", "CK_work_summary_evidence_evidence_type", "CK_work_summary_evidence_slot_no" },
            checks.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task NoColumnStoresAStorageReferenceOrAScanStatus_TheScanStatusLivesOnlyOnTheFileAsset()
    {
        var columns = await WithDbAsync(db => db.Database.SqlQuery<string>(
            $"SELECT [COLUMN_NAME] AS [Value] FROM [INFORMATION_SCHEMA].[COLUMNS] WHERE [TABLE_NAME] IN ('work_evidence_upload','work_summary_evidence')").ToListAsync());

        Assert.DoesNotContain(columns, column => column.Contains("storage", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(columns, column => column.Contains("scan", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(columns, column => column.Contains("row_version", StringComparison.OrdinalIgnoreCase));
    }

    // ---------------- integrity: uploads ----------------

    [Fact]
    public async Task AnUploadForAnotherTenantsFile_AWorkOrderOfAnotherTenant_OrAVisitOfAnotherWorkOrder_IsRejectedByTheCompositeForeignKeys()
    {
        var world = await EvidenceWorlds.CreateAsync(_host);
        var other = await EvidenceWorlds.CreateAsync(_host);
        var ownFile = await AddFileAsync(world.Scope.TenantId, world.Technician.UserId);
        var foreignFile = await AddFileAsync(other.Scope.TenantId, other.Technician.UserId);

        // Foreign tenant file under this tenant's Work Order.
        await AssertViolationAsync(
            () => WithDbAsync(async db =>
            {
                db.WorkEvidenceUploads.Add(WorkEvidenceUpload.Create(world.Scope.TenantId, world.WorkOrderId, world.VisitId, foreignFile, WorkEvidenceType.Other, DateTime.UtcNow));
                await db.SaveChangesAsync();
            }),
            547,
            "FK_work_evidence_upload_file_asset");

        // This tenant's id on the other tenant's Work Order.
        await AssertViolationAsync(
            () => WithDbAsync(async db =>
            {
                db.WorkEvidenceUploads.Add(WorkEvidenceUpload.Create(world.Scope.TenantId, other.WorkOrderId, other.VisitId, ownFile, WorkEvidenceType.Other, DateTime.UtcNow));
                await db.SaveChangesAsync();
            }),
            547,
            "FK_work_evidence_upload_work_order");

        // A Visit that belongs to a different Work Order.
        await AssertViolationAsync(
            () => WithDbAsync(async db =>
            {
                db.WorkEvidenceUploads.Add(WorkEvidenceUpload.Create(world.Scope.TenantId, world.WorkOrderId, other.VisitId, ownFile, WorkEvidenceType.Other, DateTime.UtcNow));
                await db.SaveChangesAsync();
            }),
            547,
            "FK_work_evidence_upload_service_visit");

        Assert.Equal(0, await WithDbAsync(db => db.WorkEvidenceUploads.CountAsync(item => item.FileAssetId == ownFile || item.FileAssetId == foreignFile)));
    }

    [Fact]
    public async Task OneFileAssetCanBeOneEvidenceItemOnly_AndTheTypeIsTheClosedCatalog()
    {
        var world = await EvidenceWorlds.CreateAsync(_host);
        var file = await AddFileAsync(world.Scope.TenantId, world.Technician.UserId);
        await WithDbAsync(async db =>
        {
            db.WorkEvidenceUploads.Add(WorkEvidenceUpload.Create(world.Scope.TenantId, world.WorkOrderId, world.VisitId, file, WorkEvidenceType.Other, DateTime.UtcNow));
            await db.SaveChangesAsync();
        });

        await AssertViolationAsync(
            () => WithDbAsync(async db =>
            {
                db.WorkEvidenceUploads.Add(WorkEvidenceUpload.Create(world.Scope.TenantId, world.WorkOrderId, world.VisitId, file, WorkEvidenceType.AfterWork, DateTime.UtcNow));
                await db.SaveChangesAsync();
            }),
            2601,
            "UQ_work_evidence_upload_file_asset_id");

        var second = await AddFileAsync(world.Scope.TenantId, world.Technician.UserId);
        var exception = await Assert.ThrowsAsync<SqlException>(() => WithDbAsync(db => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO [work_evidence_upload] ([work_evidence_id], [tenant_id], [work_order_id], [service_visit_id], [file_asset_id], [evidence_type_code], [created_at])
            VALUES ({Guid.NewGuid()}, {world.Scope.TenantId}, {world.WorkOrderId}, {world.VisitId}, {second}, 'DURING_WORK', {DateTime.UtcNow})
            """)));
        Assert.Equal(547, exception.Number);
        Assert.Contains("CK_work_evidence_upload_evidence_type", exception.Message);
    }

    // ---------------- integrity: bound snapshot ----------------

    private async Task<WorkEvidenceUpload> AddUploadAsync(EvidenceWorld world, WorkEvidenceType type = WorkEvidenceType.AfterWork)
    {
        var file = await AddFileAsync(world.Scope.TenantId, world.Technician.UserId);
        return await WithDbAsync(async db =>
        {
            var upload = WorkEvidenceUpload.Create(world.Scope.TenantId, world.WorkOrderId, world.VisitId, file, type, DateTime.UtcNow);
            db.WorkEvidenceUploads.Add(upload);
            await db.SaveChangesAsync();
            return upload;
        });
    }

    [Fact]
    public async Task ABoundSnapshot_HoldsSlotsOneToTen_AndRejectsAnEleventhOrADuplicateSlot()
    {
        var world = await EvidenceWorlds.CreateAsync(_host);
        var summaryId = await AddSummaryAsync(world);
        var uploads = new List<WorkEvidenceUpload>();
        for (var i = 0; i < 11; i++)
        {
            uploads.Add(await AddUploadAsync(world));
        }

        await WithDbAsync(async db =>
        {
            for (var slot = 1; slot <= 10; slot++)
            {
                db.WorkSummaryEvidence.Add(WorkSummaryEvidence.Bind(uploads[slot - 1], summaryId, slot, DateTime.UtcNow));
            }

            await db.SaveChangesAsync();
        });
        Assert.Equal(10, await WithDbAsync(db => db.WorkSummaryEvidence.CountAsync(item => item.WorkSummaryId == summaryId)));

        // An eleventh item has no free slot: slot 10 again is a duplicate, and slot 11 cannot be built.
        await AssertViolationAsync(
            () => WithDbAsync(async db =>
            {
                db.WorkSummaryEvidence.Add(WorkSummaryEvidence.Bind(uploads[10], summaryId, 10, DateTime.UtcNow));
                await db.SaveChangesAsync();
            }),
            2601,
            "UQ_work_summary_evidence_work_summary_id_slot_no");
        Assert.Throws<ArgumentOutOfRangeException>(() => WorkSummaryEvidence.Bind(uploads[10], summaryId, 11, DateTime.UtcNow));

        // The database also rejects slot 11 and slot 0 written directly.
        foreach (var slot in new[] { 11, 0 })
        {
            var exception = await Assert.ThrowsAsync<SqlException>(() => InsertSnapshotAsync(
                uploads[10].Id, world.Scope.TenantId, world.WorkOrderId, world.VisitId, summaryId, "AFTER_WORK", uploads[10].FileAssetId, slot));
            Assert.Equal(547, exception.Number);
            Assert.Contains("CK_work_summary_evidence_slot_no", exception.Message);
        }
    }

    [Fact]
    public async Task AnUploadIsBoundAtMostOnce_AndOnlyAsAnExistingUploadOfTheSameTenant()
    {
        var world = await EvidenceWorlds.CreateAsync(_host);
        var summaryId = await AddSummaryAsync(world);
        var upload = await AddUploadAsync(world);
        await WithDbAsync(async db =>
        {
            db.WorkSummaryEvidence.Add(WorkSummaryEvidence.Bind(upload, summaryId, 1, DateTime.UtcNow));
            await db.SaveChangesAsync();
        });

        // The same upload twice (same evidence id) — whatever the slot.
        await Assert.ThrowsAnyAsync<Exception>(() => WithDbAsync(async db =>
        {
            db.WorkSummaryEvidence.Add(WorkSummaryEvidence.Bind(upload, summaryId, 2, DateTime.UtcNow));
            await db.SaveChangesAsync();
        }));

        // A snapshot item that was never uploaded.
        var phantomFile = await AddFileAsync(world.Scope.TenantId, world.Technician.UserId);
        var exception = await Assert.ThrowsAsync<SqlException>(() => InsertSnapshotAsync(
            Guid.NewGuid(), world.Scope.TenantId, world.WorkOrderId, world.VisitId, summaryId, "OTHER", phantomFile, 2));
        Assert.Equal(547, exception.Number);
        Assert.Contains("FK_work_summary_evidence_upload", exception.Message);

        // Another tenant's id on this tenant's Work Summary.
        var other = await EvidenceWorlds.CreateAsync(_host);
        var foreignUpload = await AddUploadAsync(other);
        var crossTenant = await Assert.ThrowsAsync<SqlException>(() => InsertSnapshotAsync(
            foreignUpload.Id, other.Scope.TenantId, other.WorkOrderId, other.VisitId, summaryId, "AFTER_WORK", foreignUpload.FileAssetId, 3));
        Assert.Equal(547, crossTenant.Number);
        Assert.Contains("FK_work_summary_evidence_work_summary", crossTenant.Message);

        Assert.Equal(1, await WithDbAsync(db => db.WorkSummaryEvidence.CountAsync(item => item.WorkSummaryId == summaryId)));
    }

    // ---------------- integrity: the snapshot proves its upload and its round ----------------

    private Task<int> InsertSnapshotAsync(
        Guid evidenceId, Guid tenantId, Guid workOrderId, Guid visitId, Guid summaryId, string type, Guid fileAssetId, int slot) =>
        WithDbAsync(db => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO [work_summary_evidence] ([evidence_id], [tenant_id], [work_order_id], [service_visit_id], [work_summary_id], [evidence_type_code], [file_asset_id], [slot_no], [bound_at])
            VALUES ({evidenceId}, {tenantId}, {workOrderId}, {visitId}, {summaryId}, {type}, {fileAssetId}, {slot}, {DateTime.UtcNow})
            """));

    private async Task AssertSnapshotRejectedAsync(string constraint, Func<Task<int>> insert)
    {
        var exception = await Assert.ThrowsAsync<SqlException>(insert);
        Assert.Equal(547, exception.Number);
        Assert.Contains(constraint, exception.Message);
    }

    [Fact]
    public async Task ASnapshotRow_CanOnlyBeBuiltFromItsOwnUpload_SoTheTenantWorkOrderVisitFileAndTypeAllMatch()
    {
        var world = await EvidenceWorlds.CreateAsync(_host);
        var summaryId = await AddSummaryAsync(world);
        var upload = await AddUploadAsync(world, WorkEvidenceType.BeforeWork);
        var unrelatedFile = await AddFileAsync(world.Scope.TenantId, world.Technician.UserId);

        // The faithful row is accepted (the domain model builds exactly this).
        await InsertSnapshotAsync(upload.Id, world.Scope.TenantId, world.WorkOrderId, world.VisitId, summaryId, "BEFORE_WORK", upload.FileAssetId, 1);
        Assert.Equal(1, await WithDbAsync(db => db.WorkSummaryEvidence.CountAsync(item => item.WorkSummaryId == summaryId)));

        // Each deviation from the upload is refused by FK_work_summary_evidence_upload.
        var second = await AddUploadAsync(world, WorkEvidenceType.AfterWork);
        await AssertSnapshotRejectedAsync("FK_work_summary_evidence_upload", () => // a different file than the upload's
            InsertSnapshotAsync(second.Id, world.Scope.TenantId, world.WorkOrderId, world.VisitId, summaryId, "AFTER_WORK", unrelatedFile, 2));
        await AssertSnapshotRejectedAsync("FK_work_summary_evidence_upload", () => // another upload's file
            InsertSnapshotAsync(second.Id, world.Scope.TenantId, world.WorkOrderId, world.VisitId, summaryId, "AFTER_WORK", upload.FileAssetId, 2));
        await AssertSnapshotRejectedAsync("FK_work_summary_evidence_upload", () => // another evidence type than the upload's
            InsertSnapshotAsync(second.Id, world.Scope.TenantId, world.WorkOrderId, world.VisitId, summaryId, "OTHER", second.FileAssetId, 2));
        await AssertSnapshotRejectedAsync("FK_work_summary_evidence_upload", () => // another tenant than the upload's
            InsertSnapshotAsync(second.Id, Guid.NewGuid(), world.WorkOrderId, world.VisitId, summaryId, "AFTER_WORK", second.FileAssetId, 2));
        await AssertSnapshotRejectedAsync("FK_work_summary_evidence_upload", () => // another Work Order than the upload's
            InsertSnapshotAsync(second.Id, world.Scope.TenantId, Guid.NewGuid(), world.VisitId, summaryId, "AFTER_WORK", second.FileAssetId, 2));
        await AssertSnapshotRejectedAsync("FK_work_summary_evidence_upload", () => // another Visit than the upload's
            InsertSnapshotAsync(second.Id, world.Scope.TenantId, world.WorkOrderId, Guid.NewGuid(), summaryId, "AFTER_WORK", second.FileAssetId, 2));

        Assert.Equal(1, await WithDbAsync(db => db.WorkSummaryEvidence.CountAsync(item => item.WorkSummaryId == summaryId)));
    }

    [Fact]
    public async Task ASnapshotRow_CanOnlyJoinTheWorkSummaryOfItsOwnRound_NeverAnotherVisitsOrWorkOrdersSummary()
    {
        var world = await EvidenceWorlds.CreateAsync(_host);
        var firstSummary = await AddSummaryAsync(world);
        var firstUpload = await AddUploadAsync(world);

        // A corrective round (second Visit) with its own Work Summary and upload.
        var (secondVisit, _) = await EvidenceWorlds.AddSecondRoundAsync(_host, world);
        var secondSummary = await AddSummaryAsync(world, secondVisit);
        var secondFile = await AddFileAsync(world.Scope.TenantId, world.Technician.UserId);
        var secondUpload = await WithDbAsync(async db =>
        {
            var created = WorkEvidenceUpload.Create(world.Scope.TenantId, world.WorkOrderId, secondVisit, secondFile, WorkEvidenceType.AfterWork, DateTime.UtcNow);
            db.WorkEvidenceUploads.Add(created);
            await db.SaveChangesAsync();
            return created;
        });

        // The first Visit's upload cannot be frozen into the second Visit's summary, however the row declares its Visit.
        await AssertSnapshotRejectedAsync("FK_work_summary_evidence_work_summary", () =>
            InsertSnapshotAsync(firstUpload.Id, world.Scope.TenantId, world.WorkOrderId, world.VisitId, secondSummary, "AFTER_WORK", firstUpload.FileAssetId, 1));
        await AssertSnapshotRejectedAsync("FK_work_summary_evidence_upload", () =>
            InsertSnapshotAsync(firstUpload.Id, world.Scope.TenantId, world.WorkOrderId, secondVisit, secondSummary, "AFTER_WORK", firstUpload.FileAssetId, 1));

        // An upload of another Work Order of the same tenant cannot join this Work Order's summary either.
        var (otherWorkOrderId, otherVisitId) = await EvidenceWorlds.AddOtherWorkOrderAsync(_host, world);
        var otherFile = await AddFileAsync(world.Scope.TenantId, world.Technician.UserId);
        var otherUpload = await WithDbAsync(async db =>
        {
            var created = WorkEvidenceUpload.Create(world.Scope.TenantId, otherWorkOrderId, otherVisitId, otherFile, WorkEvidenceType.AfterWork, DateTime.UtcNow);
            db.WorkEvidenceUploads.Add(created);
            await db.SaveChangesAsync();
            return created;
        });
        await AssertSnapshotRejectedAsync("FK_work_summary_evidence_work_summary", () =>
            InsertSnapshotAsync(otherUpload.Id, world.Scope.TenantId, otherWorkOrderId, otherVisitId, firstSummary, "AFTER_WORK", otherFile, 1));
        await AssertSnapshotRejectedAsync("FK_work_summary_evidence_upload", () =>
            InsertSnapshotAsync(otherUpload.Id, world.Scope.TenantId, world.WorkOrderId, world.VisitId, firstSummary, "AFTER_WORK", otherFile, 1));

        // The matching pairs are accepted, each in its own round.
        await InsertSnapshotAsync(firstUpload.Id, world.Scope.TenantId, world.WorkOrderId, world.VisitId, firstSummary, "AFTER_WORK", firstUpload.FileAssetId, 1);
        await InsertSnapshotAsync(secondUpload.Id, world.Scope.TenantId, world.WorkOrderId, secondVisit, secondSummary, "AFTER_WORK", secondFile, 1);
        Assert.Equal(2, await WithDbAsync(db => db.WorkSummaryEvidence.CountAsync(item => item.WorkOrderId == world.WorkOrderId)));
    }

    [Fact]
    public async Task ASnapshotsUploadAndWorkSummary_CannotBeDeleted_WhileTheSnapshotExists()
    {
        var world = await EvidenceWorlds.CreateAsync(_host);
        var summaryId = await AddSummaryAsync(world);
        var upload = await AddUploadAsync(world);
        await WithDbAsync(async db =>
        {
            db.WorkSummaryEvidence.Add(WorkSummaryEvidence.Bind(upload, summaryId, 1, DateTime.UtcNow));
            await db.SaveChangesAsync();
        });

        // The frozen upload, its file and its Work Summary are protected by restrictive foreign keys.
        await AssertSnapshotRejectedAsync("FK_work_summary_evidence_upload", () =>
            WithDbAsync(db => db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM [work_evidence_upload] WHERE [work_evidence_id] = {upload.Id}")));
        await AssertSnapshotRejectedAsync("FK_work_summary_evidence_work_summary", () =>
            WithDbAsync(db => db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM [work_summary] WHERE [work_summary_id] = {summaryId}")));
    }

    // ---------------- migration ----------------

    [Fact]
    public async Task MigrationScript_ContainsNoDataStatementsOtherThanTheMigrationHistoryRow()
    {
        await using var scope = _host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RepairRequestDbContext>();
        var migrationId = db.Database.GetMigrations().Single(id => id.EndsWith("_AddWorkEvidence", StringComparison.Ordinal));
        var migrator = db.GetService<IMigrator>();

        var script = migrator.GenerateScript(PreviousMigration, migrationId);

        var statements = script
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith("--", StringComparison.Ordinal))
            .Where(line => !line.Contains("[__EFMigrationsHistory]", StringComparison.Ordinal))
            .ToList();

        Assert.Contains(statements, line => line.StartsWith("CREATE TABLE [work_evidence_upload]", StringComparison.Ordinal));
        Assert.Contains(statements, line => line.StartsWith("CREATE TABLE [work_summary_evidence]", StringComparison.Ordinal));
        Assert.DoesNotContain(statements, line => System.Text.RegularExpressions.Regex.IsMatch(
            line, @"^(INSERT|UPDATE|DELETE|MERGE|TRUNCATE)\b|\bSELECT\b.*\bINTO\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
    }

    [Fact]
    public async Task Migrating_APopulatedDatabase_CreatesNoEvidenceRowAndChangesNoHistoricalWorkSummary()
    {
        var options = new DbContextOptionsBuilder<RepairRequestDbContext>().UseSqlServer(ScratchConnection).Options;
        await using var db = new RepairRequestDbContext(options);
        await db.Database.EnsureDeletedAsync();
        try
        {
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync(PreviousMigration);
            Assert.Null(await db.Database.SqlQuery<int?>($"SELECT OBJECT_ID('work_evidence_upload') AS [Value]").SingleAsync());

            // A Work Summary submitted before evidence existed (no evidence by design).
            var summaryId = Guid.NewGuid();
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE [work_summary] NOCHECK CONSTRAINT ALL");
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO [work_summary] ([work_summary_id], [tenant_id], [work_order_id], [service_visit_id], [revision_no], [summary_text], [repair_outcome_code])
                VALUES ({summaryId}, {Guid.NewGuid()}, {Guid.NewGuid()}, {Guid.NewGuid()}, 1, 'Historic summary.', 'REPAIRED')
                """);
            var auditRowsBefore = await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM [audit_history]").SingleAsync();

            await migrator.MigrateAsync();

            Assert.Equal(0, await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM [work_evidence_upload]").SingleAsync());
            Assert.Equal(0, await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM [work_summary_evidence]").SingleAsync());
            Assert.Equal(auditRowsBefore, await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM [audit_history]").SingleAsync());
            Assert.Equal(
                "Historic summary.|REPAIRED",
                await db.Database.SqlQuery<string>($"SELECT [summary_text] + '|' + [repair_outcome_code] AS [Value] FROM [work_summary] WHERE [work_summary_id] = {summaryId}").SingleAsync());
        }
        finally
        {
            await db.Database.EnsureDeletedAsync();
        }
    }
}
