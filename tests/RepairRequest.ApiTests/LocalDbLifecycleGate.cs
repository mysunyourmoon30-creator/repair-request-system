using Microsoft.Data.SqlClient;

namespace RepairRequest.ApiTests;

/// <summary>
/// Serializes the create/migrate and drop of the per-class API test databases on one LocalDB instance.
/// <para>
/// Every API test class owns a database (unique name) that its factory deletes, creates and migrates on start and drops on dispose. xUnit starts
/// as many classes at once as the machine has processors (4 on the CI runner), so up to four <c>CREATE DATABASE</c> + migration batches run at
/// the same time against one instance. <c>CREATE DATABASE</c> needs an exclusive lock on <c>model</c> and every migration batch flushes its
/// log: on the slow runner disk the post-merge runs of 12 October 2026 failed in exactly those steps — twelve fixtures with
/// "Could not obtain exclusive lock on database 'model'", and, in the second attempt, a <c>CREATE DATABASE</c> timeout, a commit timeout and a
/// login request that stalled for 22 s at the same moment. Holding this gate for the lifecycle step lets only one database be created, migrated or
/// dropped at a time; the tests themselves still run in parallel.
/// </para>
/// The gate is an SQL Server application lock held by a dedicated, unpooled connection to <c>master</c> — so it works across threads (an
/// <c>await</c> may resume anywhere) and across test processes on the same instance, and a crashed holder releases it when its connection drops.
/// </summary>
internal static class LocalDbLifecycleGate
{
    internal const string Resource = "RepairRequest.ApiTests.LocalDbLifecycle";

    private static readonly TimeSpan DefaultWait = TimeSpan.FromMinutes(5);

    /// <summary>Waits for the gate (bounded, never silently skipped) and returns the handle that releases it.</summary>
    public static async Task<IAsyncDisposable> EnterAsync(string databaseConnectionString, string resource = Resource, TimeSpan? wait = null)
    {
        var timeout = wait ?? DefaultWait;
        var builder = new SqlConnectionStringBuilder(databaseConnectionString)
        {
            InitialCatalog = "master",
            Pooling = false,
            ConnectTimeout = 60
        };

        var connection = new SqlConnection(builder.ConnectionString);
        try
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DECLARE @result int; EXEC @result = sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = @timeout; SELECT @result;";
            command.Parameters.AddWithValue("@resource", resource);
            command.Parameters.AddWithValue("@timeout", (int)timeout.TotalMilliseconds);
            // The command must outlive the lock wait, otherwise the client would give up first.
            command.CommandTimeout = (int)timeout.TotalSeconds + 60;

            var result = (int)(await command.ExecuteScalarAsync())!;
            if (result < 0)
            {
                throw new TimeoutException($"The LocalDB lifecycle gate '{resource}' was not granted within {timeout} (sp_getapplock returned {result}).");
            }

            return new Handle(connection);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private sealed class Handle : IAsyncDisposable
    {
        private SqlConnection? _connection;

        public Handle(SqlConnection connection) => _connection = connection;

        public async ValueTask DisposeAsync()
        {
            // Closing the session releases a session-owned application lock.
            var connection = Interlocked.Exchange(ref _connection, null);
            if (connection is not null)
            {
                await connection.DisposeAsync();
            }
        }
    }
}
