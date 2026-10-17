using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;

namespace RepairRequest.ApiTests;

/// <summary>
/// Bounded, best-effort diagnostics for ONE failure of the per-class API test databases: a fixture-owned <c>DROP DATABASE</c> (EF Core
/// <c>EnsureDeletedAsync</c>) that fails with SQL Server error 3702 ("Cannot drop database … because it is currently in use").
/// <para>
/// Why: the post-merge run 38586343631 (attempt 1) failed once with exactly that error after 20,004 ms although every test passed, and the
/// session that held the database could not be named from the logs (see <c>docs/18</c>). When the error occurs again this class records, from a
/// separate connection to <c>master</c>, which sessions, requests and locks belonged to that one database at that moment, and prints them to the
/// test output (stdout, which vstest keeps in the TRX). It is diagnostics only: it does not retry, suppress, disconnect anything, clear pools or change
/// the order of the cleanup, and the original exception is always rethrown with its stack (<c>throw;</c>).
/// </para>
/// <para>
/// Limits: the snapshot is taken AFTER the drop failed (the failed statement waited ~20 s), so a holder that disappeared in between is not seen — an
/// empty snapshot is inconclusive, not proof that nothing held the database. The collector needs <c>VIEW SERVER STATE</c>; without it, or on any connection
/// error or after the diagnostic timeout, a one-line note is printed instead and the original exception still propagates. No SQL text and no parameters
/// are collected. Only database ids of the fixture's own database are queried.
/// </para>
/// </summary>
internal static class FixtureDatabaseDropDiagnostics
{
    /// <summary>SQL Server error: "Cannot drop database '%.*ls' because it is currently in use."</summary>
    internal const int DatabaseInUseErrorNumber = 3702;

    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private const string Prefix = "[FIXTURE-DROP-DIAGNOSTICS]";
    private const int RowCap = 50;

    /// <summary>Produces the snapshot text for one database; replaceable by tests.</summary>
    internal delegate Task<string> SnapshotProvider(string fixtureConnectionString, string databaseName, CancellationToken cancellationToken);

