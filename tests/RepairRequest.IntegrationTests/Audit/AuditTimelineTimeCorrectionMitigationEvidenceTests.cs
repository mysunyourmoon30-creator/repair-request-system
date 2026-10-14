using System.Diagnostics;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RepairRequest.Application.Audit;
using RepairRequest.Infrastructure.Persistence;
using RepairRequest.IntegrationTests.Audit.Support;

namespace RepairRequest.IntegrationTests.Audit;

/// <summary>
/// OPT-IN evidence run (<c>RUN_TIMELINE_EVIDENCE=1</c>): the Time Correction page query BEFORE and AFTER the relational mitigation, on the SAME
/// seeded database in the SAME run (so data, statistics and caches are identical for both). "After" is the production store; "before" is a copy of the
/// pre-mitigation query of commit <c>7a6f2d0</c> (the correction ids probed per audit row through an <c>OPENJSON</c> id set), kept here only to compare.
/// Scenarios: <c>TC</c> (120 corrections on the target Work Order, 120 unrelated), <c>TCV</c> (volume driver: 30 on the target, 6,000 unrelated corrections of
/// the tenant) and <c>TCT</c> (target driver: 600 on the target, 120 unrelated). Every page of both variants is checked against the independent relational oracle.
/// The figures are evidence, never a performance claim, and say nothing about sizes that were not measured.
/// </summary>
[Collection(AuditTimelineCollection.Name)]
public sealed class AuditTimelineTimeCorrectionMitigationEvidenceTests(AuditTimelineDatabaseFixture fixture)
{
    private sealed class OracleRow
    {
        public Guid AuditId { get; set; }

        public DateTime OccurredAt { get; set; }
    }

    private sealed record Run(
        string Variant, string Page, int Rows, long AuditReads, string OtherTables, long TvfExecutions, long TvfRows, double ServerMs, double ClientMs, bool MatchesOracle, string Operators);

    private static async Task<List<OracleRow>> OracleAsync(RepairRequestDbContext db, AuditTimelineSources s)
    {
        static string Ids(IEnumerable<Guid> ids) => ids.Any() ? string.Join(",", ids.Select(g => $"'{g}'")) : "NULL";
        var sql = $@"SELECT a.audit_history_id AS AuditId, a.occurred_at AS OccurredAt FROM audit_history a
WHERE a.tenant_id = '{s.TenantId}' AND (
     (a.entity_type = 'WORK_ORDER' AND a.entity_id = '{s.WorkOrderId}')
  OR (a.entity_type = 'SERVICE_VISIT' AND a.entity_id IN ({Ids(s.VisitIds)}))
  OR (a.entity_type = 'WORK_SESSION' AND a.entity_id IN ({Ids(s.SessionIds)}))
  OR (a.entity_type = 'CORRECTIVE_ACTION' AND a.entity_id IN ({Ids(s.CorrectiveActionIds)}))
  OR (a.entity_type = 'TIME_CORRECTION' AND a.entity_id IN (
        SELECT c.time_correction_id FROM time_correction c
        JOIN work_session ws ON ws.work_session_id = c.work_session_id AND ws.tenant_id = c.tenant_id
        JOIN service_visit sv ON sv.service_visit_id = ws.service_visit_id AND sv.tenant_id = c.tenant_id
        WHERE c.tenant_id = '{s.TenantId}' AND sv.work_order_id = '{s.WorkOrderId}'))
  OR (a.entity_type = 'REPAIR_REQUEST' AND a.entity_id = '{s.RepairRequestId}' AND a.action_code = 'REPAIR_REQUEST_CONVERTED'))
ORDER BY a.occurred_at DESC, a.audit_history_id DESC";
        return await db.Database.SqlQueryRaw<OracleRow>(sql).ToListAsync();
    }

