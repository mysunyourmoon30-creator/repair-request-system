using System.Diagnostics;
using Microsoft.Data.SqlClient;

namespace RepairRequest.ApiTests;

/// <summary>
/// Focused tests of <see cref="FixtureDatabaseDropDiagnostics"/> against real SQL Server errors on throwaway databases of their own
/// (<c>RepairRequestDb_DropDiag_*</c>; created and removed here). A real, immediate error 3702 is produced by running <c>DROP DATABASE</c> on a
/// connection whose current database is the one being dropped; a second, separate "holder" connection stands for the unproven holder of the CI failure.
/// </summary>
public sealed class FixtureDatabaseDropDiagnosticsTests : IAsyncLifetime
{
    private const string Server = "Server=(localdb)\\MSSQLLocalDB;TrustServerCertificate=True;Trusted_Connection=True";

    private readonly List<string> _databases = [];
    private readonly List<SqlConnection> _open = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var connection in _open)
        {
            await connection.DisposeAsync();
        }

        foreach (var name in _databases)
        {
            try
            {
                await using var master = new SqlConnection($"{Server};Database=master;Pooling=False");
                await master.OpenAsync();
                await using var command = master.CreateCommand();
                command.CommandText = $"IF DB_ID('{name}') IS NOT NULL BEGIN ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]; END";
                await command.ExecuteNonQueryAsync();
            }
            catch
            {
                // a throwaway database of this test class only; a leftover is harmless and has a unique name
            }
        }
    }

    private static string ConnectionString(string database, string application) =>
        $"{Server};Database={database};Pooling=False;Application Name={application}";

    private async Task<string> NewDatabaseAsync()
    {
        var name = $"RepairRequestDb_DropDiag_{Guid.NewGuid():N}";
        _databases.Add(name);
        await using var master = new SqlConnection($"{Server};Database=master;Pooling=False");
        await master.OpenAsync();
        await using var command = master.CreateCommand();
        command.CommandText = $"CREATE DATABASE [{name}]";
        await command.ExecuteNonQueryAsync();
        return name;
    }

    /// <summary>A separate session whose current database is <paramref name="database"/>, kept open for the test.</summary>
    private async Task<(int SessionId, string Application)> HolderAsync(string database, string application)
    {
        var connection = new SqlConnection(ConnectionString(database, application));
        _open.Add(connection);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT @@SPID";
        return (Convert.ToInt32(await command.ExecuteScalarAsync()), application);
    }

    /// <summary>The fixture's drop, simulated: DROP DATABASE issued from a session inside that database fails at once with the real error 3702.</summary>
    private static async Task DropFromInsideAsync(string database)
    {
        await using var connection = new SqlConnection(ConnectionString(database, "rr-drop-diag-dropper"));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP DATABASE [{database}]";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<SqlException> Real3702Async(string database)
    {
        try
        {
            await DropFromInsideAsync(database);
        }
        catch (SqlException exception)
        {
            return exception;
        }

        throw new InvalidOperationException("The drop unexpectedly succeeded.");
    }

    // ---------------- collection happens for 3702 and reports the fixture's own database ----------------

    [Fact]
    public async Task Error3702_ReportsIdentityTimingError_AndTheSessionsOfThatDatabaseOnly_ThenRethrowsTheOriginalWithItsStack()
    {
        var target = await NewDatabaseAsync();
        var other = await NewDatabaseAsync();
        var holder = await HolderAsync(target, "rr-drop-diag-holder-target");
        var unrelated = await HolderAsync(other, "rr-drop-diag-holder-other");
        var output = new StringWriter();
        SqlException? original = null;

        var thrown = await Assert.ThrowsAsync<SqlException>(() => FixtureDatabaseDropDiagnostics.RunAsync(
            async () =>
            {
                try
                {
                    await DropFromInsideAsync(target);
                }
                catch (SqlException exception)
                {
                    original = exception;
                    throw;
                }
            },
            ConnectionString(target, "fixture"),
            "FakeFixtureFactory",
            output: output));

        // The original exception object is rethrown, with its original frames still in the stack.
        Assert.Same(original, thrown);
        Assert.Equal(3702, thrown.Number);
        Assert.Contains("OnError", thrown.StackTrace, StringComparison.Ordinal);
        Assert.Contains(nameof(DropFromInsideAsync), thrown.StackTrace, StringComparison.Ordinal);

        var text = output.ToString();
        Assert.Contains("fixture=FakeFixtureFactory", text, StringComparison.Ordinal);
        Assert.Contains($"database={target}", text, StringComparison.Ordinal);
        Assert.Contains("error=3702", text, StringComparison.Ordinal);
        Assert.Contains("startedUtc=", text, StringComparison.Ordinal);
        Assert.Contains("failedUtc=", text, StringComparison.Ordinal);
        Assert.Contains("dropDurationMs=", text, StringComparison.Ordinal);
        Assert.Matches(@"sessions\(\d+\)", text);
        Assert.Contains($"session_id={holder.SessionId} status=sleeping program_name={holder.Application}", text, StringComparison.Ordinal);
        Assert.Contains("requests(0)", text, StringComparison.Ordinal);
        Assert.Contains("locks(", text, StringComparison.Ordinal);
        Assert.Contains("inconclusive", text, StringComparison.Ordinal);

        // Another database's session is not in the snapshot; neither is any SQL text.
        Assert.DoesNotContain(unrelated.Application, text, StringComparison.Ordinal);
        Assert.DoesNotContain($"session_id={unrelated.SessionId} ", text, StringComparison.Ordinal);
        Assert.DoesNotContain($"DROP DATABASE [{target}]", text, StringComparison.OrdinalIgnoreCase);   // the dropper's statement text is never collected
        Assert.DoesNotContain("SELECT", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheSnapshot_ShowsLocksAndRequests_OfThatDatabase()
    {
        var target = await NewDatabaseAsync();
        var holder = await HolderAsync(target, "rr-drop-diag-holder-locks");

        // A session with an open transaction and a running request that waits on a lock in that database.
        await using var blocker = new SqlConnection(ConnectionString(target, "rr-drop-diag-blocker"));
        await blocker.OpenAsync();
        await using (var create = blocker.CreateCommand())
        {
            create.CommandText = "CREATE TABLE dbo.t (id int PRIMARY KEY); INSERT dbo.t VALUES (1);";
            await create.ExecuteNonQueryAsync();
        }

        await using var transaction = (SqlTransaction)await blocker.BeginTransactionAsync();
        await using (var lockRow = blocker.CreateCommand())
        {
            lockRow.Transaction = transaction;
            lockRow.CommandText = "UPDATE dbo.t SET id = 1 WHERE id = 1";
            await lockRow.ExecuteNonQueryAsync();
        }

        await using var waiter = new SqlConnection(ConnectionString(target, "rr-drop-diag-waiter"));
        await waiter.OpenAsync();
        int waitingSession;
        await using (var spid = waiter.CreateCommand())
        {
            spid.CommandText = "SELECT @@SPID";
            waitingSession = Convert.ToInt32(await spid.ExecuteScalarAsync());
        }

        var waiting = Task.Run(async () =>
        {
            await using var command = waiter.CreateCommand();
            command.CommandText = "SELECT id FROM dbo.t WITH (UPDLOCK)";
            command.CommandTimeout = 60;
            try
            {
                await command.ExecuteScalarAsync();
            }
            catch (SqlException)
            {
                // cancelled when the blocker is released at the end
            }
        });

        var output = new StringWriter();
        try
        {
            // wait until SQL Server reports the waiter as blocked, so the request is in the snapshot deterministically
            var deadline = Stopwatch.GetTimestamp();
            while (Stopwatch.GetElapsedTime(deadline) < TimeSpan.FromSeconds(20))
            {
                await using var probe = new SqlConnection($"{Server};Database=master;Pooling=False");
                await probe.OpenAsync();
                await using var count = probe.CreateCommand();
                count.CommandText = $"SELECT COUNT(*) FROM sys.dm_exec_requests WHERE session_id = {waitingSession} AND blocking_session_id <> 0";
                var blocked = Convert.ToInt32(await count.ExecuteScalarAsync());
                if (blocked > 0)
                {
                    break;
                }

                await Task.Delay(50);
            }

            await Assert.ThrowsAsync<SqlException>(() => FixtureDatabaseDropDiagnostics.RunAsync(
                () => DropFromInsideAsync(target), ConnectionString(target, "fixture"), "LockFactory", output: output));
        }
        finally
        {
            await transaction.RollbackAsync();
            await waiting;
        }

        var text = output.ToString();
        Assert.Contains($"session_id={holder.SessionId} ", text, StringComparison.Ordinal);
        Assert.Matches($@"requests\(\d+\)", text);
        Assert.Contains($"session_id={waitingSession} status=suspended command=SELECT", text, StringComparison.Ordinal);
        Assert.Contains("blocking_session_id=", text, StringComparison.Ordinal);
        Assert.Contains("wait_type=LCK_M_U", text, StringComparison.Ordinal);
        Assert.Contains("resource_type=DATABASE mode=S status=GRANT", text, StringComparison.Ordinal);
        Assert.Contains("resource_type=KEY", text, StringComparison.Ordinal);
    }

    // ---------------- diagnostics never replace the original exception ----------------

    [Fact]
    public async Task ACollectorFailure_NeverReplacesTheOriginalException()
    {
        var target = await NewDatabaseAsync();
        var original = await Real3702Async(target);
        var output = new StringWriter();

        var thrown = await Assert.ThrowsAsync<SqlException>(() => FixtureDatabaseDropDiagnostics.RunAsync(
            () => Task.FromException(original),
            ConnectionString(target, "fixture"),
            "FailingCollector",
            snapshot: (_, _, _) => throw new InvalidOperationException("collector boom"),
            output: output));

        Assert.Same(original, thrown);
        Assert.Equal(3702, thrown.Number);
        Assert.Contains("snapshot collection failed (InvalidOperationException", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("fixture=FailingCollector", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnreachableMaster_RealCollector_NeverReplacesTheOriginalException()
    {
        var target = await NewDatabaseAsync();
        var original = await Real3702Async(target);
        var output = new StringWriter();

        // The diagnostics' own connection cannot be made (nothing listens there); the fixture's drop error is unrelated to it.
        var thrown = await Assert.ThrowsAsync<SqlException>(() => FixtureDatabaseDropDiagnostics.RunAsync(
            () => Task.FromException(original),
            $"Server=127.0.0.1,1;Database={target};Connect Timeout=2;TrustServerCertificate=True;User Id=none;Password=none",
            "UnreachableMaster",
            output: output));

        Assert.Same(original, thrown);
        Assert.Contains("snapshot collection failed (SqlException", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("inconclusive", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACollectorThatNeverCompletes_IsCutOffByTheTimeout_AndTheOriginalExceptionStillPropagates()
    {
        var target = await NewDatabaseAsync();
        var original = await Real3702Async(target);
        var output = new StringWriter();
        var started = Stopwatch.GetTimestamp();

        var thrown = await Assert.ThrowsAsync<SqlException>(() => FixtureDatabaseDropDiagnostics.RunAsync(
            () => Task.FromException(original),
            ConnectionString(target, "fixture"),
            "HangingCollector",
            snapshot: (_, _, _) => new TaskCompletionSource<string>().Task,    // ignores the cancellation token on purpose
            output: output,
            timeout: TimeSpan.FromMilliseconds(300)));

        Assert.Same(original, thrown);
        Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(5));
        Assert.Contains("timed out after", output.ToString(), StringComparison.Ordinal);
    }

    // ---------------- other outcomes keep their behavior and trigger no diagnostics ----------------

    [Fact]
    public async Task Success_RunsNoDiagnostics()
    {
        var calls = 0;
        var output = new StringWriter();

        await FixtureDatabaseDropDiagnostics.RunAsync(
            () => Task.CompletedTask, "Server=ignored", "Fine", snapshot: (_, _, _) => { calls++; return Task.FromResult("x"); }, output: output);

        Assert.Equal(0, calls);
        Assert.Equal(string.Empty, output.ToString());
    }

    [Fact]
    public async Task AnotherSqlError_IsRethrownUnchanged_WithoutDiagnostics()
    {
        var calls = 0;
        var output = new StringWriter();
        SqlException? original = null;
        try
        {
            await using var connection = new SqlConnection(ConnectionString($"RepairRequestDb_DropDiag_missing_{Guid.NewGuid():N}", "rr-drop-diag-missing"));
            await connection.OpenAsync();
        }
        catch (SqlException exception)
        {
            original = exception;                                   // a real login failure (4060): "cannot open database"
        }

        Assert.NotNull(original);
        Assert.NotEqual(3702, original.Number);

        var thrown = await Assert.ThrowsAsync<SqlException>(() => FixtureDatabaseDropDiagnostics.RunAsync(
            () => Task.FromException(original), "Server=ignored", "Other", snapshot: (_, _, _) => { calls++; return Task.FromResult("x"); }, output: output));

        Assert.Same(original, thrown);
        Assert.Equal(0, calls);
        Assert.Equal(string.Empty, output.ToString());
    }

    [Fact]
    public async Task ANonSqlException_IsRethrownUnchanged_WithoutDiagnostics()
    {
        var calls = 0;
        var output = new StringWriter();
        var original = new InvalidOperationException("not a sql error");

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => FixtureDatabaseDropDiagnostics.RunAsync(
            () => Task.FromException(original), "Server=ignored", "NonSql", snapshot: (_, _, _) => { calls++; return Task.FromResult("x"); }, output: output));

        Assert.Same(original, thrown);
        Assert.Equal(0, calls);
        Assert.Equal(string.Empty, output.ToString());
    }

    [Fact]
    public async Task Classification_RecognizesError3702_AnywhereInTheChain_AndNothingElse()
    {
        var target = await NewDatabaseAsync();
        var real3702 = await Real3702Async(target);

        Assert.True(FixtureDatabaseDropDiagnostics.TryGetDatabaseInUse(real3702, out var error));
        Assert.Equal(3702, error!.Number);
        Assert.True(FixtureDatabaseDropDiagnostics.TryGetDatabaseInUse(new InvalidOperationException("wrapped", real3702), out _));
        Assert.True(FixtureDatabaseDropDiagnostics.TryGetDatabaseInUse(new AggregateException(new InvalidOperationException("x"), real3702), out _));
        Assert.False(FixtureDatabaseDropDiagnostics.TryGetDatabaseInUse(new InvalidOperationException("plain"), out _));
        Assert.False(FixtureDatabaseDropDiagnostics.TryGetDatabaseInUse(null, out _));
    }

    // ---------------- the real collector stays on the fixture's own database ----------------

    [Theory]
    [InlineData("master")]
    [InlineData("model")]
    [InlineData("msdb")]
    [InlineData("tempdb")]
    [InlineData("")]
    public async Task TheCollector_RefusesSystemDatabases_WithoutConnecting(string database)
    {
        // The connection string points at nothing: refusing must happen before any connection attempt.
        var text = await FixtureDatabaseDropDiagnostics.CollectAsync("Server=127.0.0.1,1;Connect Timeout=1", database, CancellationToken.None);

        Assert.Contains("snapshot refused", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCollector_ReportsADatabaseThatNoLongerExists_AsInconclusive()
    {
        var name = $"RepairRequestDb_DropDiag_gone_{Guid.NewGuid():N}";

        var text = await FixtureDatabaseDropDiagnostics.CollectAsync(ConnectionString(name, "fixture"), name, CancellationToken.None);

        Assert.Contains("no longer exists", text, StringComparison.Ordinal);
        Assert.Contains("inconclusive", text, StringComparison.Ordinal);
    }
}
