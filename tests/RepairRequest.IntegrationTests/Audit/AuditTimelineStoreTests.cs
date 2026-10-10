using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RepairRequest.Application.Audit;
using RepairRequest.IntegrationTests.Audit.Support;

namespace RepairRequest.IntegrationTests.Audit;

/// <summary>
/// The production <see cref="IAuditTimelineStore"/> against real SQL Server (UC-WO-002 / AUD-API-001; docs/15 §5, §8.1,
/// §11, §12) — the Phase 1B gate. The ordering oracle is SQL Server itself (an un-paged <c>ORDER BY</c> executed by the
/// database); .NET <c>Guid</c> ordering is used only to assert the premise that the two orders differ, never to produce an
/// expected value. Nothing here sorts, compares Guids or pages in memory.
/// </summary>
[Collection(AuditTimelineCollection.Name)]
public sealed class AuditTimelineStoreTests(AuditTimelineDatabaseFixture fixture)
{
    public static IEnumerable<object[]> CompositeScenarios() =>
        new[] { "WO20", "WO200", "WO2000", "MANYIDS", "BIGIDS" }.Select(s => new object[] { s });

    public static IEnumerable<object[]> Ks1PageSizes() =>
        new[] { 1, 7, 20, 50, 100, 149, 150, 151 }.Select(s => new object[] { s });

    public static IEnumerable<object[]> MixPageSizes() =>
        new[] { 1, 2, 3, 7, 20, 25, 26, 27, 30, 54, 81, 161, 162, 163 }.Select(s => new object[] { s });

    private sealed class OracleRow
    {
        public Guid AuditId { get; set; }

        public DateTime OccurredAt { get; set; }
    }

    // ------------------------------------------------------------------ helpers

    private static string Ids(IEnumerable<Guid> ids) => ids.Any() ? string.Join(",", ids.Select(g => $"'{g}'")) : "NULL";

    /// <summary>Independent of the store: one un-paged statement, SQL Server's own ORDER BY.</summary>
    private async Task<List<OracleRow>> OracleAsync(AuditTimelineSources s)
    {
        await using var db = fixture.CreateContext();
        var sql = $@"SELECT a.audit_history_id AS AuditId, a.occurred_at AS OccurredAt
FROM audit_history a
WHERE a.tenant_id = '{s.TenantId}' AND (
     (a.entity_type = 'WORK_ORDER' AND a.entity_id = '{s.WorkOrderId}')
  OR (a.entity_type = 'SERVICE_VISIT' AND a.entity_id IN ({Ids(s.VisitIds)}))
  OR (a.entity_type = 'WORK_SESSION' AND a.entity_id IN ({Ids(s.SessionIds)}))
  OR (a.entity_type = 'CORRECTIVE_ACTION' AND a.entity_id IN ({Ids(s.CorrectiveActionIds)}))
  OR (a.entity_type = 'REPAIR_REQUEST' AND a.entity_id = '{s.RepairRequestId}' AND a.action_code = 'REPAIR_REQUEST_CONVERTED'))
ORDER BY a.occurred_at DESC, a.audit_history_id DESC";
        return await db.Database.SqlQueryRaw<OracleRow>(sql).ToListAsync();
    }

