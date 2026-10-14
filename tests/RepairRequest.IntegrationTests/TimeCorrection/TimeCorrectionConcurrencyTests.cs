using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RepairRequest.Application.Common;
using RepairRequest.Application.WorkOrders;
using RepairRequest.Domain.WorkOrders;
using RepairRequest.Infrastructure.Persistence;
using RepairRequest.IntegrationTests.Authentication;
using RepairRequest.IntegrationTests.Persistence;
using RepairRequest.IntegrationTests.WorkEvidence;

namespace RepairRequest.IntegrationTests.TimeCorrection;

/// <summary>
/// The race orders of Time Correction, proven against SQL Server with independent connections (`docs/17` P9, owner approval of
/// 13 October 2026). Nothing here is timing based: a command is held <em>after</em> it holds its locks (a command interceptor), a competing
/// command is started on another connection, and the test waits until SQL Server itself reports the competitor as blocked before it lets
/// the first one finish. Both orders of Approve against Close/Cancel, against Pause/Resume/Check-out, and between two Approves and between
/// an Approve and a Reject are covered.
/// </summary>
[Collection(PersistenceDatabaseCollection.Name)]
public sealed class TimeCorrectionConcurrencyTests : IAsyncLifetime
{
    private readonly AuthenticationTestHost _plainHost = new();
    private readonly List<AuthenticationTestHost> _hosts = [];
    private readonly TimeCorrectionDriver _plain;

    public TimeCorrectionConcurrencyTests(PersistenceDatabaseFixture database)
    {
        _ = database;
        _plain = new TimeCorrectionDriver(_plainHost);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _plainHost.DisposeAsync();
        foreach (var host in _hosts)
        {
            await host.DisposeAsync();
        }
    }

    /// <summary>A host whose first command containing <paramref name="match"/> is held (after its locks are taken) until released.</summary>
    private (TimeCorrectionDriver Driver, BlockOnCommandInterceptor Gate, AuthenticationTestHost Host) HeldHost(string match)
    {
        var gate = new BlockOnCommandInterceptor(match);
        var host = new AuthenticationTestHost(services => services.ConfigureDbContext<RepairRequestDbContext>(options => options.AddInterceptors(gate)));
        _hosts.Add(host);
        return (new TimeCorrectionDriver(host), gate, host);
    }

    private static async Task ReachedAsync(BlockOnCommandInterceptor gate) =>
        Assert.True(await gate.Reached.WaitAsync(TimeSpan.FromSeconds(60)), "The held command was never reached.");

    private static async Task<int> RawAsync(string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return await command.ExecuteNonQueryAsync();
    }

    // ---------------- Approve against Close / Cancel ----------------

    [Fact]
    public async Task CloseHoldingTheWorkOrderFirst_MakesApproveWait_ThenApproveSeesClosedAndIsRefused()
    {
        var world = await TimeCorrectionWorlds.CreateAsync(_plainHost);
        var request = await _plain.RequestAsync(world.Base.TeamLead, world.SessionId, "CHECK_IN", world.At(-5));
        var facts = await _plain.SessionAsync(world.SessionId);
        var connectionString = await _plain.ConnectionStringAsync();

        // An independent connection is in the middle of a Close: it has written the row and not committed.
        await using var closer = new SqlConnection(connectionString);
        await closer.OpenAsync();
        await using var transaction = (SqlTransaction)await closer.BeginTransactionAsync();
        await using (var update = closer.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE [work_order] SET [status] = 'CLOSED' WHERE [work_order_id] = @id";
            update.Parameters.AddWithValue("@id", world.WorkOrderId);
            Assert.Equal(1, await update.ExecuteNonQueryAsync());
        }

        var approve = Task.Run(() => _plain.ApproveAsync(world.Base.Supervisor, request.Value));
        await SqlProbes.WaitForBlockedRequestsAsync(connectionString);
        Assert.False(approve.IsCompleted);                                    // the Approve is waiting for the Close, not racing past it

        await transaction.CommitAsync();
        var result = await approve;

        Assert.Equal(CommandFailure.StateConflict, result.Error?.Failure);
        Assert.Equal(facts, await _plain.SessionAsync(world.SessionId));
        Assert.Equal(TimeCorrectionStatus.Pending, await _plain.WithDbAsync(db => db.TimeCorrections.AsNoTracking().Where(item => item.Id == request.Value).Select(item => item.Status).SingleAsync()));
        Assert.Equal(0, await _plain.AuditCountAsync(world.TenantId, WorkOrderAudit.TimeCorrectionApprovedAction));
    }

