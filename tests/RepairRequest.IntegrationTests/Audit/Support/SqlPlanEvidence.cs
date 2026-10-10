using System.Data;
using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace RepairRequest.IntegrationTests.Audit.Support;

/// <summary>Captures the SQL text and parameters EF Core actually sends (docs/15 §5, §11, §12).</summary>
public sealed class CapturingInterceptor : DbCommandInterceptor
{
    public List<CapturedCommand> Commands { get; } = [];

    public override ValueTask<InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
        System.Data.Common.DbCommand command, CommandEventData eventData, InterceptionResult<System.Data.Common.DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Capture(command);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<System.Data.Common.DbDataReader> ReaderExecuting(
        System.Data.Common.DbCommand command, CommandEventData eventData, InterceptionResult<System.Data.Common.DbDataReader> result)
    {
        Capture(command);
        return base.ReaderExecuting(command, eventData, result);
    }

    private void Capture(System.Data.Common.DbCommand command) =>
        Commands.Add(new CapturedCommand(
            command.CommandText,
            command.Parameters.Cast<SqlParameter>().Select(p => (SqlParameter)((ICloneable)p).Clone()).ToList()));
}

public sealed record CapturedCommand(string Sql, IReadOnlyList<SqlParameter> Parameters);

public sealed record PlanOperator(string PhysicalOp, string? Index, long ActualRows, long ActualRowsRead, long Executions, long KeyLookupRows);

public sealed record Measurement(
    int RowsReturned,
    long LogicalReads,
    long ScanCount,
    string OtherTables,
    IReadOnlyList<PlanOperator> Operators,
    bool HasSpill,
    long SpillWritesToTempDb,
    int ParameterCount,
    string PlanXml)
{
    private static string Join(IEnumerable<string> parts) => string.Join(", ", parts);

    public long AuditRowsReadBySeekOrScan =>
        Operators.Where(o => o.Index is not null && o.Index.Contains("audit_history", StringComparison.OrdinalIgnoreCase)
                              && o.PhysicalOp.Contains("Seek", StringComparison.OrdinalIgnoreCase)
                              || o.PhysicalOp.Contains("Scan", StringComparison.OrdinalIgnoreCase) && (o.Index ?? "").Contains("audit_history", StringComparison.OrdinalIgnoreCase))
                 .Sum(o => o.ActualRowsRead > 0 ? o.ActualRowsRead : o.ActualRows);

    public string OperatorSummary => Join(Operators
        .GroupBy(o => (o.PhysicalOp, o.Index))
        .Select(g => $"{g.Key.PhysicalOp}{(g.Key.Index is null ? "" : $"[{g.Key.Index}]")} x{g.Sum(o => o.Executions)} (rows {g.Sum(o => o.ActualRows)}, read {g.Sum(o => o.ActualRowsRead)})"));
}

public static class SqlPlanEvidence
{
    private static readonly XNamespace Plan = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    /// <summary>
    /// Re-executes the captured/handwritten SQL text with its parameters under SET STATISTICS IO ON / XML ON, so the
    /// logical reads and the ACTUAL execution plan (with runtime row counters) belong to exactly that text.
    /// </summary>
    public static async Task<Measurement> MeasureAsync(string connectionString, string sql, IEnumerable<SqlParameter> parameters)
    {
        await using var connection = new SqlConnection(connectionString);
        var messages = new StringBuilder();
        connection.InfoMessage += (_, e) => messages.AppendLine(e.Message);
        await connection.OpenAsync();

        await using (var setup = connection.CreateCommand())
        {
            setup.CommandText = "SET STATISTICS IO ON; SET STATISTICS XML ON;";
            await setup.ExecuteNonQueryAsync();
        }

        var cloned = parameters.Select(p => (SqlParameter)((ICloneable)p).Clone()).ToList();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddRange(cloned.ToArray());

        var rows = 0;
        string planXml = "";
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                rows++;
            }

