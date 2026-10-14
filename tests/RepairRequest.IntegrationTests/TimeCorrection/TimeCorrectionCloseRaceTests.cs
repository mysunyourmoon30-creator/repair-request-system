using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RepairRequest.Application.Common;
using RepairRequest.Application.Sla;
using RepairRequest.Application.WorkOrders;
using RepairRequest.Domain.Auditing;
using RepairRequest.Domain.Sla;
using RepairRequest.Domain.WorkOrders;
using RepairRequest.Infrastructure.Persistence;
using RepairRequest.IntegrationTests.Authentication;
using RepairRequest.IntegrationTests.Persistence;
using TimeCorrectionEntity = RepairRequest.Domain.WorkOrders.TimeCorrection;

namespace RepairRequest.IntegrationTests.TimeCorrection;

/// <summary>
/// Approve of a Time Correction against the <em>real</em> Work Order Close (`WorkOrderCloseService`, its real transaction, SLA stop and audit),
/// on a COMPLETED Work Order that satisfies every Close prerequisite, with independent connections (`docs/17` P9). Nothing is timing based:
/// the first command is held at a point where it already holds every lock it needs, the competitor is started on another connection, and the
/// test waits until SQL Server itself reports the competitor as blocked on a lock of the intended operation before the first one is released.
/// The rollback test shows that a failure inside the Approve transaction leaves nothing behind and is not turned into a conflict.
/// </summary>
[Collection(PersistenceDatabaseCollection.Name)]
public sealed class TimeCorrectionCloseRaceTests : IAsyncLifetime
{
    private readonly AuthenticationTestHost _plainHost = new();
    private readonly List<AuthenticationTestHost> _hosts = [];
    private readonly TimeCorrectionDriver _plain;

    public TimeCorrectionCloseRaceTests(PersistenceDatabaseFixture database)
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

    private AuthenticationTestHost HostWith(Action<DbContextOptionsBuilder> configure)
    {
        var host = new AuthenticationTestHost(services => services.ConfigureDbContext<RepairRequestDbContext>(configure));
        _hosts.Add(host);
        return host;
    }

    private static async Task<CommandResult<Guid>> CloseAsync(AuthenticationTestHost host, TimeCorrectionWorld world, byte[] workOrderRowVersion)
    {
        await using var scope = host.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WorkOrderCloseService>().CloseAsync(
            new CommandContext(world.Base.Supervisor, Guid.NewGuid()), world.WorkOrderId, workOrderRowVersion, CancellationToken.None);
    }

    private Task<WorkOrderStatus> WorkOrderStatusAsync(Guid workOrderId) =>
        _plain.WithDbAsync(db => db.WorkOrders.AsNoTracking().Where(item => item.Id == workOrderId).Select(item => item.Status).SingleAsync());

    private Task<List<AuditHistory>> AuditsAsync(Guid tenantId, string actionCode) =>
        _plain.WithDbAsync(db => db.AuditHistory.AsNoTracking().Where(audit => audit.TenantId == tenantId && audit.ActionCode == actionCode).ToListAsync());

    private Task<TimeCorrectionEntity> CorrectionAsync(Guid correctionId) =>
        _plain.WithDbAsync(db => db.TimeCorrections.AsNoTracking().SingleAsync(item => item.Id == correctionId));

    // ---------------- Order A: Close holds the Work Order first ----------------