    [Fact]
    public async Task ApproveInFlight_MakesCloseWait_ThenBothCommit_AndTheCloseKeepsItsOriginalRowVersion()
    {
        var world = await TimeCorrectionWorlds.CreateAsync(_plainHost);
        var request = await _plain.RequestAsync(world.Base.TeamLead, world.SessionId, "CHECK_IN", world.At(-5));
        var (held, gate, _) = HeldHost("UPDATE [time_correction]");
        var connectionString = await _plain.ConnectionStringAsync();
        var workOrderVersion = await _plain.WorkOrderRowVersionAsync(world.WorkOrderId);

        var approve = Task.Run(() => held.ApproveAsync(world.Base.Supervisor, request.Value));
        await ReachedAsync(gate);                                             // the Approve holds every lock and has not written yet

        var close = Task.Run(() => RawAsync(
            connectionString,
            "UPDATE [work_order] SET [status] = 'CLOSED' WHERE [work_order_id] = @id AND [row_version] = @version",
            ("@id", world.WorkOrderId),
            ("@version", workOrderVersion)));
        await SqlProbes.WaitForBlockedRequestsAsync(connectionString);
        Assert.False(close.IsCompleted);                                      // the Close waits for the Approve

        gate.Release.Release();
        var approved = await approve;
        Assert.True(approved.Succeeded, approved.Error?.Message);
        Assert.Equal(1, await close);                                         // Approve never touched the Work Order row, so the Close's token still matches

        Assert.Equal(world.At(-5), (await _plain.SessionAsync(world.SessionId)).CheckIn);
        Assert.Equal(WorkOrderStatus.Closed, await _plain.WithDbAsync(db => db.WorkOrders.AsNoTracking().Where(item => item.Id == world.WorkOrderId).Select(item => item.Status).SingleAsync()));
    }

    [Fact]
    public async Task ApproveInFlight_MakesARealCancelWait_ThenTheCancelSucceedsOnItsOriginalToken()
    {
        var world = await TimeCorrectionWorlds.CreateAsync(_plainHost);
        var request = await _plain.RequestAsync(world.Base.TeamLead, world.SessionId, "CHECK_OUT", world.At(55));
        var (held, gate, _) = HeldHost("UPDATE [time_correction]");
        var connectionString = await _plain.ConnectionStringAsync();
        var workOrderVersion = await _plain.WorkOrderRowVersionAsync(world.WorkOrderId);

        var approve = Task.Run(() => held.ApproveAsync(world.Base.Supervisor, request.Value));
        await ReachedAsync(gate);

        var cancel = Task.Run(async () =>
        {
            await using var scope = _plainHost.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<WorkOrderCancelService>().CancelAsync(
                new CommandContext(world.Base.Supervisor, Guid.NewGuid()), world.WorkOrderId, workOrderVersion, "Customer withdrew the order.", CancellationToken.None);
        });
        await SqlProbes.WaitForBlockedRequestsAsync(connectionString);
        Assert.False(cancel.IsCompleted);

        gate.Release.Release();
        Assert.True((await approve).Succeeded);
        var cancelled = await cancel;
        Assert.True(cancelled.Succeeded, cancelled.Error?.Message);

        Assert.Equal(world.At(55), (await _plain.SessionAsync(world.SessionId)).CheckOut);
        Assert.Equal(WorkOrderStatus.Cancelled, await _plain.WithDbAsync(db => db.WorkOrders.AsNoTracking().Where(item => item.Id == world.WorkOrderId).Select(item => item.Status).SingleAsync()));
    }