    /// <summary>The pre-mitigation page query (commit 7a6f2d0): the Time Correction ids are one more OPENJSON id set inside the main OR.</summary>
    private static async Task<List<AuditTimelineRow>> BeforePageAsync(
        RepairRequestDbContext db, AuditTimelineSources sources, IReadOnlyList<Guid> correctionIds, TimelinePosition? after, int take)
    {
        var tenantId = sources.TenantId;
        var workOrderId = sources.WorkOrderId;
        var repairRequestId = sources.RepairRequestId;
        var visitIds = sources.VisitIds.ToList();
        var sessionIds = sources.SessionIds.ToList();
        var correctiveActionIds = sources.CorrectiveActionIds.ToList();
        var timeCorrectionIds = correctionIds.ToList();
        var codesWithDetails = AuditTimelineDetails.CodesWithDetails;

        var main = db.AuditHistory.AsNoTracking().Where(audit =>
            audit.TenantId == tenantId
            && ((audit.EntityType == "WORK_ORDER" && audit.EntityId == workOrderId)
                || (audit.EntityType == "SERVICE_VISIT" && EF.Parameter(visitIds).Contains(audit.EntityId))
                || (audit.EntityType == "WORK_SESSION" && EF.Parameter(sessionIds).Contains(audit.EntityId))
                || (audit.EntityType == "CORRECTIVE_ACTION" && EF.Parameter(correctiveActionIds).Contains(audit.EntityId))
                || (audit.EntityType == "TIME_CORRECTION" && EF.Parameter(timeCorrectionIds).Contains(audit.EntityId))));
        var converted = db.AuditHistory.AsNoTracking().Where(audit =>
            audit.TenantId == tenantId && audit.EntityType == "REPAIR_REQUEST" && audit.EntityId == repairRequestId && audit.ActionCode == "REPAIR_REQUEST_CONVERTED");

        if (after is { } position)
        {
            var occurredAt = position.OccurredAt;
            var auditId = position.AuditId;
            main = main.Where(audit => audit.OccurredAt <= occurredAt && (audit.OccurredAt < occurredAt || (audit.OccurredAt == occurredAt && audit.Id.CompareTo(auditId) < 0)));
            converted = converted.Where(audit => audit.OccurredAt <= occurredAt && (audit.OccurredAt < occurredAt || (audit.OccurredAt == occurredAt && audit.Id.CompareTo(auditId) < 0)));
        }

        var page = main.Select(audit => new { audit.Id, audit.OccurredAt })
            .Concat(converted.Select(audit => new { audit.Id, audit.OccurredAt }))
            .OrderByDescending(candidate => candidate.OccurredAt).ThenByDescending(candidate => candidate.Id)
            .Take(take);

        return await (
                from candidate in page
                join audit in db.AuditHistory.AsNoTracking() on candidate.Id equals audit.Id
                orderby candidate.OccurredAt descending, candidate.Id descending
                select new AuditTimelineRow(
                    audit.Id, audit.OccurredAt, audit.EntityType, audit.EntityId, audit.ActionCode, audit.FromState, audit.ToState, audit.ActorId,
                    codesWithDetails.Contains(audit.ActionCode) ? audit.NewValueJson : null))
            .ToListAsync();
    }

    /// <summary>The pre-mitigation correction lookup (one extra statement per request): correction -> Work Session -> Visit -> this Work Order.</summary>
    private static Task<List<Guid>> BeforeLookupAsync(RepairRequestDbContext db, Guid tenantId, Guid workOrderId) => (
            from correction in db.TimeCorrections.AsNoTracking()
            join session in db.WorkSessions.AsNoTracking() on correction.WorkSessionId equals session.Id
            join visit in db.ServiceVisits.AsNoTracking() on session.ServiceVisitId equals visit.Id
            where correction.TenantId == tenantId && session.TenantId == tenantId && visit.TenantId == tenantId && visit.WorkOrderId == workOrderId
            select correction.Id)
        .ToListAsync();