    [Fact]
    public async Task RealCloseHoldingTheWorkOrderFirst_MakesApproveWait_ThenApproveIsRefused_AndNothingOfItSurvives()
    {
        var world = await TimeCorrectionWorlds.CreateAsync(_plainHost);
        await _plain.MakeClosableAsync(world);
        var request = await _plain.RequestAsync(world.Base.TeamLead, world.SessionId, "CHECK_IN", world.At(-5));
        Assert.True(request.Succeeded, request.Error?.Message);

        var factsBefore = await _plain.SessionAsync(world.SessionId);
        var slaBefore = await _plain.SlaAsync(world);
        var workOrderVersion = await _plain.WorkOrderRowVersionAsync(world.WorkOrderId);
        var connectionString = await _plain.ConnectionStringAsync();

        // The real Close runs every statement (guards, SLA stop, Work Order write, audit) and is held just before its commit: all locks held.
        var commitGate = new BlockBeforeCommitInterceptor();
        var closeHost = HostWith(options => options.AddInterceptors(commitGate));
        var close = Task.Run(() => CloseAsync(closeHost, world, workOrderVersion));
        Assert.True(await commitGate.Reached.WaitAsync(TimeSpan.FromSeconds(60)), "The real Close never reached its commit.");

        var approve = Task.Run(() => _plain.ApproveAsync(world.Base.Supervisor, request.Value));
        var blocked = await SqlProbes.WaitForBlockedRequestAsync(connectionString, "work_order");
        Assert.StartsWith("LCK_M_", blocked.WaitType);                         // SQL Server reports the Approve waiting on a lock of the Work Order
        Assert.False(approve.IsCompleted);                                     // it is waiting for the Close, not racing past it
        Assert.False(close.IsCompleted);

        commitGate.Release.Release();
        var closed = await close;
        var approved = await approve;

        Assert.True(closed.Succeeded, closed.Error?.Message);
        Assert.Equal(CommandFailure.StateConflict, approved.Error?.Failure);   // the Approve re-read the Work Order after the Close committed

        // No correction application and no approval audit survives.
        Assert.Equal(factsBefore, await _plain.SessionAsync(world.SessionId));
        var correction = await CorrectionAsync(request.Value);
        Assert.Equal(TimeCorrectionStatus.Pending, correction.Status);
        Assert.Null(correction.DecidedBy);
        Assert.Equal(0, await _plain.AuditCountAsync(world.TenantId, WorkOrderAudit.TimeCorrectionApprovedAction));

        // The real Close's own results are intact: CLOSED, one WORK_ORDER_CLOSED audit, the SLA stopped (WO_CLOSED) at the Close's instant.
        Assert.Equal(WorkOrderStatus.Closed, await WorkOrderStatusAsync(world.WorkOrderId));
        var closedAudit = Assert.Single(await AuditsAsync(world.TenantId, WorkOrderAudit.ClosedAction));
        var sla = await _plain.SlaAsync(world);
        Assert.Equal(SlaStopReason.WoClosed, sla.StopReason);
        Assert.Equal(closedAudit.OccurredAt, sla.StoppedAt);
        Assert.Single(await AuditsAsync(world.TenantId, SlaAudit.StoppedAction));
        Assert.Equal((slaBefore.StartedAt, slaBefore.OriginalDueAt, slaBefore.RiskAt), (sla.StartedAt, sla.OriginalDueAt, sla.RiskAt));
    }

    // ---------------- Order B: Approve holds the Work Order first ----------------