    [Fact]
    public async Task ACancelCommittedFirst_MeansTheApproveIsRefused_ButTheRequestCanStillBeRejected()
    {
        var world = await TimeCorrectionWorlds.CreateAsync(_plainHost);
        var toApprove = await _plain.RequestAsync(world.Base.TeamLead, world.SessionId, "CHECK_IN", world.At(-5));
        var toReject = await _plain.RequestAsync(world.Base.TeamLead, world.SessionId, "CHECK_OUT", world.At(55));
        var facts = await _plain.SessionAsync(world.SessionId);

        await using (var scope = _plainHost.CreateScope())
        {
            var cancelled = await scope.ServiceProvider.GetRequiredService<WorkOrderCancelService>().CancelAsync(
                new CommandContext(world.Base.Supervisor, Guid.NewGuid()), world.WorkOrderId, await _plain.WorkOrderRowVersionAsync(world.WorkOrderId), "Customer withdrew the order.", CancellationToken.None);
            Assert.True(cancelled.Succeeded, cancelled.Error?.Message);
        }

        Assert.Equal(CommandFailure.StateConflict, (await _plain.ApproveAsync(world.Base.Supervisor, toApprove.Value)).Error?.Failure);
        Assert.True((await _plain.RejectAsync(world.Base.Supervisor, toReject.Value)).Succeeded);
        Assert.Equal(facts, await _plain.SessionAsync(world.SessionId));
    }

    // ---------------- Approve against Pause / Resume / Check-out ----------------

    [Fact]
    public async Task ApproveInFlight_MakesAConcurrentPauseWait_ThenThePauseHoldsAStaleToken_AndConflicts()
    {
        // A live (CHECKED_IN) session: the Technician can still Pause it.
        var live = await EvidenceWorlds.CreateAsync(_plainHost, checkedOut: false);
        var request = await _plain.RequestAsync(live.TeamLead, live.SessionId, "CHECK_IN", await CheckInOfAsync(live.SessionId) - TimeSpan.FromMinutes(5));
        Assert.True(request.Succeeded, request.Error?.Message);

        var (held, gate, _) = HeldHost("UPDATE [time_correction]");
        var connectionString = await _plain.ConnectionStringAsync();
        var sessionToken = await _plain.SessionRowVersionAsync(live.SessionId);

        var approve = Task.Run(() => held.ApproveAsync(live.Supervisor, request.Value));
        await ReachedAsync(gate);

        var pause = Task.Run(async () =>
        {
            await using var scope = _plainHost.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<WorkSessionService>().PauseAsync(
                new CommandContext(live.Technician, Guid.NewGuid()), live.SessionId, sessionToken, "Waiting for a part", CancellationToken.None);
        });
        await SqlProbes.WaitForBlockedRequestsAsync(connectionString);
        Assert.False(pause.IsCompleted);

        gate.Release.Release();
        Assert.True((await approve).Succeeded);
        var paused = await pause;

        // The Approve rewrote the session row, so the Technician's earlier token is stale: nothing was paused.
        Assert.Equal(CommandFailure.ConcurrencyConflict, paused.Error?.Failure);
        var session = await _plain.SessionAsync(live.SessionId);
        Assert.Equal(WorkSessionStatus.CheckedIn, session.Status);
        Assert.Equal(0, await _plain.WithDbAsync(db => db.WorkSessionPauses.CountAsync(item => item.WorkSessionId == live.SessionId)));
        Assert.True(session.CheckIn < await CheckInBeforeAsync(live.SessionId, request.Value));
    }

    [Fact]
    public async Task APauseCommittedFirst_IsSeenByTheApprove_WhichValidatesAgainstTheFreshIntervals()
    {
        var live = await EvidenceWorlds.CreateAsync(_plainHost, checkedOut: false);
        var checkIn = await CheckInOfAsync(live.SessionId);
        var request = await _plain.RequestAsync(live.TeamLead, live.SessionId, "CHECK_IN", checkIn - TimeSpan.FromMinutes(5));
        Assert.True(request.Succeeded, request.Error?.Message);

        await using (var scope = _plainHost.CreateScope())
        {
            var paused = await scope.ServiceProvider.GetRequiredService<WorkSessionService>().PauseAsync(
                new CommandContext(live.Technician, Guid.NewGuid()), live.SessionId, await _plain.SessionRowVersionAsync(live.SessionId), "Waiting for a part", CancellationToken.None);
            Assert.True(paused.Succeeded, paused.Error?.Message);
        }

        var approved = await _plain.ApproveAsync(live.Supervisor, request.Value);

        Assert.True(approved.Succeeded, approved.Error?.Message);
        var session = await _plain.SessionAsync(live.SessionId);
        Assert.Equal(checkIn - TimeSpan.FromMinutes(5), session.CheckIn);
        Assert.Equal(WorkSessionStatus.Paused, session.Status);              // the new pause survived; nothing was overwritten
        Assert.NotNull(session.PauseStart);
    }

