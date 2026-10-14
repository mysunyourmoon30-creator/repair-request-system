using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using RepairRequest.Infrastructure.Persistence;
using RepairRequest.IntegrationTests.Authentication;
using RepairRequest.IntegrationTests.Persistence;

namespace RepairRequest.IntegrationTests.TimeCorrection;

/// <summary>
/// The <c>time_correction</c> schema against LocalDB (`docs/17` P1, P8, P13): exact columns, tenant-consistent keys, the pause target that must
/// belong to the same session, the CHECK constraints that back the domain rules, the two filtered unique indexes that make "one PENDING per
/// logical target" atomic (including the nullable pause), and a schema-only migration that upgrades a populated database without touching it.
/// Every database here is an isolated test database.
/// </summary>
[Collection(PersistenceDatabaseCollection.Name)]
public sealed class TimeCorrectionSchemaTests : IAsyncLifetime
{
    private const string PreviousMigration = "20261011162724_AddWorkEvidence";
    private const string ScratchConnection =
        "Server=(localdb)\\MSSQLLocalDB;Database=RepairRequestDb_TimeCorrectionMigrationTest;Trusted_Connection=True;TrustServerCertificate=True";

    private readonly AuthenticationTestHost _host = new();
    private readonly TimeCorrectionDriver _driver;

