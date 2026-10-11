using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using RepairRequest.Domain.Security;
using RepairRequest.Domain.Sla;
using RepairRequest.Infrastructure.Persistence;
using RepairRequest.IntegrationTests.Authentication;
using RepairRequest.IntegrationTests.Authorization;
using RepairRequest.IntegrationTests.Persistence;

namespace RepairRequest.IntegrationTests.Sla;

/// <summary>
/// `sla_record` schema and database integrity against LocalDB (`docs/16` D5-D7): required columns, the tenant-consistent composite
/// foreign key, the one-record-per-request unique constraint, every time/stop check, and the additive no-backfill migration.
/// </summary>
[Collection(PersistenceDatabaseCollection.Name)]
public sealed class SlaSchemaTests : IAsyncLifetime
{
    private const string PreviousMigration = "20260929125930_AddCorrectiveActionRowVersion";
    private const string ScratchConnection =
        "Server=(localdb)\\MSSQLLocalDB;Database=RepairRequestDb_SlaMigrationTest;Trusted_Connection=True;TrustServerCertificate=True";

    private static readonly DateTime Start = new(2026, 10, 11, 8, 0, 0, DateTimeKind.Utc);
    private static readonly string[] ExpectedColumns =
    [
        "sla_record_id", "tenant_id", "repair_request_id", "sla_policy_version", "started_at", "original_due_at", "risk_at", "stopped_at", "stop_reason"
    ];

    private readonly AuthenticationTestHost _host = new();

    public SlaSchemaTests(PersistenceDatabaseFixture database)
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

    private async Task<(Guid TenantId, Guid RequestId, Guid OtherTenantId, Guid OtherRequestId)> SeedRequestsAsync()
    {
        var world = await WithDbAsync(ScopeWorld.CreateAsync);
        var user = await _host.CreateUserInTenantAsync(world.TenantId, RoleCodes.Requester);
        var otherUser = await _host.CreateUserInTenantAsync(world.OtherTenantId, RoleCodes.Requester);
        var requestId = await WithDbAsync(db => ScopeWorld.AddRequestAsync(db, world.TenantId, user.Id, world.SiteA1));
        var otherRequestId = await WithDbAsync(db => ScopeWorld.AddRequestAsync(db, world.OtherTenantId, otherUser.Id, world.OtherTenantSite));
        return (world.TenantId, requestId, world.OtherTenantId, otherRequestId);
    }