    private static async Task<bool> SqlLessThanAsync(Guid a, Guid b)
    {
        await using var connection = new SqlConnection(AuditTimelineDatabaseFixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CASE WHEN @a < @b THEN 1 ELSE 0 END";
        command.Parameters.Add(new SqlParameter("@a", SqlDbType.UniqueIdentifier) { Value = a });
        command.Parameters.Add(new SqlParameter("@b", SqlDbType.UniqueIdentifier) { Value = b });
        return (int)(await command.ExecuteScalarAsync())! == 1;
    }

    private async Task<(IReadOnlyList<AuditTimelineRow> Rows, CapturedCommand Command)> ReadCapturedAsync(
        AuditTimelineSources sources, TimelinePosition? after, int take)
    {
        await using var scope = fixture.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IAuditTimelineStore>();
        fixture.Capture.Commands.Clear();
        var rows = await store.ReadPageAsync(sources, after, take, CancellationToken.None);
        return (rows, fixture.Capture.Commands.Last());
    }

    private async Task<List<TimelineWalker.Page>> WalkAsync(AuditTimelineSources sources, int pageSize, TimelinePosition? start = null)
    {
        await using var scope = fixture.CreateScope();
        return await TimelineWalker.WalkAsync(scope.ServiceProvider.GetRequiredService<IAuditTimelineStore>(), sources, pageSize, start);
    }

    private static TimelinePosition PositionOf(OracleRow row) => new(row.OccurredAt, row.AuditId);

    // ------------------------------------------------------------------ environment / OPENJSON

    [Fact]
    public async Task Database_IsSqlServer_WithACompatibilityLevelThatSupportsOpenJson()
    {
        await using var db = fixture.CreateContext();
        Assert.Contains("SqlServer", db.Database.ProviderName);

        // OPENJSON needs compatibility level >= 130 (the level on this instance is recorded in docs/15 §12).
        Assert.True(fixture.CompatibilityLevel >= 130, $"compatibility level {fixture.CompatibilityLevel} does not support OPENJSON");

        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        await using var connection = new SqlConnection(AuditTimelineDatabaseFixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM OPENJSON(@ids) WITH (Id uniqueidentifier '$')";
        command.Parameters.Add(new SqlParameter("@ids", SqlDbType.NVarChar, -1) { Value = System.Text.Json.JsonSerializer.Serialize(ids) });
        Assert.Equal(3, (int)(await command.ExecuteScalarAsync())!);
    }

    // ------------------------------------------------------------------ 1. uniqueidentifier ordering

    [Fact]
    public async Task SqlServerGuidOrdering_DiffersFromDotNetGuidOrdering()
    {
        var a = new Guid("ffffffff-0000-0000-0000-000000000001");
        var b = new Guid("00000000-0000-0000-0000-ffffffffffff");

        Assert.True(await SqlLessThanAsync(a, b));   // SQL Server compares the trailing six bytes first ...
        Assert.True(a.CompareTo(b) > 0);             // ... .NET compares the leading int first.

        var oracle = await OracleAsync(fixture.Scenarios["KS1"].Sources);
        var sqlOrder = oracle.Select(r => r.AuditId).ToList();
        Assert.NotEqual(sqlOrder.OrderByDescending(g => g).ToList(), sqlOrder); // premise only — never an expected value
    }

    // ------------------------------------------------------------------ 2. server-side keyset / 3. two-step shape / parameter count

    [Fact]
    public async Task KeysetPredicate_IsExecutedByTheDatabase_AndPageEqualsTheOracleSlice()
    {
        var scenario = fixture.Scenarios["MIX"];
        var oracle = await OracleAsync(scenario.Sources);
        var (rows, command) = await ReadCapturedAsync(scenario.Sources, PositionOf(oracle[40]), 21);

        // Both branches of the UNION ALL carry the keyset predicate, executed by SQL Server.
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(command.Sql, @"\[a0?\]\.\[occurred_at\]\s*<=\s*@").Count);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(command.Sql, @"\[a0?\]\.\[occurred_at\]\s*<\s*@").Count);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(command.Sql, @"\[a0?\]\.\[occurred_at\]\s*=\s*@").Count);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(command.Sql, @"\[a0?\]\.\[audit_history_id\]\s*<\s*@").Count);
        Assert.Matches(@"ORDER BY \[\w+\]\.\[OccurredAt\] DESC, \[\w+\]\.\[Id\] DESC", command.Sql);
        Assert.Equal(oracle.Skip(41).Take(21).Select(r => r.AuditId), rows.Select(r => r.AuditId));
    }

    [Fact]
    public async Task QueryShape_IsTwoStep_IndexOnlyCandidatesThenJoinByPrimaryKey()
    {
        var scenario = fixture.Scenarios["WO200"];
        var (_, command) = await ReadCapturedAsync(scenario.Sources, null, 21);
        var sql = command.Sql;

        // Step 1: TOP over index-only columns (Id, OccurredAt) of a UNION ALL of the main source predicate and the Repair Request
        // converted branch, ordered; step 2: joined back to the table by primary key; the outer order is repeated.
        Assert.Matches(@"FROM \(\s*SELECT TOP\(@\w+\) \[u\]\.\[Id\], \[u\]\.\[OccurredAt\]\s+FROM \(", sql);
        Assert.Contains("UNION ALL", sql, StringComparison.Ordinal);
        Assert.Matches(@"ORDER BY \[u\]\.\[OccurredAt\] DESC, \[u\]\.\[Id\] DESC\s*\) AS \[\w+\]\s+INNER JOIN \[audit_history\] AS \[\w+\] ON \[\w+\]\.\[Id\] = \[\w+\]\.\[audit_history_id\]", sql);
        Assert.Matches(@"ORDER BY \[\w+\]\.\[OccurredAt\] DESC, \[\w+\]\.\[Id\] DESC\s*$", sql.Trim());

        // The payload column is read only in the outer select (before the first FROM), and only for allowlisted action codes;
        // the candidate step selects nothing but the id and the timestamp.
        var firstFrom = sql.IndexOf("FROM (", StringComparison.Ordinal);
        Assert.True(firstFrom > 0);
        Assert.Contains("CASE", sql[..firstFrom], StringComparison.Ordinal);
        Assert.Contains("new_value_json", sql[..firstFrom], StringComparison.Ordinal);
        Assert.DoesNotContain("new_value_json", sql[firstFrom..], StringComparison.Ordinal);
        Assert.DoesNotContain("old_value_json", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("correlation_id", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("[reason]", sql, StringComparison.Ordinal);

        // The main OR references index columns only: action_code appears solely in the separate Repair Request branch.
        var unionAt = sql.IndexOf("UNION ALL", StringComparison.Ordinal);
        Assert.DoesNotContain("action_code", sql[firstFrom..unionAt], StringComparison.Ordinal);

        // Entity types are server constants in the SQL text, not parameters.
        foreach (var type in new[] { "WORK_ORDER", "SERVICE_VISIT", "WORK_SESSION", "CORRECTIVE_ACTION", "REPAIR_REQUEST" })
        {
            Assert.Contains($"'{type}'", sql[firstFrom..], StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ParameterCount_DoesNotGrowWithTheNumberOfChildIds_AndUsesOpenJson()
    {
        var counts = new List<int>();
        foreach (var name in new[] { "WO20", "MANYIDS", "BIGIDS" })
        {
            var scenario = fixture.Scenarios[name];
            var children = scenario.Sources.VisitIds.Count + scenario.Sources.SessionIds.Count + scenario.Sources.CorrectiveActionIds.Count;
            var (_, command) = await ReadCapturedAsync(scenario.Sources, null, 21);

            Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(command.Sql, "OPENJSON").Count);
            Assert.Matches(@"WITH \(\[value\] uniqueidentifier '\$'\)", command.Sql);
            Assert.DoesNotContain(command.Parameters, p => System.Text.RegularExpressions.Regex.IsMatch(p.ParameterName, @"^@(visits|sessions|actions)\d+$"));
            Assert.True(command.Parameters.Count <= 12, $"{name}: {command.Parameters.Count} parameters for {children} child ids");
            counts.Add(command.Parameters.Count);

            if (name == "BIGIDS")
            {
                Assert.True(children > 2100, $"BIGIDS must exceed 2,100 child ids, has {children}");
            }
        }

        Assert.Single(counts.Distinct()); // identical for 7, 232 and > 2,100 child ids
    }

    // ------------------------------------------------------------------ 4. composite source set (incl. > 2,100 child ids)

    [Theory]
    [MemberData(nameof(CompositeScenarios))]
    public async Task CompositeSourceSet_ReturnsExactlyTheSeededEvents_AndNoDecoys(string name)
    {
        var scenario = fixture.Scenarios[name];
        var oracle = await OracleAsync(scenario.Sources);

        var pages = await WalkAsync(scenario.Sources, 100);
        var rows = pages.SelectMany(p => p.Items).ToList();
        var ids = rows.Select(r => r.AuditId).ToList();

        Assert.Equal(scenario.TotalExpected, ids.Count);
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.Empty(scenario.ExpectedAuditIds.Except(ids));
        Assert.Empty(ids.Intersect(scenario.DecoyAuditIds));
        Assert.Equal(oracle.Select(r => r.AuditId), ids);

        var types = rows.Select(r => r.EntityType).ToHashSet();
        Assert.Contains("WORK_ORDER", types);
        Assert.Contains("SERVICE_VISIT", types);
        Assert.Contains("WORK_SESSION", types);
        Assert.Contains("CORRECTIVE_ACTION", types);
        Assert.Contains("REPAIR_REQUEST", types);
        Assert.All(rows.Where(r => r.EntityType == "REPAIR_REQUEST"), r => Assert.Equal("REPAIR_REQUEST_CONVERTED", r.ActionCode));
        Assert.Equal(scenario.Sources.VisitIds.Count, rows.Where(r => r.EntityType == "SERVICE_VISIT").Select(r => r.EntityId).Distinct().Count());
        Assert.Equal(scenario.Sources.SessionIds.Count, rows.Where(r => r.EntityType == "WORK_SESSION").Select(r => r.EntityId).Distinct().Count());
    }

    // ------------------------------------------------------------------ 5. KS-1 equal timestamp

    [Theory]
    [MemberData(nameof(Ks1PageSizes))]
    public async Task KS1_150RowsWithOneTimestamp_ExactlyOnce_InSqlServerGuidOrder(int pageSize)
    {
        var scenario = fixture.Scenarios["KS1"];
        var oracle = await OracleAsync(scenario.Sources);
        Assert.Equal(150, oracle.Count);
        Assert.Single(oracle.Select(r => r.OccurredAt).Distinct());

        var pages = await WalkAsync(scenario.Sources, pageSize);
        var ids = pages.SelectMany(p => p.Items).Select(r => r.AuditId).ToList();

        Assert.Equal(150, ids.Count);
        Assert.Equal(150, ids.Distinct().Count());                          // no duplicate
        Assert.Empty(scenario.ExpectedAuditIds.Except(ids));                // no missing
        Assert.Equal(oracle.Select(r => r.AuditId), ids);                   // SQL Server uniqueidentifier DESC order
        Assert.Equal((int)Math.Ceiling(150d / pageSize), pages.Count);
        Assert.All(pages.Take(pages.Count - 1), p => { Assert.Equal(pageSize, p.Items.Count); Assert.True(p.HasMore); Assert.NotNull(p.Next); });
        Assert.False(pages[^1].HasMore);
        Assert.Null(pages[^1].Next);
    }

    // ------------------------------------------------------------------ 6. mixed timestamps and cursor boundaries

    [Theory]
    [MemberData(nameof(MixPageSizes))]
    public async Task MixedTimestamps_TieGroups_ExactlyOnce_InOrder(int pageSize)
    {
        var scenario = fixture.Scenarios["MIX"];
        var oracle = await OracleAsync(scenario.Sources);
        Assert.Equal(162, oracle.Count);

        var pages = await WalkAsync(scenario.Sources, pageSize);
        var rows = pages.SelectMany(p => p.Items).ToList();

        Assert.Equal(oracle.Select(r => r.AuditId), rows.Select(r => r.AuditId));
        for (var i = 1; i < rows.Count; i++)
        {
            Assert.True(rows[i].OccurredAt <= rows[i - 1].OccurredAt);
        }

        Assert.Equal(162 % pageSize == 0, pages[^1].Items.Count == pageSize);
        Assert.False(pages[^1].HasMore);
        Assert.Null(pages[^1].Next);
    }

    [Fact]
    public async Task MixedTimestamps_PageSizesCoverMidGroupEndOfGroupAndExactBoundaries()
    {
        var oracle = await OracleAsync(fixture.Scenarios["MIX"].Sources);
        var sizes = new[] { 1, 2, 3, 7, 20, 25, 26, 27, 30, 54, 81, 161, 162, 163 };
        int mid = 0, end = 0;
        foreach (var size in sizes)
        {
            for (var k = size; k < oracle.Count; k += size)
            {
                if (oracle[k - 1].OccurredAt == oracle[k].OccurredAt) { mid++; } else { end++; }
            }
        }

        Assert.True(mid > 0, "no page boundary fell inside a tie group");
        Assert.True(end > 0, "no page boundary fell at the end of a tie group");
        Assert.Contains(sizes, s => 162 % s == 0 && s < 162);
    }

    [Fact]
    public async Task Cursor_AtEveryTieGroupPosition_ContinuesExactlyAfterTheCursor()
    {
        var scenario = fixture.Scenarios["MIX"];
        var oracle = await OracleAsync(scenario.Sources);
        await using var scope = fixture.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IAuditTimelineStore>();

        var groupStart = oracle.FindIndex(r => r.OccurredAt == oracle[40].OccurredAt);
        var groupSize = oracle.Count(r => r.OccurredAt == oracle[40].OccurredAt);
        Assert.Equal(60, groupSize);
        var groupEnd = groupStart + groupSize - 1;

        foreach (var position in new[] { groupStart, groupStart + 29, groupEnd - 1, groupEnd, groupStart - 1, oracle.Count - 2, oracle.Count - 1 })
        {
            var rows = await store.ReadPageAsync(scenario.Sources, PositionOf(oracle[position]), 25, CancellationToken.None);
            Assert.Equal(oracle.Skip(position + 1).Take(25).Select(r => r.AuditId), rows.Select(r => r.AuditId));
        }
    }

    // ------------------------------------------------------------------ 7. appended events / late commit

    [Fact]
    public async Task EventsAppendedBetweenPages_NeverDuplicateOrShiftDeliveredRows_AndLateCommitLimitationIsExact()
    {
        var scenario = await fixture.SeedAsync(new ScenarioSpec("APP", 10, 2, 10, 2, 10, 1, 10, false, Groups: [10, 10, 10, 10, 10, 10]));
        var s = scenario.Sources;
        await using var scope = fixture.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IAuditTimelineStore>();

        var delivered = new List<AuditTimelineRow>();
        TimelinePosition? cursor = null;
        for (var page = 0; page < 2; page++)
        {
            var rows = await store.ReadPageAsync(s, cursor, 8, CancellationToken.None);
            var items = rows.Take(7).ToList();
            delivered.AddRange(items);
            cursor = new TimelinePosition(items[^1].OccurredAt, items[^1].AuditId);
        }

        Assert.Equal(14, delivered.Count);
        var cursorRow = delivered[^1];
        var firstTs = delivered[0].OccurredAt;
        var olderTs = (await OracleAsync(s)).Last().OccurredAt.AddMilliseconds(-5);

        // Which side of the cursor an id falls on is decided by SQL Server, never in memory.
        Guid smaller = default, greater = default;
        while (smaller == default || greater == default)
        {
            var candidate = Guid.NewGuid();
            if (await SqlLessThanAsync(candidate, cursorRow.AuditId)) { smaller = smaller == default ? candidate : smaller; }
            else { greater = greater == default ? candidate : greater; }
        }

        var wo = s.WorkOrderId;
        var above = new List<Guid>
        {
            await fixture.InsertAsync(s.TenantId, "WORK_ORDER", wo, "WORK_ORDER_STARTED", cursorRow.OccurredAt.AddDays(100)), // newer than everything
            await fixture.InsertAsync(s.TenantId, "WORK_ORDER", wo, "WORK_ORDER_STARTED", cursorRow.OccurredAt, greater),     // same ts, sorts before the cursor
            await fixture.InsertAsync(s.TenantId, "WORK_ORDER", wo, "WORK_ORDER_STARTED", firstTs),                           // an already-passed group
        };
        var below = new List<Guid>
        {
            await fixture.InsertAsync(s.TenantId, "WORK_ORDER", wo, "WORK_ORDER_STARTED", cursorRow.OccurredAt, smaller),     // same ts, sorts after the cursor
            await fixture.InsertAsync(s.TenantId, "WORK_ORDER", wo, "WORK_ORDER_STARTED", olderTs),                           // older than everything
        };

        var continued = await TimelineWalker.WalkAsync(store, s, 7, cursor);
        var continuedIds = continued.SelectMany(p => p.Items).Select(r => r.AuditId).ToList();

        Assert.Empty(continuedIds.Intersect(delivered.Select(r => r.AuditId)));   // nothing delivered reappears or shifts
        Assert.Equal(continuedIds.Count, continuedIds.Distinct().Count());
        Assert.Empty(continuedIds.Intersect(above));                              // documented limitation: not delivered in this session
        Assert.Empty(below.Except(continuedIds));                                 // delivered exactly once

        var oracle = await OracleAsync(s);
        var position = oracle.FindIndex(r => r.AuditId == cursorRow.AuditId);
        Assert.Equal(oracle.Skip(position + 1).Select(r => r.AuditId), continuedIds);

        var fresh = (await TimelineWalker.WalkAsync(store, s, 20)).SelectMany(p => p.Items).Select(r => r.AuditId).ToList();
        Assert.Equal(oracle.Select(r => r.AuditId), fresh);                       // a refresh sees every row
        Assert.Empty(above.Except(fresh));
    }

    // ------------------------------------------------------------------ 8. actual plan of the production query

    [Theory]
    [InlineData("WO2000", false)]
    [InlineData("WO2000", true)]
    [InlineData("MANYIDS", false)]
    [InlineData("BIGIDS", false)]
    [InlineData("BIGIDS", true)]
    public async Task ActualPlan_HasNoAuditHistoryScan_AndPayloadLookupsAreLimitedToThePageRows(string name, bool deepPage)
    {
        var scenario = fixture.Scenarios[name];
        const int take = 21;
        var oracle = await OracleAsync(scenario.Sources);
        TimelinePosition? after = deepPage ? PositionOf(oracle[(int)(oracle.Count * 0.8)]) : null;

        var (rows, command) = await ReadCapturedAsync(scenario.Sources, after, take);
        var measurement = await SqlPlanEvidence.MeasureAsync(AuditTimelineDatabaseFixture.ConnectionString, command.Sql, command.Parameters);

        Assert.Equal(rows.Count, measurement.RowsReturned);
        Assert.Equal(command.Parameters.Count, measurement.ParameterCount);

        // No scan of audit_history (neither the clustered PK nor the timeline index).
        Assert.DoesNotContain(measurement.Operators, o => o.PhysicalOp.Contains("Scan", StringComparison.Ordinal) && o.Index is not null && o.Index.Contains("audit_history", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(measurement.Operators, o => o.PhysicalOp == "Index Seek" && o.Index == "IX_audit_history_timeline");

        // No sort spill at up to 2,000 events / 232 child ids. At 2,400 child ids the sort of the candidate rows spills a few
        // pages (measured: 9 pages = 72 KB written to tempdb, 19 ms warm) because SQL Server estimates 50 rows for an OPENJSON
        // source; that is recorded in docs/15 §12 and bounded here rather than hidden.
        if (name != "BIGIDS")
        {
            Assert.False(measurement.HasSpill);
        }
        else
        {
            Assert.True(measurement.SpillWritesToTempDb <= 100, $"BIGIDS spilled {measurement.SpillWritesToTempDb} pages to tempdb");
        }

        // Payload/key lookups: only the page's rows are looked up by the clustered primary key.
        var lookups = measurement.Operators.Where(o => o.PhysicalOp == "Clustered Index Seek" && o.Index == "PK_audit_history").ToList();
        Assert.NotEmpty(lookups);
        Assert.All(lookups, o => Assert.True(o.Executions <= take + 5, $"{name}: {o.Executions} primary-key lookups for a page of {take}"));
        Assert.All(lookups, o => Assert.True(o.ActualRows <= take + 5));
    }
}