    // ---------------- A decided correction invalidates the session tokens that were read before it ----------------

    [Fact]
    public async Task AfterAnApprovedCorrection_AStaleResumeToken_IsRefusedWithoutResuming()
    {
        var live = await EvidenceWorlds.CreateAsync(_plainHost, checkedOut: false);
        var technician = new CommandContext(live.Technician, Guid.NewGuid());
        await using (var scope = _plainHost.CreateScope())
        {
            var paused = await scope.ServiceProvider.GetRequiredService<WorkSessionService>().PauseAsync(
                technician, live.SessionId, await _plain.SessionRowVersionAsync(live.SessionId), "Waiting for a part", CancellationToken.None);
            Assert.True(paused.Succeeded, paused.Error?.Message);
        }

        var staleToken = await _plain.SessionRowVersionAsync(live.SessionId);        // what the Technician's screen holds
        var request = await _plain.RequestAsync(live.TeamLead, live.SessionId, "CHECK_IN", await CheckInOfAsync(live.SessionId) - TimeSpan.FromMinutes(5));
        Assert.True(request.Succeeded, request.Error?.Message);
        Assert.True((await _plain.ApproveAsync(live.Supervisor, request.Value)).Succeeded);

        await using var resumeScope = _plainHost.CreateScope();
        var resumed = await resumeScope.ServiceProvider.GetRequiredService<WorkSessionService>().ResumeAsync(
            new CommandContext(live.Technician, Guid.NewGuid()), live.SessionId, staleToken, CancellationToken.None);

        Assert.Equal(CommandFailure.ConcurrencyConflict, resumed.Error?.Failure);
        var session = await _plain.SessionAsync(live.SessionId);
        Assert.Equal(WorkSessionStatus.Paused, session.Status);
        Assert.Null(session.Resume);
        Assert.Equal(0, await _plain.WithDbAsync(db => db.WorkSessionPauses.CountAsync(item => item.WorkSessionId == live.SessionId && item.ResumedAt != null)));
    }

    [Fact]
    public async Task AfterAnApprovedCorrection_AStaleCheckOutToken_IsRefusedWithoutCheckingOut()
    {
        var live = await EvidenceWorlds.CreateAsync(_plainHost, checkedOut: false);
        var staleToken = await _plain.SessionRowVersionAsync(live.SessionId);
        var request = await _plain.RequestAsync(live.TeamLead, live.SessionId, "CHECK_IN", await CheckInOfAsync(live.SessionId) - TimeSpan.FromMinutes(5));
        Assert.True(request.Succeeded, request.Error?.Message);
        Assert.True((await _plain.ApproveAsync(live.Supervisor, request.Value)).Succeeded);

        await using var scope = _plainHost.CreateScope();
        var checkedOut = await scope.ServiceProvider.GetRequiredService<WorkSessionService>().CheckOutAsync(
            new CommandContext(live.Technician, Guid.NewGuid()), live.SessionId, staleToken, CancellationToken.None);

        Assert.Equal(CommandFailure.ConcurrencyConflict, checkedOut.Error?.Failure);
        var session = await _plain.SessionAsync(live.SessionId);
        Assert.Equal(WorkSessionStatus.CheckedIn, session.Status);
        Assert.Null(session.CheckOut);
        Assert.Null(await _plain.VisitCompletedAtAsync(live.VisitId));
    }

    // ---------------- Approve against Approve, and against Reject ----------------