            while (await reader.NextResultAsync())
            {
                while (await reader.ReadAsync())
                {
                    planXml = reader.GetValue(0)?.ToString() ?? planXml;
                }
            }
        }

        long logical = 0;
        long scans = 0;
        var other = new List<string>();
        foreach (var line in messages.ToString().Split('\n'))
        {
            var t = line.Trim();
            if (!t.StartsWith("Table '", StringComparison.Ordinal))
            {
                continue;
            }

            var table = t[7..t.IndexOf('\'', 7)];
            var scan = ReadNumber(t, "Scan count ");
            var reads = ReadNumber(t, "logical reads ");
            if (table.Equals("audit_history", StringComparison.OrdinalIgnoreCase))
            {
                logical += reads;
                scans += scan;
            }
            else
            {
                other.Add($"{table}: scans {scan}, logical reads {reads}");
            }
        }

        var (operators, spill, spillWrites) = ParsePlan(planXml);
        return new Measurement(rows, logical, scans, string.Join("; ", other), operators, spill, spillWrites, cloned.Count, planXml);
    }

    private static long ReadNumber(string text, string label)
    {
        var i = text.IndexOf(label, StringComparison.Ordinal);
        if (i < 0)
        {
            return 0;
        }

        i += label.Length;
        var j = i;
        while (j < text.Length && char.IsDigit(text[j]))
        {
            j++;
        }

        return long.Parse(text[i..j], CultureInfo.InvariantCulture);
    }

    private static (List<PlanOperator> Operators, bool Spill, long SpillWrites) ParsePlan(string planXml)
    {
        var result = new List<PlanOperator>();
        if (string.IsNullOrWhiteSpace(planXml))
        {
            return (result, false, 0);
        }

        var doc = XDocument.Parse(planXml);
        foreach (var rel in doc.Descendants(Plan + "RelOp"))
        {
            var physical = (string?)rel.Attribute("PhysicalOp") ?? "?";
            var index = rel.Elements().SelectMany(e => e.DescendantsAndSelf(Plan + "Object")).FirstOrDefault()?.Attribute("Index")?.Value
                        ?? rel.Descendants(Plan + "Object").FirstOrDefault()?.Attribute("Index")?.Value;
            if (!physical.Contains("Seek", StringComparison.Ordinal) && !physical.Contains("Scan", StringComparison.Ordinal) && !physical.Contains("Lookup", StringComparison.Ordinal))
            {
                index = null;
            }

            long rowsOut = 0, rowsRead = 0, execs = 0;
            foreach (var counter in rel.Elements(Plan + "RunTimeInformation").Elements(Plan + "RunTimeCountersPerThread"))
            {
                rowsOut += Number(counter, "ActualRows");
                rowsRead += Number(counter, "ActualRowsRead");
                execs += Number(counter, "ActualExecutions");
            }

            result.Add(new PlanOperator(physical, index?.Trim('[', ']'), rowsOut, rowsRead, execs, physical.Contains("Lookup", StringComparison.Ordinal) ? rowsOut : 0));
        }

        var spill = doc.Descendants(Plan + "SpillToTempDb").Any() || doc.Descendants(Plan + "SortSpillDetails").Any();
        var spillWrites = doc.Descendants(Plan + "SortSpillDetails").Sum(e => Number(e, "WritesToTempDb"));
        return (result, spill, spillWrites);
    }

    private static long Number(XElement e, string attribute) =>
        long.TryParse((string?)e.Attribute(attribute), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    /// <summary>Median wall-clock milliseconds of <paramref name="runs"/> warm executions on one client (informational only, not a latency claim).</summary>
    public static async Task<double> MedianMillisecondsAsync(string connectionString, string sql, IEnumerable<SqlParameter> parameters, int runs = 15)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        var samples = new List<double>();
        for (var i = 0; i < runs + 2; i++)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddRange(parameters.Select(p => (SqlParameter)((ICloneable)p).Clone()).ToArray());
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            await using (var reader = await command.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                }
            }

            var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (i >= 2)
            {
                samples.Add(elapsed); // the first two runs warm the plan/buffer cache
            }
        }

        samples.Sort();
        return samples[samples.Count / 2];
    }

    public static async Task<string> DescribeIndexesAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT i.name, i.type_desc, i.is_primary_key, i.is_unique,
       STUFF((SELECT ', ' + c.name + CASE WHEN ic.is_descending_key = 1 THEN ' DESC' ELSE '' END + CASE WHEN ic.is_included_column = 1 THEN ' (INCLUDE)' ELSE '' END
              FROM sys.index_columns ic JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
              WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id ORDER BY ic.is_included_column, ic.key_ordinal FOR XML PATH('')), 1, 2, '') AS cols
FROM sys.indexes i WHERE i.object_id = OBJECT_ID('audit_history') AND i.type > 0 ORDER BY i.index_id";
        var sb = new StringBuilder();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            sb.AppendLine($"- `{reader.GetString(0)}` — {reader.GetString(1)}{(reader.GetBoolean(2) ? ", PRIMARY KEY" : "")}{(reader.GetBoolean(3) ? ", UNIQUE" : "")}: ({reader.GetString(4)})");
        }

        return sb.ToString();
    }

    public static string EvidencePath =>
        Environment.GetEnvironmentVariable("TIMELINE_EVIDENCE_PATH")
        ?? Path.Combine(AppContext.BaseDirectory, "timeline-evidence.md");
}
