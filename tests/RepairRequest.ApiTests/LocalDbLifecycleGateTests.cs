using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RepairRequest.Infrastructure.Persistence;

namespace RepairRequest.ApiTests;

/// <summary>
/// The LocalDB lifecycle gate (see <see cref="LocalDbLifecycleGate"/>): what the API test factories rely on so that databases are never
/// created, migrated or dropped concurrently on one instance. Every assertion is deterministic: mutual exclusion is proven with an explicit
/// in-gate counter, not with timing.
/// </summary>
public sealed class LocalDbLifecycleGateTests
{
    private const string Server = "Server=(localdb)\\MSSQLLocalDB;Database=RepairRequestDb_GateProbe;Trusted_Connection=True;TrustServerCertificate=True";

    /// <summary>A resource of its own, so these tests never wait for (or delay) the factories of other test classes.</summary>
    private static string NewResource() => $"RepairRequest.ApiTests.GateTest.{Guid.NewGuid():N}";

    [Fact]
    public async Task OnlyOneHolderIsInsideTheGateAtATime_WhateverTheNumberOfConcurrentCallers()
    {
        var resource = NewResource();
        var inside = 0;
        var maxInside = 0;
        var completed = 0;

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            await using (await LocalDbLifecycleGate.EnterAsync(Server, resource))
            {
                var now = Interlocked.Increment(ref inside);
                InterlockedMax(ref maxInside, now);
                await Task.Delay(75); // an await inside the gate may resume on another thread: the lock is session-owned, not thread-owned
                Interlocked.Decrement(ref inside);
                Interlocked.Increment(ref completed);
            }
        })));

        Assert.Equal(8, completed);
        Assert.Equal(1, maxInside);
        Assert.Equal(0, inside);
    }

    [Fact]
    public async Task TheGateIsRefusedWhileHeld_NeverSilentlySkipped_AndIsGrantedAgainAfterItIsReleased()
    {
        var resource = NewResource();
        var holder = await LocalDbLifecycleGate.EnterAsync(Server, resource);

        var exception = await Assert.ThrowsAsync<TimeoutException>(() => LocalDbLifecycleGate.EnterAsync(Server, resource, TimeSpan.FromMilliseconds(250)));
        Assert.Contains(resource, exception.Message, StringComparison.Ordinal);

        await holder.DisposeAsync();
        await holder.DisposeAsync(); // releasing twice is harmless

        await using (await LocalDbLifecycleGate.EnterAsync(Server, resource, TimeSpan.FromSeconds(10)))
        {
        }
    }

    [Fact]
    public async Task DifferentResourcesDoNotBlockEachOther()
    {
        await using var first = await LocalDbLifecycleGate.EnterAsync(Server, NewResource());
        await using var second = await LocalDbLifecycleGate.EnterAsync(Server, NewResource(), TimeSpan.FromSeconds(5));
    }

    /// <summary>The factories' real lifecycle: concurrent initialization and disposal of several databases succeeds, one step at a time, and leaves nothing behind.</summary>
    [Fact]
    public async Task SeveralFactoriesStartedAtOnce_CreateAndDropTheirDatabases_WithoutAnyFailure()
    {
        var factories = Enumerable.Range(0, 4).Select(_ => new GateProbeFactory()).ToList();
        try
        {
            var started = await Task.WhenAll(factories.Select(factory => Task.Run<Exception?>(async () =>
            {
                try
                {
                    await ((IAsyncLifetime)factory).InitializeAsync();
                    return null;
                }
                catch (Exception exception)
                {
                    return exception;
                }
            })));
            Assert.All(started, failure => Assert.Null(failure));

            foreach (var factory in factories)
            {
                await using var scope = factory.Services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<RepairRequestDbContext>();
                Assert.True(await db.Database.CanConnectAsync());
                Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            }
        }
        finally
        {
            await Task.WhenAll(factories.Select(factory => Task.Run(() => ((IAsyncLifetime)factory).DisposeAsync())));
        }

        foreach (var factory in factories)
        {
            Assert.False(await DatabaseExistsAsync(factory.DatabaseName));
        }
    }

    private static async Task<bool> DatabaseExistsAsync(string name)
    {
        var builder = new SqlConnectionStringBuilder(Server) { InitialCatalog = "master", Pooling = false };
        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sys.databases WHERE name = @name";
        command.Parameters.AddWithValue("@name", name);
        return (int)(await command.ExecuteScalarAsync())! > 0;
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }

    private sealed class GateProbeFactory : AuthApiFactory
    {
        public string DatabaseName { get; } = $"RepairRequestDb_GateProbe_{Guid.NewGuid():N}";

        public override string ConnectionString =>
            $"Server=(localdb)\\MSSQLLocalDB;Database={DatabaseName};Trusted_Connection=True;TrustServerCertificate=True";
    }
}