    private static async Task<double> MedianAsync(Func<Task> action, int runs = 15)
    {
        for (var i = 0; i < 2; i++)
        {
            await action();
        }

        var samples = new List<double>();
        for (var i = 0; i < runs; i++)
        {
            var started = Stopwatch.GetTimestamp();
            await action();
            samples.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        samples.Sort();
        return samples[samples.Count / 2];
    }

    private async Task<Run> MeasureAsync(
        string variant, string page, Scenario scenario, TimelinePosition? after, List<OracleRow> oracle, int position,
        Func<RepairRequestDbContext, Task<List<AuditTimelineRow>>> read)
    {
        await using var scope = fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RepairRequestDbContext>();

        fixture.Capture.Commands.Clear();
        var rows = await read(db);
        var command = fixture.Capture.Commands.Last(item => item.Sql.Contains("[audit_history]", StringComparison.Ordinal));
        var expected = oracle.Skip(position + 1).Take(21).Select(row => row.AuditId).ToList();

        var measurement = await SqlPlanEvidence.MeasureAsync(AuditTimelineDatabaseFixture.ConnectionString, command.Sql, command.Parameters);
        Assert.Equal(rows.Count, measurement.RowsReturned);
        var tvf = measurement.Operators.Where(item => item.PhysicalOp == "Table-valued function").ToList();
        var serverMs = await SqlPlanEvidence.MedianMillisecondsAsync(AuditTimelineDatabaseFixture.ConnectionString, command.Sql, command.Parameters);
        var clientMs = await MedianAsync(async () =>
        {
            await using var inner = fixture.CreateScope();
            await read(inner.ServiceProvider.GetRequiredService<RepairRequestDbContext>());
        });

        var match = expected.SequenceEqual(rows.Select(row => row.AuditId));
        Assert.True(match, $"{scenario.Spec.Name}/{variant}/{page}: the page differs from the oracle slice.");
        return new Run(variant, page, rows.Count, measurement.LogicalReads, measurement.OtherTables, tvf.Sum(item => item.Executions), tvf.Sum(item => item.ActualRows), serverMs, clientMs, match, measurement.OperatorSummary);
    }

    [EvidenceFact]
    public async Task CollectBeforeAfterEvidence()
    {
        var md = new StringBuilder();
        md.AppendLine("# Time Correction page query — before / after the relational mitigation");
        md.AppendLine();
        md.AppendLine($"Noise rows: {fixture.NoiseRows:N0}; compatibility level: {fixture.CompatibilityLevel}. Same seeded database and same run for both variants; page size 20 (TOP 21); every page checked against the independent relational oracle.");
        md.AppendLine();
        md.AppendLine("| Scenario | Target corrections | Tenant correction audit rows | Variant | Page | Rows | audit_history logical reads | Other tables | OPENJSON evaluations (rows parsed) | Server median ms | Client median ms | = oracle |");
        md.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|");

        var lookups = new StringBuilder();
        var operators = new StringBuilder();
        foreach (var name in new[] { "TC", "TCV", "TCT", "TCS" })
        {
            if (!fixture.Scenarios.TryGetValue(name, out var scenario))
            {
                continue;
            }

            await using var db = fixture.CreateContext();
            var oracle = await OracleAsync(db, scenario.Sources);
            var tenantId = scenario.Sources.TenantId;
            var tenantCorrectionAudits = await db.Database.SqlQuery<int>(
                $"SELECT COUNT(*) AS [Value] FROM audit_history WHERE tenant_id = {tenantId} AND entity_type = 'TIME_CORRECTION'").SingleAsync();
            Assert.Equal(scenario.Spec.TimeCorrections * scenario.Spec.PerTimeCorrection + scenario.Spec.UnrelatedCorrections * scenario.Spec.PerTimeCorrection, tenantCorrectionAudits);

            foreach (var (label, position) in new[] { ("first", -1), ("deep (80%)", (int)(oracle.Count * 0.8)) })
            {
                TimelinePosition? after = position < 0 ? null : new TimelinePosition(oracle[position].OccurredAt, oracle[position].AuditId);
                var runs = new[]
                {
                    await MeasureAsync("before", label, scenario, after, oracle, position, d => BeforePageAsync(d, scenario.Sources, scenario.TimeCorrectionIds, after, 21)),
                    await MeasureAsync("after", label, scenario, after, oracle, position, async _ =>
                    {
                        await using var storeScope = fixture.CreateScope();
                        var store = storeScope.ServiceProvider.GetRequiredService<IAuditTimelineStore>();
                        return (await store.ReadPageAsync(scenario.Sources, after, 21, CancellationToken.None)).ToList();
                    }),
                };
                foreach (var run in runs)
                {
                    operators.AppendLine($"- {name} / {run.Variant} / {run.Page}: {run.Operators}");
                    md.AppendLine(
                        $"| {name} | {scenario.Spec.TimeCorrections} | {tenantCorrectionAudits} | {run.Variant} | {run.Page} | {run.Rows} | {run.AuditReads} | {run.OtherTables} | {run.TvfExecutions} ({run.TvfRows:N0}) | {run.ServerMs:F1} | {run.ClientMs:F1} | {run.MatchesOracle} |");
                }
            }

            // The extra statement the "before" design ran on every request (the id lookup); the "after" design has none.
            fixture.Capture.Commands.Clear();
            await using var lookupScope = fixture.CreateScope();
            var ids = await BeforeLookupAsync(lookupScope.ServiceProvider.GetRequiredService<RepairRequestDbContext>(), scenario.Sources.TenantId, scenario.Sources.WorkOrderId);
            Assert.Equal(scenario.TimeCorrectionIds.Order(), ids.Order());
            var lookupCommand = fixture.Capture.Commands.Last(item => item.Sql.Contains("[time_correction]", StringComparison.Ordinal));
            var lookup = await SqlPlanEvidence.MeasureAsync(AuditTimelineDatabaseFixture.ConnectionString, lookupCommand.Sql, lookupCommand.Parameters);
            var lookupMs = await SqlPlanEvidence.MedianMillisecondsAsync(AuditTimelineDatabaseFixture.ConnectionString, lookupCommand.Sql, lookupCommand.Parameters);
            lookups.AppendLine($"- {name}: before-only lookup returned {ids.Count} ids; {lookup.OtherTables}; median {lookupMs:F2} ms; operators: {lookup.OperatorSummary}");
        }

        md.AppendLine();
        md.AppendLine("## Actual plan operators per run");
        md.AppendLine(operators.ToString());
        md.AppendLine("## The statement the before-design ran in addition on every request (correction id lookup; the after-design has none)");
        md.AppendLine(lookups.ToString());
        var path = Path.Combine(Path.GetDirectoryName(SqlPlanEvidence.EvidencePath)!, "time-correction-mitigation-evidence.md");
        await File.WriteAllTextAsync(path, md.ToString());
        Assert.True(File.Exists(path));
    }
}
