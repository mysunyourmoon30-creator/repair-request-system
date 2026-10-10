using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RepairRequest.Application.Audit;
using RepairRequest.IntegrationTests.Audit.Support;

namespace RepairRequest.IntegrationTests.Audit;

/// <summary>
/// OPT-IN evidence run (<c>RUN_TIMELINE_EVIDENCE=1</c>; skipped otherwise, so default IntegrationTests/CI never seed
/// 500,000 audit rows). Measures the PRODUCTION store's actual command under SET STATISTICS IO / XML for every scenario,
/// a first page, a deep page and (KS1) a mid-tie page, and writes the numbers to <c>TIMELINE_EVIDENCE_PATH</c>
/// (default <c>timeline-evidence.md</c> beside the binaries). It asserts correctness only (each page equals SQL Server's
/// own ordering); the figures are evidence, never a performance claim.
/// </summary>
[Collection(AuditTimelineCollection.Name)]
public sealed class AuditTimelineEvidenceTests(AuditTimelineDatabaseFixture fixture)
{
    private sealed class OracleRow
    {
        public Guid AuditId { get; set; }

        public DateTime OccurredAt { get; set; }
    }

    [EvidenceFact]
    public async Task CollectProductionStorePlanEvidence()
    {
        var md = new StringBuilder();
        md.AppendLine("# Work Order timeline — production store plan evidence");
        md.AppendLine();
        md.AppendLine($"Noise rows: {fixture.NoiseRows:N0}; compatibility level: {fixture.CompatibilityLevel}; scenarios: {string.Join(", ", fixture.Scenarios.Keys)}.");
        md.AppendLine();
        md.AppendLine("## audit_history indexes");
        md.AppendLine(await SqlPlanEvidence.DescribeIndexesAsync(AuditTimelineDatabaseFixture.ConnectionString));

        var baseline = await SqlPlanEvidence.MeasureAsync(AuditTimelineDatabaseFixture.ConnectionString, "SELECT COUNT_BIG(*) FROM audit_history", []);
        md.AppendLine($"Context — a bare `COUNT_BIG(*)` over the table costs {baseline.LogicalReads:N0} logical reads.");
        md.AppendLine();
        md.AppendLine("## Measurements (page size 20 → TOP 21; logical reads = audit_history only)");
        md.AppendLine();
        md.AppendLine("| Scenario | Events | Entity ids | Page | Rows returned | Logical reads | Scan count | Index seeks (IX) | IX rows read | PK lookups (executions) | Sorts | Spill (tempdb pages) | Parameters | Median ms (warm, 1 client) |");
        md.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");

        var samples = new StringBuilder();
        foreach (var (name, scenario) in fixture.Scenarios)
        {
            await using var db = fixture.CreateContext();
            var oracle = await OracleAsync(db, scenario.Sources);
            var positions = new List<(string Label, int Position)> { ("first", -1), ("deep (80%)", (int)(oracle.Count * 0.8)) };
            if (name == "KS1")
            {
                positions.Add(("ties (mid)", 70));
            }

            foreach (var (label, position) in positions)
            {
                TimelinePosition? after = position < 0 ? null : new TimelinePosition(oracle[position].OccurredAt, oracle[position].AuditId);

                await using var scope = fixture.CreateScope();
                var store = scope.ServiceProvider.GetRequiredService<IAuditTimelineStore>();
                fixture.Capture.Commands.Clear();
                var rows = await store.ReadPageAsync(scenario.Sources, after, 21, CancellationToken.None);
                var command = fixture.Capture.Commands.Last();
                Assert.Equal(oracle.Skip(position + 1).Take(21).Select(r => r.AuditId), rows.Select(r => r.AuditId));

                var m = await SqlPlanEvidence.MeasureAsync(AuditTimelineDatabaseFixture.ConnectionString, command.Sql, command.Parameters);
                Assert.Equal(rows.Count, m.RowsReturned);
                Assert.DoesNotContain(m.Operators, o => o.PhysicalOp.Contains("Scan", StringComparison.Ordinal) && o.Index is not null && o.Index.Contains("audit_history", StringComparison.OrdinalIgnoreCase));

                var ix = m.Operators.Where(o => o.PhysicalOp == "Index Seek" && o.Index == "IX_audit_history_timeline").ToList();
                var pk = m.Operators.Where(o => o.PhysicalOp == "Clustered Index Seek" && o.Index == "PK_audit_history").ToList();
                var ms = await SqlPlanEvidence.MedianMillisecondsAsync(AuditTimelineDatabaseFixture.ConnectionString, command.Sql, command.Parameters);
                md.AppendLine($"| {name} | {scenario.TotalExpected} | {scenario.EntityIdCount} | {label} | {m.RowsReturned} | {m.LogicalReads} | {m.ScanCount} | {ix.Sum(o => o.Executions)} | {ix.Sum(o => o.ActualRowsRead)} | {pk.Sum(o => o.Executions)} | {m.Operators.Count(o => o.PhysicalOp.Contains("Sort", StringComparison.Ordinal))} | {m.SpillWritesToTempDb} | {m.ParameterCount} | {ms:F1} |");

                if (label == "first" && name is "WO2000" or "BIGIDS")
                {
                    samples.AppendLine($"### {name} / first page — captured command ({command.Parameters.Count} parameters)");
                    samples.AppendLine("```sql");
                    samples.AppendLine(command.Sql);
                    samples.AppendLine("```");
                    samples.AppendLine($"Operators: {m.OperatorSummary}");
                    samples.AppendLine();
                }
            }
        }

        md.AppendLine();
        md.AppendLine("## Captured SQL samples");
        md.AppendLine(samples.ToString());
        await File.WriteAllTextAsync(SqlPlanEvidence.EvidencePath, md.ToString());
        Assert.True(File.Exists(SqlPlanEvidence.EvidencePath));
    }

    private static async Task<List<OracleRow>> OracleAsync(RepairRequest.Infrastructure.Persistence.RepairRequestDbContext db, AuditTimelineSources s)
    {
        static string Ids(IEnumerable<Guid> ids) => ids.Any() ? string.Join(",", ids.Select(g => $"'{g}'")) : "NULL";
        var sql = $@"SELECT a.audit_history_id AS AuditId, a.occurred_at AS OccurredAt FROM audit_history a
WHERE a.tenant_id = '{s.TenantId}' AND (
     (a.entity_type = 'WORK_ORDER' AND a.entity_id = '{s.WorkOrderId}')
  OR (a.entity_type = 'SERVICE_VISIT' AND a.entity_id IN ({Ids(s.VisitIds)}))
  OR (a.entity_type = 'WORK_SESSION' AND a.entity_id IN ({Ids(s.SessionIds)}))
  OR (a.entity_type = 'CORRECTIVE_ACTION' AND a.entity_id IN ({Ids(s.CorrectiveActionIds)}))
  OR (a.entity_type = 'REPAIR_REQUEST' AND a.entity_id = '{s.RepairRequestId}' AND a.action_code = 'REPAIR_REQUEST_CONVERTED'))
ORDER BY a.occurred_at DESC, a.audit_history_id DESC";
        return await db.Database.SqlQueryRaw<OracleRow>(sql).ToListAsync();
    }
}