    [Fact]
    public async Task ApproveInFlight_MakesTheRealCloseWait_ThenCloseSucceedsOnItsOriginalRowVersion_AndBothResultsAreConsistent()
    {
        var world = await TimeCorrectionWorlds.CreateAsync(_plainHost);
        await _plain.MakeClosableAsync(world);
        var newCheckIn = world.At(-5);
        var request = await _plain.RequestAsync(world.Base.TeamLead, world.SessionId, "CHECK_IN", newCheckIn);
        Assert.True(request.Succeeded, request.Error?.Message);

        var slaBefore = await _plain.SlaAsync(world);
        var workOrderVersion = await _plain.WorkOrderRowVersionAsync(world.WorkOrderId);
        var connectionString = await _plain.ConnectionStringAsync();

        // The Approve holds the Work Order, session, pause and Visit locks and has not written yet.
        var gate = new BlockOnCommandInterceptor("UPDATE [time_correction]");
        var approveHost = HostWith(options => options.AddInterceptors(gate));
        var approve = Task.Run(() => new TimeCorrectionDriver(approveHost).ApproveAsync(world.Base.Supervisor, request.Value));
        Assert.True(await gate.Reached.WaitAsync(TimeSpan.FromSeconds(60)), "The Approve never reached its write.");

        // The real Close, with the Work Order row version read before the Approve began: it must reach its guarded Work Order write and wait there.
        var close = Task.Run(() => CloseAsync(_plainHost, world, workOrderVersion));
        var blocked = await SqlProbes.WaitForBlockedRequestAsync(connectionString, "UPDATE [work_order]");
        Assert.StartsWith("LCK_M_", blocked.WaitType);
        Assert.False(close.IsCompleted);
        Assert.False(approve.IsCompleted);

        gate.Release.Release();
        var approved = await approve;
        var closed = await close;

        Assert.True(approved.Succeeded, approved.Error?.Message);
        Assert.True(closed.Succeeded, closed.Error?.Message);                  // the Approve never rewrote the Work Order row, so the original token is still valid

        // The correction is applied and decided; the Work Order is CLOSED by the real Close.
        Assert.Equal(newCheckIn, (await _plain.SessionAsync(world.SessionId)).CheckIn);
        var correction = await CorrectionAsync(request.Value);
        Assert.Equal(TimeCorrectionStatus.Approved, correction.Status);
        Assert.False(correction.SlaRecalculated);
        Assert.Single(await AuditsAsync(world.TenantId, WorkOrderAudit.TimeCorrectionApprovedAction));
        Assert.Equal(WorkOrderStatus.Closed, await WorkOrderStatusAsync(world.WorkOrderId));
        var closedAudit = Assert.Single(await AuditsAsync(world.TenantId, WorkOrderAudit.ClosedAction));

        // Close's SLA stop is intact and is the Close's own: stopped once, WO_CLOSED, at the Close's instant; Time Correction recalculated nothing.
        var sla = await _plain.SlaAsync(world);
        Assert.Equal(SlaStopReason.WoClosed, sla.StopReason);
        Assert.Equal(closedAudit.OccurredAt, sla.StoppedAt);
        Assert.NotEqual(correction.DecidedAt, sla.StoppedAt);
        Assert.Single(await AuditsAsync(world.TenantId, SlaAudit.StoppedAction));
        Assert.Equal((slaBefore.StartedAt, slaBefore.OriginalDueAt, slaBefore.RiskAt), (sla.StartedAt, sla.OriginalDueAt, sla.RiskAt));
    }

    // ---------------- Atomicity: a failure inside the Approve transaction ----------------

    [Fact]
    public async Task AFailureJustBeforeTheCommit_RollsBackTheWholeApprove_IsNotMappedToAConflict_AndALaterApproveSucceeds()
    {
        var world = await TimeCorrectionWorlds.CreateAsync(_plainHost);
        var newCheckIn = world.At(-5);
        var request = await _plain.RequestAsync(world.Base.TeamLead, world.SessionId, "CHECK_IN", newCheckIn);
        Assert.True(request.Succeeded, request.Error?.Message);
        var factsBefore = await _plain.SessionAsync(world.SessionId);
        var sessionVersion = await _plain.SessionRowVersionAsync(world.SessionId);
        var correctionVersion = await _plain.RowVersionAsync(request.Value);

        // Every statement of the Approve ran (session, correction and audit writes); the commit then fails.
        var failing = new TimeCorrectionDriver(HostWith(options => options.AddInterceptors(new FailAtCommitInterceptor())));
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => failing.ApproveAsync(world.Base.Supervisor, request.Value));
        Assert.Contains("Simulated failure", thrown.Message);                  // an unrelated failure surfaces as itself, not as a 409

        Assert.Equal(factsBefore, await _plain.SessionAsync(world.SessionId));
        Assert.Equal(sessionVersion, await _plain.SessionRowVersionAsync(world.SessionId));
        Assert.Equal(correctionVersion, await _plain.RowVersionAsync(request.Value));
        var correction = await CorrectionAsync(request.Value);
        Assert.Equal(TimeCorrectionStatus.Pending, correction.Status);
        Assert.Null(correction.DecidedAt);
        Assert.Equal(0, await _plain.AuditCountAsync(world.TenantId, WorkOrderAudit.TimeCorrectionApprovedAction));

        // The rolled-back transaction released every lock: the same request, with the same token, is approved normally afterwards.
        var approved = await _plain.ApproveAsync(world.Base.Supervisor, request.Value, correctionVersion);
        Assert.True(approved.Succeeded, approved.Error?.Message);
        Assert.Equal(newCheckIn, (await _plain.SessionAsync(world.SessionId)).CheckIn);
        Assert.Equal(1, await _plain.AuditCountAsync(world.TenantId, WorkOrderAudit.TimeCorrectionApprovedAction));
    }
}