    /// <summary>
    /// Runs <paramref name="drop"/> (the fixture's own <c>EnsureDeletedAsync</c>). Success and every error other than 3702 behave exactly as without this
    /// wrapper. On 3702 the snapshot is printed and the original exception is rethrown unchanged.
    /// </summary>
    internal static async Task RunAsync(
        Func<Task> drop,
        string fixtureConnectionString,
        string fixtureName,
        SnapshotProvider? snapshot = null,
        TextWriter? output = null,
        TimeSpan? timeout = null,
        Func<DateTimeOffset>? clock = null)
    {
        var now = clock ?? (() => DateTimeOffset.UtcNow);
        var startedAt = now();
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await drop();
        }
        catch (Exception exception) when (TryGetDatabaseInUse(exception, out _))
        {
            stopwatch.Stop();
            var error = TryGetDatabaseInUse(exception, out var sqlError) ? sqlError! : null;
            await ReportBestEffortAsync(
                exception, error, fixtureConnectionString, fixtureName, startedAt, now(), stopwatch.Elapsed, snapshot ?? CollectAsync, output ?? Console.Out, timeout ?? DefaultTimeout);
            throw;
        }
    }

    /// <summary>True for a <see cref="SqlException"/> carrying error 3702 anywhere in the exception chain.</summary>
    internal static bool TryGetDatabaseInUse(Exception? exception, out SqlError? error)
    {
        error = null;
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException sql)
            {
                foreach (SqlError candidate in sql.Errors)
                {
                    if (candidate.Number == DatabaseInUseErrorNumber)
                    {
                        error = candidate;
                        return true;
                    }
                }
            }

            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    if (TryGetDatabaseInUse(inner, out error))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static async Task ReportBestEffortAsync(
        Exception exception,
        SqlError? error,
        string fixtureConnectionString,
        string fixtureName,
        DateTimeOffset startedAt,
        DateTimeOffset failedAt,
        TimeSpan duration,
        SnapshotProvider snapshot,
        TextWriter output,
        TimeSpan timeout)
    {
        // Nothing in here may throw: whatever happens, the caller rethrows the ORIGINAL exception.
        try
        {
            var databaseName = "<unknown>";
            try
            {
                databaseName = new SqlConnectionStringBuilder(fixtureConnectionString).InitialCatalog;
            }
            catch
            {
                // the identity line below says <unknown>
            }

            var text = new StringBuilder();
            text.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{Prefix} fixture={fixtureName} database={databaseName} error={error?.Number} state={error?.State} class={error?.Class} startedUtc={startedAt:O} failedUtc={failedAt:O} dropDurationMs={duration.TotalMilliseconds:F0} exception={exception.GetType().Name} message=\"{Flatten(error?.Message ?? exception.Message)}\""));

            try
            {
                using var cts = new CancellationTokenSource(timeout);
                var collected = snapshot(fixtureConnectionString, databaseName, cts.Token);
                _ = collected.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted); // a late failure after the timeout is observed, never rethrown
                text.Append(await collected.WaitAsync(timeout));
            }
            catch (TimeoutException)
            {
                text.AppendLine($"{Prefix} snapshot collection timed out after {timeout.TotalSeconds:F0} s; no snapshot (inconclusive).");
            }
            catch (OperationCanceledException)
            {
                text.AppendLine($"{Prefix} snapshot collection timed out after {timeout.TotalSeconds:F0} s; no snapshot (inconclusive).");
            }
            catch (Exception collectorFailure)
            {
                text.AppendLine($"{Prefix} snapshot collection failed ({collectorFailure.GetType().Name}: {Flatten(collectorFailure.Message)}); no snapshot (inconclusive). The original exception follows unchanged.");
            }

            output.Write(text.ToString());
            output.Flush();
        }
        catch
        {
            // best effort only
        }
    }

    /// <summary>The real collector: read-only queries on a separate, unpooled connection to master, limited to the fixture's own database id.</summary>
    internal static async Task<string> CollectAsync(string fixtureConnectionString, string databaseName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(databaseName) || IsSystemDatabase(databaseName))
        {
            return $"{Prefix} snapshot refused: '{databaseName}' is not a fixture-owned database name.{Environment.NewLine}";
        }

        var builder = new SqlConnectionStringBuilder(fixtureConnectionString)
        {
            InitialCatalog = "master",
            Pooling = false,
            ConnectTimeout = 5,
            ApplicationName = "rr-fixture-drop-diagnostics",
        };

        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        var text = new StringBuilder();
        var snapshotAt = DateTimeOffset.UtcNow;

        int? databaseId;
        await using (var lookup = connection.CreateCommand())
        {
            lookup.CommandText = "SELECT DB_ID(@name);";
            lookup.CommandTimeout = 5;
            lookup.Parameters.AddWithValue("@name", databaseName);
            var value = await lookup.ExecuteScalarAsync(cancellationToken);
            databaseId = value is null or DBNull ? null : Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }

        if (databaseId is null)
        {
            text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"{Prefix} snapshotUtc={snapshotAt:O} database '{databaseName}' no longer exists at collection time (inconclusive: the holder, if any, is gone)."));
            return text.ToString();
        }

        text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"{Prefix} snapshotUtc={snapshotAt:O} databaseId={databaseId}"));

        var sessions = await QueryAsync(
            connection,
            databaseId.Value,
            $"SELECT TOP ({RowCap}) session_id, status, program_name, is_user_process, open_transaction_count, login_time, last_request_start_time, last_request_end_time "
            + "FROM sys.dm_exec_sessions WHERE database_id = @databaseId AND session_id <> @@SPID ORDER BY session_id;",
            ["session_id", "status", "program_name", "is_user_process", "open_transaction_count", "login_time_server_local", "last_request_start_server_local", "last_request_end_server_local"],
            cancellationToken);
        Append(text, "sessions", sessions);

        var requests = await QueryAsync(
            connection,
            databaseId.Value,
            $"SELECT TOP ({RowCap}) session_id, status, command, wait_type, wait_time, blocking_session_id, start_time "
            + "FROM sys.dm_exec_requests WHERE database_id = @databaseId AND session_id <> @@SPID ORDER BY session_id;",
            ["session_id", "status", "command", "wait_type", "wait_time_ms", "blocking_session_id", "start_server_local"],
            cancellationToken);
        Append(text, "requests", requests);

        var locks = await QueryAsync(
            connection,
            databaseId.Value,
            $"SELECT TOP ({RowCap}) request_session_id, resource_type, request_mode, request_status, COUNT_BIG(*) AS lock_count "
            + "FROM sys.dm_tran_locks WHERE resource_database_id = @databaseId "
            + "GROUP BY request_session_id, resource_type, request_mode, request_status ORDER BY request_session_id, resource_type, request_mode;",
            ["session_id", "resource_type", "mode", "status", "count"],
            cancellationToken);
        Append(text, "locks", locks);

        text.AppendLine($"{Prefix} note: taken after the failed drop; an empty snapshot is inconclusive (the holder may have disappeared first). No SQL text or parameters are collected.");
        return text.ToString();
    }

    private static bool IsSystemDatabase(string name) =>
        name.Equals("master", StringComparison.OrdinalIgnoreCase)
        || name.Equals("model", StringComparison.OrdinalIgnoreCase)
        || name.Equals("msdb", StringComparison.OrdinalIgnoreCase)
        || name.Equals("tempdb", StringComparison.OrdinalIgnoreCase);

    private static async Task<List<string>> QueryAsync(
        SqlConnection connection, int databaseId, string sql, string[] columns, CancellationToken cancellationToken)
    {
        var rows = new List<string>();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 5;
        command.Parameters.AddWithValue("@databaseId", databaseId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var cells = new List<string>(columns.Length);
            for (var i = 0; i < columns.Length; i++)
            {
                var value = reader.IsDBNull(i) ? "null" : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture) ?? "null";
                cells.Add($"{columns[i]}={Flatten(value)}");
            }

            rows.Add(string.Join(' ', cells));
        }

        return rows;
    }

    private static void Append(StringBuilder text, string label, List<string> rows)
    {
        text.AppendLine($"{Prefix} {label}({rows.Count}){(rows.Count >= RowCap ? " [capped]" : string.Empty)}");
        foreach (var row in rows)
        {
            text.AppendLine($"{Prefix}   {row}");
        }
    }

    private static string Flatten(string value) =>
        value.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Replace("\"", "'", StringComparison.Ordinal);
}