    public TimeCorrectionSchemaTests(PersistenceDatabaseFixture database)
    {
        _ = database;
        _driver = new TimeCorrectionDriver(_host);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _host.DisposeAsync();

    // ---------------- structure ----------------

    [Fact]
    public async Task TheTableHasExactlyTheApprovedColumns()
    {
        var columns = await _driver.WithDbAsync(db => db.Database.SqlQuery<string>(
            $"SELECT [COLUMN_NAME] + ':' + [IS_NULLABLE] AS [Value] FROM [INFORMATION_SCHEMA].[COLUMNS] WHERE [TABLE_NAME] = 'time_correction'").ToListAsync());

        Assert.Equal(
            new[]
            {
                "decided_at:YES", "decided_by:YES", "decision_reason:YES", "new_value:NO", "old_value:NO", "reason:NO", "requested_by:NO",
                "row_version:NO", "sla_recalculated:NO", "status:NO", "tenant_id:NO", "time_correction_id:NO", "timestamp_type:NO",
                "work_session_id:NO", "work_session_pause_id:YES"
            },
            columns.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task KeysChecksAndFilteredUniqueIndexes_ExistWithTheApprovedNames()
    {
        var foreignKeys = await _driver.WithDbAsync(db => db.Database.SqlQuery<string>(
            $"SELECT [name] AS [Value] FROM [sys].[foreign_keys] WHERE [parent_object_id] = OBJECT_ID('time_correction')").ToListAsync());
        Assert.Equal(
            new[]
            {
                "FK_time_correction_decided_by_user", "FK_time_correction_requested_by_user", "FK_time_correction_work_session",
                "FK_time_correction_work_session_pause"
            },
            foreignKeys.Order(StringComparer.Ordinal));
        Assert.Equal(0, await _driver.WithDbAsync(db => db.Database.SqlQuery<int>(
            $"SELECT COUNT(*) AS [Value] FROM [sys].[foreign_keys] WHERE [parent_object_id] = OBJECT_ID('time_correction') AND [delete_referential_action] <> 0").SingleAsync()));

        var checks = await _driver.WithDbAsync(db => db.Database.SqlQuery<string>(
            $"SELECT [name] AS [Value] FROM [sys].[check_constraints] WHERE [parent_object_id] = OBJECT_ID('time_correction')").ToListAsync());
        Assert.Equal(
            new[]
            {
                "CK_time_correction_decision_fields", "CK_time_correction_decision_reason_required_on_reject", "CK_time_correction_no_self_decision",
                "CK_time_correction_pause_target", "CK_time_correction_reason_not_blank", "CK_time_correction_sla_not_recalculated",
                "CK_time_correction_status", "CK_time_correction_timestamp_type"
            },
            checks.Order(StringComparer.Ordinal));

        var filtered = await _driver.WithDbAsync(db => db.Database.SqlQuery<string>(
            $"SELECT [name] + ' :: ' + [filter_definition] AS [Value] FROM [sys].[indexes] WHERE [object_id] = OBJECT_ID('time_correction') AND [is_unique] = 1 AND [has_filter] = 1").ToListAsync());
        Assert.Equal(2, filtered.Count);
        Assert.Contains(filtered, text => text.StartsWith("UQ_time_correction_pending_session_type ::", StringComparison.Ordinal) && text.Contains("IS NULL", StringComparison.Ordinal));
        Assert.Contains(filtered, text => text.StartsWith("UQ_time_correction_pending_session_type_pause ::", StringComparison.Ordinal) && text.Contains("IS NOT NULL", StringComparison.Ordinal));

        var alternateKeys = await _driver.WithDbAsync(db => db.Database.SqlQuery<string>(
            $"SELECT [name] AS [Value] FROM [sys].[key_constraints] WHERE [type] = 'UQ' AND [name] IN ('AK_work_session_tenant_id_work_session_id','AK_work_session_pause_tenant_id_work_session_id_pause_id')").ToListAsync());
        Assert.Equal(2, alternateKeys.Count);
    }

    // ---------------- integrity enforced by the database ----------------

    private Task<int> InsertAsync(
        TimeCorrectionWorld world, Guid? tenantId = null, Guid? sessionId = null, Guid? pauseId = null, string type = "CHECK_IN", string status = "PENDING",
        Guid? decidedBy = null, string? decisionReason = null, DateTime? decidedAt = null, bool sla = false, string reason = "Wrong.", Guid? requestedBy = null)
    {
        var tenant = tenantId ?? world.TenantId;
        var session = sessionId ?? world.SessionId;
        var requester = requestedBy ?? world.Base.TeamLead.UserId;
        return _driver.WithDbAsync(db => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO [time_correction] ([time_correction_id], [tenant_id], [work_session_id], [work_session_pause_id], [timestamp_type], [old_value], [new_value],
                [reason], [requested_by], [status], [decided_by], [decision_reason], [decided_at], [sla_recalculated])
            VALUES ({Guid.NewGuid()}, {tenant}, {session}, {pauseId}, {type}, {world.CheckIn}, {world.CheckIn.AddMinutes(-1)}, {reason}, {requester}, {status},
                {decidedBy}, {decisionReason}, {decidedAt}, {sla})
            """));
    }

    /// <summary>The statement is refused with <paramref name="number"/> by one of <paramref name="names"/> (SQL Server names the first violated constraint when several apply).</summary>
    private static async Task AssertViolationAsync(Func<Task<int>> action, int number, params string[] names)
    {
        var exception = await Assert.ThrowsAsync<SqlException>(action);
        Assert.Equal(number, exception.Number);
        Assert.Contains(names, name => exception.Message.Contains(name, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ThePauseTarget_IsRequiredExactlyForPauseTypes_AndMustBelongToTheSameSessionAndTenant()
    {
        var world = await TimeCorrectionWorlds.CreateAsync(_host);
        var other = await TimeCorrectionWorlds.CreateAsync(_host);

        await AssertViolationAsync(() => InsertAsync(world, pauseId: world.Pause1, type: "CHECK_IN"), 547, "CK_time_correction_pause_target");
        await AssertViolationAsync(() => InsertAsync(world, pauseId: null, type: "PAUSE_START"), 547, "CK_time_correction_pause_target");
        await AssertViolationAsync(() => InsertAsync(world, pauseId: null, type: "RESUME"), 547, "CK_time_correction_pause_target");
        await AssertViolationAsync(() => InsertAsync(world, pauseId: other.Pause1, type: "PAUSE_START"), 547, "FK_time_correction_work_session_pause");   // another session's pause
        await AssertViolationAsync(() => InsertAsync(world, tenantId: other.TenantId, type: "CHECK_IN"), 547, "FK_time_correction_work_session", "FK_time_correction_requested_by_user"); // a row of another tenant than its session

        Assert.Equal(1, await InsertAsync(world, pauseId: world.Pause1, type: "PAUSE_START"));
        Assert.Equal(1, await InsertAsync(world, pauseId: null, type: "CHECK_OUT"));
    }

    [Fact]
    public async Task TheDecisionRules_AreBackedByTheDatabase()
    {
        var world = await TimeCorrectionWorlds.CreateAsync(_host);
        var supervisor = world.Base.Supervisor.UserId;
        var decidedAt = DateTime.UtcNow;

        await AssertViolationAsync(() => InsertAsync(world, sla: true), 547, "CK_time_correction_sla_not_recalculated");
        await AssertViolationAsync(() => InsertAsync(world, reason: "   "), 547, "CK_time_correction_reason_not_blank");
        await AssertViolationAsync(() => InsertAsync(world, status: "WHATEVER", decidedBy: supervisor, decidedAt: decidedAt), 547, "CK_time_correction_status");
        await AssertViolationAsync(() => InsertAsync(world, type: "CHECK_SIDEWAYS"), 547, "CK_time_correction_timestamp_type", "CK_time_correction_pause_target");
        // A PENDING request has no decision fields; a decided one must have them.
        await AssertViolationAsync(() => InsertAsync(world, decidedBy: supervisor, decidedAt: decidedAt), 547, "CK_time_correction_decision_fields");
        await AssertViolationAsync(() => InsertAsync(world, status: "APPROVED"), 547, "CK_time_correction_decision_fields");
        // Reject needs a reason.
        await AssertViolationAsync(() => InsertAsync(world, status: "REJECTED", decidedBy: supervisor, decidedAt: decidedAt), 547, "CK_time_correction_decision_reason_required_on_reject");
        // Nobody decides their own request, whatever the application does.
        await AssertViolationAsync(
            () => InsertAsync(world, status: "APPROVED", decidedBy: world.Base.TeamLead.UserId, decidedAt: decidedAt, requestedBy: world.Base.TeamLead.UserId),
            547,
            "CK_time_correction_no_self_decision");

        Assert.Equal(1, await InsertAsync(world, status: "APPROVED", decidedBy: supervisor, decidedAt: decidedAt));
    }

    [Fact]
    public async Task OnePendingPerLogicalTarget_IsAtomic_ForNullableAndPauseTargets_AndAllowsHistory()
    {
        var world = await TimeCorrectionWorlds.CreateAsync(_host);
        var supervisor = world.Base.Supervisor.UserId;

        // Nullable pause target (CHECK_IN): the second PENDING row is a unique violation, which a single composite index would have let through.
        Assert.Equal(1, await InsertAsync(world, type: "CHECK_IN"));
        await AssertViolationAsync(() => InsertAsync(world, type: "CHECK_IN"), 2601, "UQ_time_correction_pending_session_type");
        // Pause target.
        Assert.Equal(1, await InsertAsync(world, pauseId: world.Pause1, type: "PAUSE_START"));
        await AssertViolationAsync(() => InsertAsync(world, pauseId: world.Pause1, type: "PAUSE_START"), 2601, "UQ_time_correction_pending_session_type_pause");
        // Different targets are independent.
        Assert.Equal(1, await InsertAsync(world, pauseId: world.Pause2, type: "PAUSE_START"));
        Assert.Equal(1, await InsertAsync(world, pauseId: world.Pause1, type: "RESUME"));
        Assert.Equal(1, await InsertAsync(world, type: "CHECK_OUT"));

        // Decided rows are history: any number may exist for a target.
        Assert.Equal(1, await InsertAsync(world, type: "CHECK_IN", status: "APPROVED", decidedBy: supervisor, decidedAt: DateTime.UtcNow));
        Assert.Equal(1, await InsertAsync(world, type: "CHECK_IN", status: "REJECTED", decidedBy: supervisor, decidedAt: DateTime.UtcNow, decisionReason: "No."));
    }

    // ---------------- migration ----------------

    private static string Stamp(DateTime value) => value.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task MigrationScript_ContainsNoDataStatementsOtherThanTheMigrationHistoryRow()
    {
        await using var scope = _host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RepairRequestDbContext>();
        var migrationId = db.Database.GetMigrations().Single(id => id.EndsWith("_AddTimeCorrection", StringComparison.Ordinal));
        Assert.True(string.CompareOrdinal(migrationId, PreviousMigration) > 0, "The Time Correction migration must come after the latest migration of main.");
        Assert.Equal(migrationId, db.Database.GetMigrations().Last());

        var script = db.GetService<IMigrator>().GenerateScript(PreviousMigration, migrationId);

        var statements = script
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith("--", StringComparison.Ordinal))
            .Where(line => !line.Contains("[__EFMigrationsHistory]", StringComparison.Ordinal))
            .ToList();

        Assert.Contains(statements, line => line.StartsWith("CREATE TABLE [time_correction]", StringComparison.Ordinal));
        Assert.DoesNotContain(statements, line => System.Text.RegularExpressions.Regex.IsMatch(
            line, @"^(INSERT|UPDATE|DELETE|MERGE|TRUNCATE)\b|\bSELECT\b.*\bINTO\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
    }

    [Fact]
    public async Task UpgradingAPopulatedDatabaseFromTheCurrentMainSchema_CreatesNoCorrection_AndChangesNoSessionOrPause()
    {
        var options = new DbContextOptionsBuilder<RepairRequestDbContext>().UseSqlServer(ScratchConnection).Options;
        await using var db = new RepairRequestDbContext(options);
        await db.Database.EnsureDeletedAsync();
        try
        {
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync(PreviousMigration);
            Assert.Null(await db.Database.SqlQuery<int?>($"SELECT OBJECT_ID('time_correction') AS [Value]").SingleAsync());

            // Historic Work Session and pause rows, written without their parents (the foreign keys are not what this test is about).
            var sessionId = Guid.NewGuid();
            var pauseId = Guid.NewGuid();
            var tenantId = Guid.NewGuid();
            var checkIn = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE [work_session] NOCHECK CONSTRAINT ALL");
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE [work_session_pause] NOCHECK CONSTRAINT ALL");
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO [work_session] ([work_session_id], [tenant_id], [service_visit_id], [technician_id], [status], [check_in_at], [check_out_at])
                VALUES ({sessionId}, {tenantId}, {Guid.NewGuid()}, {Guid.NewGuid()}, 'CHECKED_OUT', {checkIn}, {checkIn.AddHours(2)})
                """);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO [work_session_pause] ([work_session_pause_id], [tenant_id], [work_session_id], [paused_at], [pause_reason], [resumed_at])
                VALUES ({pauseId}, {tenantId}, {sessionId}, {checkIn.AddMinutes(30)}, 'Waiting', {checkIn.AddMinutes(45)})
                """);
            var auditRowsBefore = await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM [audit_history]").SingleAsync();

            await migrator.MigrateAsync();

            Assert.Equal(0, await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM [time_correction]").SingleAsync());
            Assert.Equal(auditRowsBefore, await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM [audit_history]").SingleAsync());
            Assert.Equal(
                $"CHECKED_OUT|{Stamp(checkIn.AddHours(2))}",
                await db.Database.SqlQuery<string>($"SELECT [status] + '|' + FORMAT([check_out_at], 'yyyy-MM-dd HH:mm') AS [Value] FROM [work_session] WHERE [work_session_id] = {sessionId}").SingleAsync());
            Assert.Equal(
                $"{Stamp(checkIn.AddMinutes(30))}|{Stamp(checkIn.AddMinutes(45))}",
                await db.Database.SqlQuery<string>($"SELECT FORMAT([paused_at], 'yyyy-MM-dd HH:mm') + '|' + FORMAT([resumed_at], 'yyyy-MM-dd HH:mm') AS [Value] FROM [work_session_pause] WHERE [work_session_pause_id] = {pauseId}").SingleAsync());
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        }
        finally
        {
            await db.Database.EnsureDeletedAsync();
        }
    }
}
