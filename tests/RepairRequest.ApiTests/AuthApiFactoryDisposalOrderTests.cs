using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RepairRequest.Api.DevelopmentScanning;
using RepairRequest.ApiTests.DevelopmentScanning;
using RepairRequest.Infrastructure.Persistence;

namespace RepairRequest.ApiTests;

/// <summary>
/// Controlled lifecycle tests of <see cref="AuthApiFactory"/>'s disposal (the fixtures that own a LocalDB database): the fixture's own host, including
/// background services such as the 200 ms <c>DevelopmentOnlyPendingScanRunner</c>, is stopped and awaited BEFORE its database is dropped, and nothing of
/// the fixture touches the database afterwards. Each test uses a database of its own (<c>RepairRequestDb_DisposalProbe_*</c>) and a lifecycle gate resource
/// of its own, so it neither disturbs nor depends on other classes; synchronization is explicit: the test holds the gate, lets <c>DisposeAsync</c> run into it,
/// and inspects the state at that point. This is a deterministic check of the ORDER; it does not reproduce the historical CI incident.
/// </summary>
public sealed class AuthApiFactoryDisposalOrderTests
{
    private const string LocalDb = "Server=(localdb)\\MSSQLLocalDB;TrustServerCertificate=True;Trusted_Connection=True";

    public sealed class PlainProbeFactory : AuthApiFactory
    {
        private readonly string _database = $"RepairRequestDb_DisposalProbe_{Guid.NewGuid():N}";
        private readonly string _gate = $"rr.dp.{Guid.NewGuid():N}"[..26];     // sys.dm_tran_locks shows an application lock's name cut to 32 characters

        public override string ConnectionString => $"{LocalDb};Database={_database}";

        public string Database => _database;

        public string Gate => _gate;

        protected override string LifecycleGateResource => _gate;
    }

    /// <summary>Its drop fails with a real SQL Server error 3702 (DROP DATABASE issued from a session inside the database itself).</summary>
    public sealed class FailingDropProbeFactory : AuthApiFactory
    {
        private readonly string _database = $"RepairRequestDb_DisposalProbe_{Guid.NewGuid():N}";
        private readonly string _gate = $"rr.dp.{Guid.NewGuid():N}"[..26];

        public override string ConnectionString => $"{LocalDb};Database={_database}";

        public string Database => _database;

        protected override string LifecycleGateResource => _gate;

        protected override async Task DropDatabaseAsync(RepairRequestDbContext cleanup)
        {
            await using var inside = new SqlConnection($"{LocalDb};Database={_database};Pooling=False;Application Name=rr-disposal-order-dropper");
            await inside.OpenAsync();
            await using var command = inside.CreateCommand();
            command.CommandText = $"DROP DATABASE [{_database}]";
            await command.ExecuteNonQueryAsync();
        }
    }

    public sealed class RunnerProbeFactory : ScanningApiFactory
    {
        private readonly string _database = $"RepairRequestDb_DisposalProbe_{Guid.NewGuid():N}";
        private readonly string _gate = $"rr.dp.{Guid.NewGuid():N}"[..26];     // sys.dm_tran_locks shows an application lock's name cut to 32 characters

        public override string ConnectionString => $"{LocalDb};Database={_database}";

        public string Database => _database;

        public string Gate => _gate;

        protected override string LifecycleGateResource => _gate;

        protected override string EnvironmentName => DevelopmentMalwareScanning.TestingEnvironmentName;

        protected override bool FakeScanningEnabled => true;
    }

    // ---------------- helpers (all against master, unpooled, read-only) ----------------

    private static SqlConnection Master() => new($"{LocalDb};Database=master;Pooling=False");