    private static Task<int> InsertRawAsync(
        RepairRequestDbContext db,
        Guid tenantId,
        Guid requestId,
        DateTime? started = null,
        DateTime? due = null,
        DateTime? risk = null,
        DateTime? stoppedAt = null,
        string? stopReason = null,
        string policy = "SLA-004/v1")
    {
        var startedAt = started ?? Start;
        var dueAt = due ?? startedAt.AddHours(24);
        var riskAt = risk ?? dueAt.AddHours(-4);
        return db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO [sla_record] ([sla_record_id], [tenant_id], [repair_request_id], [sla_policy_version], [started_at], [original_due_at], [risk_at], [stopped_at], [stop_reason])
            VALUES ({Guid.NewGuid()}, {tenantId}, {requestId}, {policy}, {startedAt}, {dueAt}, {riskAt}, {stoppedAt}, {stopReason})
            """);
    }

    private static async Task AssertConstraintViolationAsync(Func<Task> insert, int sqlNumber, string constraintName)
    {
        var exception = await Assert.ThrowsAsync<SqlException>(insert);
        Assert.Equal(sqlNumber, exception.Number);
        Assert.Contains(constraintName, exception.Message);
    }

    // ---------------- Schema ----------------

    [Fact]
    public async Task SlaRecord_HasExactlyTheApprovedColumns_OnlyStoppedAtAndStopReasonNullable_AndNoRowVersionOrFutureColumns()
    {
        var columns = await WithDbAsync(db => db.Database
            .SqlQuery<string>($"SELECT [COLUMN_NAME] AS [Value] FROM [INFORMATION_SCHEMA].[COLUMNS] WHERE [TABLE_NAME] = 'sla_record'")
            .ToListAsync());
        var nullable = await WithDbAsync(db => db.Database
            .SqlQuery<string>($"SELECT [COLUMN_NAME] AS [Value] FROM [INFORMATION_SCHEMA].[COLUMNS] WHERE [TABLE_NAME] = 'sla_record' AND [IS_NULLABLE] = 'YES'")
            .ToListAsync());

        Assert.Equal(ExpectedColumns.Order(StringComparer.Ordinal), columns.Order(StringComparer.Ordinal));
        Assert.Equal(new[] { "stop_reason", "stopped_at" }, nullable.Order(StringComparer.Ordinal));
        Assert.DoesNotContain("row_version", columns);
        Assert.DoesNotContain("effective_due_at", columns);
        Assert.DoesNotContain("risk_notified_at", columns);
        Assert.DoesNotContain("breached_at", columns);
    }

    [Fact]
    public async Task OnlyTheSlaRecordTableExists_NoWorkerNotificationOverrideOrScheduleObjects()
    {
        var tables = await WithDbAsync(db => db.Database
            .SqlQuery<string>($"SELECT [name] AS [Value] FROM [sys].[tables] WHERE [name] LIKE '%sla%' OR [name] LIKE '%outbox%' OR [name] LIKE '%notification%' OR [name] LIKE '%override%' OR [name] LIKE '%schedule%job%'")
            .ToListAsync());

        Assert.Equal(new[] { "sla_record" }, tables);
    }

    [Fact]
    public async Task UniqueIndex_AlternateKey_CompositeForeignKey_AndChecks_ExistWithTheApprovedNames()
    {
        var uniqueIndexColumns = await WithDbAsync(db => db.Database.SqlQuery<string>($"""
            SELECT [c].[name] AS [Value]
            FROM [sys].[indexes] [i]
            JOIN [sys].[index_columns] [ic] ON [ic].[object_id] = [i].[object_id] AND [ic].[index_id] = [i].[index_id]
            JOIN [sys].[columns] [c] ON [c].[object_id] = [ic].[object_id] AND [c].[column_id] = [ic].[column_id]
            WHERE [i].[object_id] = OBJECT_ID('sla_record') AND [i].[name] = 'UQ_sla_record_repair_request_id' AND [i].[is_unique] = 1
            """).ToListAsync());
        Assert.Equal(new[] { "repair_request_id" }, uniqueIndexColumns);

        var alternateKey = await WithDbAsync(db => db.Database.SqlQuery<string>($"""
            SELECT [c].[name] AS [Value]
            FROM [sys].[key_constraints] [k]
            JOIN [sys].[index_columns] [ic] ON [ic].[object_id] = [k].[parent_object_id] AND [ic].[index_id] = [k].[unique_index_id]
            JOIN [sys].[columns] [c] ON [c].[object_id] = [ic].[object_id] AND [c].[column_id] = [ic].[column_id]
            WHERE [k].[name] = 'AK_repair_request_tenant_id_repair_request_id' AND [k].[type] = 'UQ' AND [k].[parent_object_id] = OBJECT_ID('repair_request')
            ORDER BY [ic].[key_ordinal]
            """).ToListAsync());
        Assert.Equal(new[] { "tenant_id", "repair_request_id" }, alternateKey);

        var foreignKey = await WithDbAsync(db => db.Database.SqlQuery<string>($"""
            SELECT [pc].[name] + '->' + [rc].[name] + ':' + CAST([f].[delete_referential_action] AS varchar(5)) AS [Value]
            FROM [sys].[foreign_keys] [f]
            JOIN [sys].[foreign_key_columns] [fc] ON [fc].[constraint_object_id] = [f].[object_id]
            JOIN [sys].[columns] [pc] ON [pc].[object_id] = [fc].[parent_object_id] AND [pc].[column_id] = [fc].[parent_column_id]
            JOIN [sys].[columns] [rc] ON [rc].[object_id] = [fc].[referenced_object_id] AND [rc].[column_id] = [fc].[referenced_column_id]
            WHERE [f].[name] = 'FK_sla_record_repair_request' AND [f].[parent_object_id] = OBJECT_ID('sla_record') AND [f].[referenced_object_id] = OBJECT_ID('repair_request')
            ORDER BY [fc].[constraint_column_id]
            """).ToListAsync());

        // Restrict delete behavior is NO ACTION (0).
        Assert.Equal(new[] { "tenant_id->tenant_id:0", "repair_request_id->repair_request_id:0" }, foreignKey);

        var checks = await WithDbAsync(db => db.Database
            .SqlQuery<string>($"SELECT [name] AS [Value] FROM [sys].[check_constraints] WHERE [parent_object_id] = OBJECT_ID('sla_record')")
            .ToListAsync());
        Assert.Equal(
            new[]
            {
                "CK_sla_record_due_after_start", "CK_sla_record_risk_before_due", "CK_sla_record_stop_pair",
                "CK_sla_record_stop_reason", "CK_sla_record_stopped_after_start"
            },
            checks.Order(StringComparer.Ordinal));
    }

    // ---------------- Integrity ----------------

    [Fact]
    public async Task ValidActiveAndStoppedRows_AreAccepted_ThroughTheDomainEntity()
    {
        var (tenantId, requestId, _, _) = await SeedRequestsAsync();

        await WithDbAsync(async db =>
        {
            db.SlaRecords.Add(SlaRecord.Start(tenantId, requestId, Start));
            await db.SaveChangesAsync();
        });

        var stored = await WithDbAsync(db => db.SlaRecords.AsNoTracking().SingleAsync(record => record.RepairRequestId == requestId));
        Assert.Equal("SLA-004/v1", stored.SlaPolicyVersion);
        Assert.Equal(Start, stored.StartedAt);
        Assert.Equal(Start.AddHours(24), stored.OriginalDueAt);
        Assert.Equal(Start.AddHours(20), stored.RiskAt);
        Assert.Null(stored.StoppedAt);
        Assert.Null(stored.StopReason);
    }

    [Theory]
    [InlineData("REQUEST_REJECTED")]
    [InlineData("REQUEST_CANCELLED")]
    [InlineData("WO_CLOSED")]
    [InlineData("WO_CANCELLED")]
    public async Task AStoppedRow_WithEachApprovedReason_IsAcceptedAndMaterializesTheEnum(string reason)
    {
        var (tenantId, requestId, _, _) = await SeedRequestsAsync();

        await WithDbAsync(db => InsertRawAsync(db, tenantId, requestId, stoppedAt: Start.AddHours(1), stopReason: reason));

        var stored = await WithDbAsync(db => db.SlaRecords.AsNoTracking().SingleAsync(record => record.RepairRequestId == requestId));
        Assert.Equal(reason, SlaStopReasonCodes.ToCode(stored.StopReason!.Value));
        Assert.Equal(Start.AddHours(1), stored.StoppedAt);
    }

    [Fact]
    public async Task ASecondRecordForTheSameRepairRequest_IsRejectedByTheUniqueConstraint()
    {
        var (tenantId, requestId, _, _) = await SeedRequestsAsync();
        await WithDbAsync(db => InsertRawAsync(db, tenantId, requestId));

        await AssertConstraintViolationAsync(
            () => WithDbAsync(db => InsertRawAsync(db, tenantId, requestId, started: Start.AddHours(2))), 2601, "UQ_sla_record_repair_request_id");

        Assert.Equal(1, await WithDbAsync(db => db.SlaRecords.CountAsync(record => record.RepairRequestId == requestId)));
    }

    [Fact]
    public async Task ATenantThatIsNotTheRequestsTenant_IsRejectedByTheCompositeForeignKey()
    {
        var (tenantId, requestId, otherTenantId, otherRequestId) = await SeedRequestsAsync();

        // Right request, wrong tenant, and the other tenant's own request under this tenant: both are cross-tenant bindings.
        await AssertConstraintViolationAsync(() => WithDbAsync(db => InsertRawAsync(db, otherTenantId, requestId)), 547, "FK_sla_record_repair_request");
        await AssertConstraintViolationAsync(() => WithDbAsync(db => InsertRawAsync(db, tenantId, otherRequestId)), 547, "FK_sla_record_repair_request");
        await AssertConstraintViolationAsync(() => WithDbAsync(db => InsertRawAsync(db, tenantId, Guid.NewGuid())), 547, "FK_sla_record_repair_request");

        // The same binding through the EF entity.
        await Assert.ThrowsAsync<DbUpdateException>(() => WithDbAsync(async db =>
        {
            db.SlaRecords.Add(SlaRecord.Start(otherTenantId, requestId, Start));
            await db.SaveChangesAsync();
        }));

        Assert.Equal(0, await WithDbAsync(db => db.SlaRecords.CountAsync(record => record.RepairRequestId == requestId || record.RepairRequestId == otherRequestId)));
    }

    [Fact]
    public async Task ARepairRequestWithASlaRecord_CannotBeDeleted_RestrictDeleteBehavior()
    {
        var (tenantId, requestId, _, _) = await SeedRequestsAsync();
        await WithDbAsync(db => InsertRawAsync(db, tenantId, requestId));

        var exception = await Assert.ThrowsAsync<SqlException>(() => WithDbAsync(db =>
            db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM [repair_request] WHERE [repair_request_id] = {requestId}")));

        Assert.Equal(547, exception.Number);
        Assert.Contains("FK_sla_record_repair_request", exception.Message);
    }

    [Fact]
    public async Task EveryRequiredColumn_RejectsNull()
    {
        var (tenantId, requestId, _, _) = await SeedRequestsAsync();

        foreach (var column in new[] { "sla_record_id", "tenant_id", "repair_request_id", "sla_policy_version", "started_at", "original_due_at", "risk_at" })
        {
            var values = new Dictionary<string, string>
            {
                ["sla_record_id"] = $"'{Guid.NewGuid()}'",
                ["tenant_id"] = $"'{tenantId}'",
                ["repair_request_id"] = $"'{requestId}'",
                ["sla_policy_version"] = "'SLA-004/v1'",
                ["started_at"] = "'2026-10-11T08:00:00'",
                ["original_due_at"] = "'2026-10-12T08:00:00'",
                ["risk_at"] = "'2026-10-12T04:00:00'"
            };
            values[column] = "NULL";
            var sql = $"INSERT INTO [sla_record] ({string.Join(", ", values.Keys.Select(k => $"[{k}]"))}) VALUES ({string.Join(", ", values.Values)})";

            var exception = await Assert.ThrowsAsync<SqlException>(() => WithDbAsync(db => db.Database.ExecuteSqlRawAsync(sql)));
            Assert.Equal(515, exception.Number);
            Assert.Contains(column, exception.Message);
        }
    }

    [Fact]
    public async Task ADueTimeThatIsNotExactly24HoursAfterStart_IsRejected()
    {
        var (tenantId, requestId, _, _) = await SeedRequestsAsync();

        await AssertConstraintViolationAsync(
            () => WithDbAsync(db => InsertRawAsync(db, tenantId, requestId, due: Start.AddHours(23), risk: Start.AddHours(19))), 547, "CK_sla_record_due_after_start");
        await AssertConstraintViolationAsync(
            () => WithDbAsync(db => InsertRawAsync(db, tenantId, requestId, due: Start.AddHours(24).AddMilliseconds(1), risk: Start.AddHours(20).AddMilliseconds(1))), 547, "CK_sla_record_due_after_start");
    }

    [Fact]
    public async Task ARiskTimeThatIsNotExactly4HoursBeforeDue_IsRejected()
    {
        var (tenantId, requestId, _, _) = await SeedRequestsAsync();

        await AssertConstraintViolationAsync(
            () => WithDbAsync(db => InsertRawAsync(db, tenantId, requestId, risk: Start.AddHours(19))), 547, "CK_sla_record_risk_before_due");
        await AssertConstraintViolationAsync(
            () => WithDbAsync(db => InsertRawAsync(db, tenantId, requestId, risk: Start.AddHours(24))), 547, "CK_sla_record_risk_before_due");
    }

    [Fact]
    public async Task AStopBeforeTheStart_IsRejected_AndAStopAtTheStartIsAccepted()
    {
        var (tenantId, requestId, _, _) = await SeedRequestsAsync();

        await AssertConstraintViolationAsync(
            () => WithDbAsync(db => InsertRawAsync(db, tenantId, requestId, stoppedAt: Start.AddMilliseconds(-1), stopReason: "REQUEST_REJECTED")),
            547,
            "CK_sla_record_stopped_after_start");

        await WithDbAsync(db => InsertRawAsync(db, tenantId, requestId, stoppedAt: Start, stopReason: "REQUEST_REJECTED"));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task StoppedAtAndStopReason_MustBothBeNullOrBothSet(bool setStoppedAt, bool setReason)
    {
        var (tenantId, requestId, _, _) = await SeedRequestsAsync();

        await AssertConstraintViolationAsync(
            () => WithDbAsync(db => InsertRawAsync(
                db,
                tenantId,
                requestId,
                stoppedAt: setStoppedAt ? Start.AddHours(1) : null,
                stopReason: setReason ? "WO_CLOSED" : null)),
            547,
            "CK_sla_record_stop_pair");
    }

    [Theory]
    [InlineData("REJECTED")]
    [InlineData("WORK_ORDER_CLOSED")]
    [InlineData("")]
    public async Task AStopReasonOutsideTheFourApprovedCodes_IsRejected(string reason)
    {
        var (tenantId, requestId, _, _) = await SeedRequestsAsync();

        var exception = await Assert.ThrowsAsync<SqlException>(() => WithDbAsync(db =>
            InsertRawAsync(db, tenantId, requestId, stoppedAt: Start.AddHours(1), stopReason: reason)));

        Assert.Equal(547, exception.Number);
        Assert.Contains("CK_sla_record_stop_reason", exception.Message);
    }

    [Fact]
    public async Task AStoppedRecord_CannotBeRestartedOrEditedBackToActive_ByThePairCheck()
    {
        var (tenantId, requestId, _, _) = await SeedRequestsAsync();
        await WithDbAsync(db => InsertRawAsync(db, tenantId, requestId, stoppedAt: Start.AddHours(1), stopReason: "WO_CLOSED"));

        // Clearing only one of the pair is rejected by the database; the application has no path that clears both.
        var exception = await Assert.ThrowsAsync<SqlException>(() => WithDbAsync(db =>
            db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [sla_record] SET [stop_reason] = NULL WHERE [repair_request_id] = {requestId}")));

        Assert.Equal(547, exception.Number);
        Assert.Contains("CK_sla_record_stop_pair", exception.Message);
    }

    // ---------------- Migration: schema only, no backfill ----------------

    [Fact]
    public async Task MigrationScript_ContainsNoDataStatementsOtherThanTheMigrationHistoryRow()
    {
        await using var db = _host.CreateScope().ServiceProvider.GetRequiredService<RepairRequestDbContext>();
        var migrationId = db.Database.GetMigrations().Single(id => id.EndsWith("_AddSlaRecord", StringComparison.Ordinal));
        var migrator = db.GetService<IMigrator>();

        var script = migrator.GenerateScript(PreviousMigration, migrationId);

        var statements = script
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith("--", StringComparison.Ordinal))
            .Where(line => !line.Contains("[__EFMigrationsHistory]", StringComparison.Ordinal))
            .ToList();

        Assert.Contains(statements, line => line.StartsWith("CREATE TABLE [sla_record]", StringComparison.Ordinal));
        Assert.Contains(statements, line => line.Contains("ADD CONSTRAINT [AK_repair_request_tenant_id_repair_request_id] UNIQUE", StringComparison.Ordinal));
        Assert.DoesNotContain(statements, line => System.Text.RegularExpressions.Regex.IsMatch(
            line, @"^(INSERT|UPDATE|DELETE|MERGE|TRUNCATE)\b|\bSELECT\b.*\bINTO\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
    }

    [Fact]
    public async Task Migrating_APopulatedDatabase_CreatesNoSlaRowAndChangesNoHistoricalFact()
    {
        var options = new DbContextOptionsBuilder<RepairRequestDbContext>().UseSqlServer(ScratchConnection).Options;
        await using var db = new RepairRequestDbContext(options);
        await db.Database.EnsureDeletedAsync();
        try
        {
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync(PreviousMigration);
            Assert.Null(await db.Database.SqlQuery<int?>($"SELECT OBJECT_ID('sla_record') AS [Value]").SingleAsync());

            // A request that was submitted before the SLA foundation existed.
            var tenantId = Guid.NewGuid();
            var requestId = Guid.NewGuid();
            var submittedAt = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE [repair_request] NOCHECK CONSTRAINT ALL");
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO [repair_request] ([repair_request_id], [tenant_id], [request_no], [status], [created_by], [submitted_by], [submitted_at])
                VALUES ({requestId}, {tenantId}, 'RR-2026-000001', 'SUBMITTED', {Guid.NewGuid()}, {Guid.NewGuid()}, {submittedAt})
                """);
            var auditRowsBefore = await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM [audit_history]").SingleAsync();

            await migrator.MigrateAsync();

            Assert.Equal(0, await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM [sla_record]").SingleAsync());
            Assert.Equal(auditRowsBefore, await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM [audit_history]").SingleAsync());
            var historical = await db.Database
                .SqlQuery<string>($"SELECT [status] + '|' + CONVERT(varchar(30), [submitted_at], 126) AS [Value] FROM [repair_request] WHERE [repair_request_id] = {requestId}")
                .SingleAsync();
            Assert.Equal("SUBMITTED|2026-09-01T08:00:00", historical);
        }
        finally
        {
            await db.Database.EnsureDeletedAsync();
        }
    }
}