    [Fact]
    public async Task TwoApprovesOnTheSameSession_SerializeOnTheLocks_AndBothApply()
    {
        var world = await TimeCorrectionWorlds.CreateAsync(_plainHost);
        var first = await _plain.RequestAsync(world.Base.TeamLead, world.SessionId, "CHECK_IN", world.At(-5));
        var second = await _plain.RequestAsync(world.Base.TeamLead, world.SessionId, "PAUSE_START", world.At(12), world.Pause1);
        var (held, gate, _) = HeldHost("UPDATE [time_correction]");
        var connectionString = await _plain.ConnectionStringAsync();

        var approveFirst = Task.Run(() => held.ApproveAsync(world.Base.Supervisor, first.Value));
        await ReachedAsync(gate);
        var approveSecond = Task.Run(() => _plain.ApproveAsync(world.Base.Supervisor, second.Value));
        await SqlProbes.WaitForBlockedRequestsAsync(connectionString);
        Assert.False(approveSecond.IsCompleted);                              // the second Approve waits for the first one's locks

        gate.Release.Release();
        Assert.True((await approveFirst).Succeeded);
        var secondResult = await approveSecond;
        Assert.True(secondResult.Succeeded, secondResult.Error?.Message);

        Assert.Equal(world.At(-5), (await _plain.SessionAsync(world.SessionId)).CheckIn);          // no lost update
        Assert.Equal(world.At(12), (await _plain.PauseAsync(world.Pause1)).PausedAt);
        Assert.Equal(2, await _plain.AuditCountAsync(world.TenantId, WorkOrderAudit.TimeCorrectionApprovedAction));
    }

    [Fact]
    public async Task AnApproveRacingARejectOfTheSameCorrection_HasExactlyOneWinner_AndALosingApproveAppliesNothing()
    {
        var world = await TimeCorrectionWorlds.CreateAsync(_plainHost);
        var request = await _plain.RequestAsync(world.Base.TeamLead, world.SessionId, "CHECK_IN", world.At(-5));
        var token = await _plain.RowVersionAsync(request.Value);
        var (held, gate, _) = HeldHost("UPDATE [time_correction]");
        var facts = await _plain.SessionAsync(world.SessionId);

        var approve = Task.Run(() => held.ApproveAsync(world.Base.Supervisor, request.Value, token));
        await ReachedAsync(gate);                                             // the Approve has validated and applied in memory, and has not written

        var reject = await _plain.RejectAsync(world.Base.Supervisor, request.Value, token);
        Assert.True(reject.Succeeded, reject.Error?.Message);                 // the Reject takes the correction first

        gate.Release.Release();
        var approved = await approve;

        Assert.Equal(CommandFailure.ConcurrencyConflict, approved.Error?.Failure);
        Assert.Equal(facts, await _plain.SessionAsync(world.SessionId));      // the losing Approve's changes were rolled back with its transaction
        Assert.Equal(TimeCorrectionStatus.Rejected, await _plain.WithDbAsync(db => db.TimeCorrections.AsNoTracking().Where(item => item.Id == request.Value).Select(item => item.Status).SingleAsync()));
        Assert.Equal(0, await _plain.AuditCountAsync(world.TenantId, WorkOrderAudit.TimeCorrectionApprovedAction));
        Assert.Equal(1, await _plain.AuditCountAsync(world.TenantId, WorkOrderAudit.TimeCorrectionRejectedAction));
    }

    [Fact]
    public async Task ManyConcurrentApprovesOfOneCorrection_ApplyItExactlyOnce()
    {
        var world = await TimeCorrectionWorlds.CreateAsync(_plainHost);
        var request = await _plain.RequestAsync(world.Base.TeamLead, world.SessionId, "CHECK_IN", world.At(-5));
        var token = await _plain.RowVersionAsync(request.Value);

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => _plain.ApproveAsync(world.Base.Supervisor, request.Value, token))));

        Assert.Equal(1, results.Count(result => result.Succeeded));
        Assert.All(results.Where(result => !result.Succeeded), result =>
            Assert.True(result.Error?.Failure is CommandFailure.ConcurrencyConflict or CommandFailure.StateConflict, result.Error?.Failure.ToString()));
        Assert.Equal(1, await _plain.AuditCountAsync(world.TenantId, WorkOrderAudit.TimeCorrectionApprovedAction));
        Assert.Equal(world.At(-5), (await _plain.SessionAsync(world.SessionId)).CheckIn);
    }

    // ---------------- helpers ----------------

    private Task<DateTime> CheckInOfAsync(Guid sessionId) =>
        _plain.WithDbAsync(db => db.WorkSessions.AsNoTracking().Where(item => item.Id == sessionId).Select(item => item.CheckInAt).SingleAsync());

    private Task<DateTime> CheckInBeforeAsync(Guid sessionId, Guid correctionId) =>
        _plain.WithDbAsync(db => db.TimeCorrections.AsNoTracking().Where(item => item.Id == correctionId).Select(item => item.OldValue).SingleAsync());
}