    private static async Task<(int Sessions, DateTime? MaxLogin, DateTime? MaxLastRequestEnd)> ActivityAsync(string database)
    {
        await using var connection = Master();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*), MAX(login_time), MAX(last_request_end_time) FROM sys.dm_exec_sessions WHERE database_id = DB_ID(@name) AND session_id <> @@SPID";
        command.Parameters.AddWithValue("@name", database);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetInt32(0), reader.IsDBNull(1) ? null : reader.GetDateTime(1), reader.IsDBNull(2) ? null : reader.GetDateTime(2));
    }

    private static async Task<bool> DatabaseExistsAsync(string database)
    {
        await using var connection = Master();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CASE WHEN DB_ID(@name) IS NULL THEN 0 ELSE 1 END";
        command.Parameters.AddWithValue("@name", database);
        return Convert.ToInt32(await command.ExecuteScalarAsync()) == 1;
    }

    /// <summary>Waits until SQL Server reports a waiter for the application lock of this test's own gate resource (so DisposeAsync is parked at the gate).</summary>
    private static async Task WaitForGateWaiterAsync(string gateResource)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        await using var connection = Master();
        await connection.OpenAsync();
        while (DateTime.UtcNow < deadline)
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT COUNT(*) FROM sys.dm_tran_locks WHERE resource_type = 'APPLICATION' AND request_status = 'WAIT' AND CHARINDEX('[' + @resource + ']', resource_description) > 0";
            command.Parameters.AddWithValue("@resource", gateResource);
            if (Convert.ToInt32(await command.ExecuteScalarAsync()) > 0)
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail($"DisposeAsync never reached the lifecycle gate '{gateResource}'.");
    }

    /// <summary>Lets a pending <c>DisposeAsync</c> finish (the gate is released by then) so a failed assertion is reported as itself and cleanup does not race it.</summary>
    private static async Task SettleAsync(Task? pending)
    {
        if (pending is null)
        {
            return;
        }

        try
        {
            await pending;
        }
        catch
        {
            // the test's own assertion is what gets reported
        }
    }

    private static async Task ForceDropAsync(string database)
    {
        await using var connection = Master();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"IF DB_ID('{database}') IS NOT NULL BEGIN ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]; END";
        await command.ExecuteNonQueryAsync();
    }

    // ---------------- the order: host stopped before the drop, nothing of the fixture touches the database afterwards ----------------

    [Fact]
    public async Task OrdinaryFactory_StopsItsHostBeforeTheDropIsEvenAttempted()
    {
        var factory = new PlainProbeFactory();
        Task? dispose = null;
        try
        {
            await factory.InitializeAsync();
            var lifetime = factory.Services.GetRequiredService<IHostApplicationLifetime>();
            Assert.False(lifetime.ApplicationStopped.IsCancellationRequested);
            Assert.True(await DatabaseExistsAsync(factory.Database));

            // Park DisposeAsync at the gate, then look at the state at exactly that point.
            await using var gate = await LocalDbLifecycleGate.EnterAsync(factory.ConnectionString, factory.Gate);
            dispose = ((IAsyncLifetime)factory).DisposeAsync();
            await WaitForGateWaiterAsync(factory.Gate);

            Assert.True(lifetime.ApplicationStopped.IsCancellationRequested, "The host must be stopped before the drop (and before waiting for the gate).");
            Assert.True(await DatabaseExistsAsync(factory.Database), "The database must still exist while DisposeAsync waits for the gate.");

            await gate.DisposeAsync();
            await dispose;
            Assert.False(await DatabaseExistsAsync(factory.Database));
        }
        finally
        {
            await SettleAsync(dispose);
            await ForceDropAsync(factory.Database);
        }
    }

    [Fact]
    public async Task RunnerHostingFactory_StopsTheRunner_BeforeTheDrop_AndTheRunnerCannotReopenConnectionsAfterwards()
    {
        var factory = new RunnerProbeFactory();
        Task? dispose = null;
        try
        {
            await factory.InitializeAsync();
            var lifetime = factory.Services.GetRequiredService<IHostApplicationLifetime>();
            var runner = factory.Services.GetServices<IHostedService>().OfType<DevelopmentOnlyPendingScanRunner>().Single();

            // Positive control: while the host runs, the runner's 200 ms polling keeps the database busy (the last request time of its sessions advances).
            var running = await ActivityAsync(factory.Database);
            await Task.Delay(800);
            var stillRunning = await ActivityAsync(factory.Database);
            Assert.True(stillRunning.MaxLastRequestEnd > running.MaxLastRequestEnd, "Positive control failed: the runner is not visibly polling the database.");
            Assert.False(runner.ExecuteTask!.IsCompleted);

            await using var gate = await LocalDbLifecycleGate.EnterAsync(factory.ConnectionString, factory.Gate);
            dispose = ((IAsyncLifetime)factory).DisposeAsync();
            await WaitForGateWaiterAsync(factory.Gate);

            // At the gate: the host is stopped, the runner's loop has finished ...
            Assert.True(lifetime.ApplicationStopped.IsCancellationRequested);
            Assert.True(runner.ExecuteTask!.IsCompleted, "The runner must have been stopped and awaited before the drop.");

            // ... and it cannot reopen connections: over more than three poll intervals there is no new session, no newer login and no newer request.
            // (Idle pooled sessions may disappear meanwhile, for example when another fixture of this process drops its database; that is not activity.)
            var afterShutdown = await ActivityAsync(factory.Database);
            await Task.Delay(800);
            var later = await ActivityAsync(factory.Database);
            Assert.True(later.Sessions <= afterShutdown.Sessions, "A session was opened after the host had stopped.");
            Assert.True(later.MaxLogin is null || later.MaxLogin <= afterShutdown.MaxLogin, "A login happened after the host had stopped.");
            Assert.True(later.MaxLastRequestEnd is null || later.MaxLastRequestEnd <= afterShutdown.MaxLastRequestEnd, "A request ran after the host had stopped.");

            await gate.DisposeAsync();
            await dispose;
            Assert.False(await DatabaseExistsAsync(factory.Database));
        }
        finally
        {
            await SettleAsync(dispose);
            await ForceDropAsync(factory.Database);
        }
    }

    // ---------------- a failed drop still propagates, with the diagnostics, after the host was stopped ----------------

    [Fact]
    public async Task AFailedDrop_StillThrowsTheOriginalError3702_WithTheDiagnostics_AfterTheHostWasStopped()
    {
        var factory = new FailingDropProbeFactory();
        var originalOut = Console.Out;
        var captured = new StringWriter();
        try
        {
            await factory.InitializeAsync();
            var lifetime = factory.Services.GetRequiredService<IHostApplicationLifetime>();

            await using var holder = new SqlConnection($"{LocalDb};Database={factory.Database};Pooling=False;Application Name=rr-disposal-order-holder");
            await holder.OpenAsync();

            Console.SetOut(captured);
            var thrown = await Assert.ThrowsAsync<SqlException>(() => ((IAsyncLifetime)factory).DisposeAsync());
            Console.SetOut(originalOut);

            Assert.Equal(3702, thrown.Number);                                                       // the original error, not a diagnostic one
            Assert.True(lifetime.ApplicationStopped.IsCancellationRequested, "The host was stopped before the failed drop.");
            var text = captured.ToString();
            Assert.Contains("[FIXTURE-DROP-DIAGNOSTICS]", text, StringComparison.Ordinal);
            Assert.Contains("fixture=FailingDropProbeFactory", text, StringComparison.Ordinal);
            Assert.Contains($"database={factory.Database}", text, StringComparison.Ordinal);
            Assert.Contains("error=3702", text, StringComparison.Ordinal);
            Assert.Contains("program_name=rr-disposal-order-holder", text, StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(originalOut);
            await ForceDropAsync(factory.Database);
        }
    }

    // ---------------- what EF Core's drop does to sessions that already exist ----------------

    [Fact]
    public async Task EfCoreDrop_KillsSessionsThatAlreadyExist_SoOnlyAConnectionMadeAfterItsAlterCanBlockTheDrop()
    {
        var database = $"RepairRequestDb_DisposalProbe_{Guid.NewGuid():N}";
        var connectionString = $"{LocalDb};Database={database};Pooling=False;Application Name=rr-disposal-order-existing";
        try
        {
            await using (var create = new RepairRequestDbContext(new DbContextOptionsBuilder<RepairRequestDbContext>().UseSqlServer(connectionString).Options))
            {
                await create.Database.EnsureCreatedAsync();
            }

            // A session that is connected to the database (not pooled, so it stays) when the drop starts.
            await using var existing = new SqlConnection(connectionString);
            await existing.OpenAsync();
            Assert.True((await ActivityAsync(database)).Sessions > 0);

            // EF Core's drop is "ALTER DATABASE ... SET SINGLE_USER WITH ROLLBACK IMMEDIATE" followed by "DROP DATABASE": the existing session is
            // disconnected by the first statement, so the drop succeeds. A 3702 therefore needs a connection that is (re)established after that ALTER.
            await using var cleanup = new RepairRequestDbContext(new DbContextOptionsBuilder<RepairRequestDbContext>().UseSqlServer(connectionString).Options);
            await cleanup.Database.EnsureDeletedAsync();

            Assert.False(await DatabaseExistsAsync(database));
            await using var probe = existing.CreateCommand();
            probe.CommandText = "SELECT 1";
            await Assert.ThrowsAnyAsync<Exception>(() => probe.ExecuteScalarAsync());     // the existing session was killed
        }
        finally
        {
            await ForceDropAsync(database);
        }
    }
}
