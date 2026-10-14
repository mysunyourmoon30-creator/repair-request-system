using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using RepairRequest.Application.Common;
using RepairRequest.Application.Security;
using RepairRequest.Application.WorkOrders;
using RepairRequest.Domain.Security;
using RepairRequest.Domain.Sla;
using RepairRequest.Domain.WorkOrders;
using RepairRequest.Infrastructure.Persistence;
using RepairRequest.IntegrationTests.Authentication;
using RepairRequest.IntegrationTests.Authorization;
using RepairRequest.IntegrationTests.WorkEvidence;

namespace RepairRequest.IntegrationTests.TimeCorrection;

/// <summary>
/// A checked-out Work Session with two closed pauses on an IN_PROGRESS Work Order, seeded through the domain model into an isolated
/// database: check-in at C, pauses (C+10..C+20) and (C+30..C+40), check-out at C+60; the Visit is COMPLETED at the check-out instant.
/// </summary>
internal sealed record TimeCorrectionWorld(
    EvidenceWorld Base,
    DateTime CheckIn,
    Guid Pause1,
    Guid Pause2,
    DateTime CheckOut)
{
    public Guid TenantId => Base.Scope.TenantId;

    public Guid WorkOrderId => Base.WorkOrderId;

    public Guid SessionId => Base.SessionId;

    public DateTime At(int minutes) => CheckIn.AddMinutes(minutes);
}

internal static class TimeCorrectionWorlds
{
    public static async Task<TimeCorrectionWorld> CreateAsync(AuthenticationTestHost host)
    {
        var world = await EvidenceWorlds.CreateAsync(host, checkedOut: false);

        await using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RepairRequestDbContext>();
        var session = await db.WorkSessions.SingleAsync(item => item.Id == world.SessionId);
        var visit = await db.ServiceVisits.SingleAsync(item => item.Id == world.VisitId);
        var checkIn = session.CheckInAt;

        var pauses = new List<WorkSessionPause>();
        foreach (var (start, end) in new[] { (10, 20), (30, 40) })
        {
            var pause = session.Pause("Waiting for a part", checkIn.AddMinutes(start));
            db.WorkSessionPauses.Add(pause);
            session.Resume(pause, checkIn.AddMinutes(end));
            pauses.Add(pause);
        }

        session.CheckOut(checkIn.AddMinutes(60));
        visit.CheckOut(checkIn.AddMinutes(60));
        await db.SaveChangesAsync();

        return new TimeCorrectionWorld(world, checkIn, pauses[0].Id, pauses[1].Id, checkIn.AddMinutes(60));
    }

    /// <summary>A user holding the given roles on Site A1 of the world's tenant, as a caller.</summary>
    public static async Task<CurrentUser> UserAsync(AuthenticationTestHost host, TimeCorrectionWorld world, params string[] roles)
    {
        var user = await host.CreateUserInTenantAsync(world.TenantId, roles);
        await using var scope = host.CreateScope();
        await ScopeWorld.AssignSitesAsync(
            scope.ServiceProvider.GetRequiredService<RepairRequestDbContext>(), world.TenantId, user.Id, [world.Base.Scope.SiteA1]);
        return new CurrentUser(user.Id, world.TenantId, roles);
    }
}

/// <summary>Everything the Time Correction tests do through the real services, each call in its own scope (its own connection), like separate requests.</summary>
internal sealed class TimeCorrectionDriver
{
    private readonly AuthenticationTestHost _host;

    public TimeCorrectionDriver(AuthenticationTestHost host)
    {
        _host = host;
    }

    public async Task<T> WithDbAsync<T>(Func<RepairRequestDbContext, Task<T>> action)
    {
        await using var scope = _host.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<RepairRequestDbContext>());
    }

    public async Task<string> ConnectionStringAsync() =>
        await WithDbAsync(db => Task.FromResult(db.Database.GetConnectionString()!));

    public static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    public async Task<CommandResult<Guid>> RequestAsync(
        CurrentUser user, Guid sessionId, string type, DateTime newValue, Guid? pauseId = null, string reason = "The technician forgot to press the button.")
    {
        await using var scope = _host.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<TimeCorrectionService>().RequestAsync(
            new CommandContext(user, Guid.NewGuid()), sessionId, type, pauseId, Utc(newValue), reason, CancellationToken.None);
    }

    public async Task<byte[]> RowVersionAsync(Guid correctionId) =>
        await WithDbAsync(db => db.TimeCorrections.AsNoTracking().Where(item => item.Id == correctionId).Select(item => item.RowVersion).SingleAsync());

    public async Task<CommandResult<Guid>> ApproveAsync(CurrentUser user, Guid correctionId, byte[]? rowVersion = null)
    {
        var token = rowVersion ?? await RowVersionAsync(correctionId);
        await using var scope = _host.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<TimeCorrectionService>().ApproveAsync(
            new CommandContext(user, Guid.NewGuid()), correctionId, token, CancellationToken.None);
    }

    public async Task<CommandResult<Guid>> RejectAsync(CurrentUser user, Guid correctionId, byte[]? rowVersion = null)
    {
        var token = rowVersion ?? await RowVersionAsync(correctionId);
        await using var scope = _host.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<TimeCorrectionService>().RejectAsync(
            new CommandContext(user, Guid.NewGuid()), correctionId, token, "Not supported by the visit log.", CancellationToken.None);
    }

    public async Task<byte[]> WorkOrderRowVersionAsync(Guid workOrderId) =>
        await WithDbAsync(db => db.WorkOrders.AsNoTracking().Where(item => item.Id == workOrderId).Select(item => item.RowVersion).SingleAsync());

    public async Task<byte[]> SessionRowVersionAsync(Guid sessionId) =>
        await WithDbAsync(db => db.WorkSessions.AsNoTracking().Where(item => item.Id == sessionId).Select(item => item.RowVersion).SingleAsync());

    public async Task<(DateTime CheckIn, DateTime? CheckOut, DateTime? PauseStart, DateTime? Resume, WorkSessionStatus Status)> SessionAsync(Guid sessionId) =>
        await WithDbAsync(async db =>
        {
            var session = await db.WorkSessions.AsNoTracking().SingleAsync(item => item.Id == sessionId);
            return (session.CheckInAt, session.CheckOutAt, session.PauseStartAt, session.ResumeAt, session.Status);
        });

    public async Task<(DateTime PausedAt, DateTime? ResumedAt)> PauseAsync(Guid pauseId) =>
        await WithDbAsync(async db =>
        {
            var pause = await db.WorkSessionPauses.AsNoTracking().SingleAsync(item => item.Id == pauseId);
            return (pause.PausedAt, pause.ResumedAt);
        });

    public Task<DateTime?> VisitCompletedAtAsync(Guid visitId) =>
        WithDbAsync(db => db.ServiceVisits.AsNoTracking().Where(item => item.Id == visitId).Select(item => item.CompletedAt).SingleAsync());

    public Task<int> AuditCountAsync(Guid tenantId, string action) =>
        WithDbAsync(db => db.AuditHistory.CountAsync(audit => audit.TenantId == tenantId && audit.EntityType == WorkOrderAudit.TimeCorrectionEntityType && audit.ActionCode == action));

    /// <summary>Seeds the downstream rows Time Correction must never touch: an SLA record, a Work Summary, a Customer Acceptance and a reviewed-style Cost Summary.</summary>
    public async Task SeedDownstreamAsync(TimeCorrectionWorld world)
    {
        await WithDbAsync(async db =>
        {
            var now = DateTime.UtcNow;
            db.SlaRecords.Add(SlaRecord.Start(world.TenantId, world.Base.RepairRequestId, now.AddDays(-1)));
            db.WorkSummaries.Add(WorkSummary.Create(world.TenantId, world.WorkOrderId, world.Base.VisitId, "Replaced the bearing.", RepairOutcomeCode.Repaired));
            db.CustomerAcceptances.Add(CustomerAcceptance.Accept(world.TenantId, world.WorkOrderId, 1, world.Base.AcceptanceContact.UserId, now));
            db.CostSummaries.Add(CostSummary.Create(world.TenantId, world.WorkOrderId, 4821.37m, "THB", "note", world.Base.TeamLead.UserId, now));
            await db.SaveChangesAsync();
            return 0;
        });
    }

    /// <summary>
    /// Turns the world's Work Order into one the real Close accepts (COMPLETED; Work Summary; designated contact; accepted round 1; a reviewed
    /// Cost Summary) and starts its SLA record, through the domain model and the same entities the lifecycle services write.
    /// </summary>
    public async Task MakeClosableAsync(TimeCorrectionWorld world)
    {
        await WithDbAsync(async db =>
        {
            var now = DateTime.UtcNow;
            var workOrder = await db.WorkOrders.SingleAsync(item => item.Id == world.WorkOrderId);
            workOrder.SubmitWorkSummary();
            workOrder.SubmitForAcceptance(world.Base.AcceptanceContact.UserId, "snapshot");
            workOrder.Accept();

            db.WorkSummaries.Add(WorkSummary.Create(world.TenantId, world.WorkOrderId, world.Base.VisitId, "Replaced the bearing.", RepairOutcomeCode.Repaired));
            db.CustomerAcceptances.Add(CustomerAcceptance.Accept(world.TenantId, world.WorkOrderId, 1, world.Base.AcceptanceContact.UserId, now));
            var cost = CostSummary.Create(world.TenantId, world.WorkOrderId, 4821.37m, "THB", "note", world.Base.TeamLead.UserId, now);
            cost.Review(world.Base.Supervisor.UserId, now);
            db.CostSummaries.Add(cost);
            db.SlaRecords.Add(SlaRecord.Start(world.TenantId, world.Base.RepairRequestId, now.AddDays(-1)));
            await db.SaveChangesAsync();
            return 0;
        });
    }

    public Task<SlaRecord> SlaAsync(TimeCorrectionWorld world) =>
        WithDbAsync(db => db.SlaRecords.AsNoTracking().SingleAsync(item => item.RepairRequestId == world.Base.RepairRequestId));

    /// <summary>One JSON text of every downstream row: any change to SLA, Work Summary, Acceptance, Cost Summary or the Work Order (status, row version) shows up.</summary>
    public async Task<string> DownstreamSnapshotAsync(TimeCorrectionWorld world)
    {
        var tenant = world.TenantId;
        var workOrder = world.WorkOrderId;
        var request = world.Base.RepairRequestId;
        return await WithDbAsync(async db => string.Join(
            "|",
            await db.Database.SqlQuery<string>($"SELECT ISNULL((SELECT * FROM [sla_record] WHERE [tenant_id] = {tenant} AND [repair_request_id] = {request} FOR JSON AUTO), '') AS [Value]").SingleAsync(),
            await db.Database.SqlQuery<string>($"SELECT ISNULL((SELECT * FROM [work_summary] WHERE [tenant_id] = {tenant} AND [work_order_id] = {workOrder} FOR JSON AUTO), '') AS [Value]").SingleAsync(),
            await db.Database.SqlQuery<string>($"SELECT ISNULL((SELECT * FROM [customer_acceptance] WHERE [tenant_id] = {tenant} AND [work_order_id] = {workOrder} FOR JSON AUTO), '') AS [Value]").SingleAsync(),
            await db.Database.SqlQuery<string>($"SELECT ISNULL((SELECT * FROM [cost_summary] WHERE [tenant_id] = {tenant} AND [work_order_id] = {workOrder} FOR JSON AUTO), '') AS [Value]").SingleAsync(),
            await db.Database.SqlQuery<string>($"SELECT ISNULL((SELECT * FROM [work_order] WHERE [tenant_id] = {tenant} AND [work_order_id] = {workOrder} FOR JSON AUTO), '') AS [Value]").SingleAsync()));
    }
}

/// <summary>
/// Blocks the first command whose text contains <see cref="Match"/> — after every lock of the command is already held — until released, so a
/// test can run a competing command on an independent connection while an Approve is "in the middle" of its transaction.
/// </summary>
internal sealed class BlockOnCommandInterceptor : DbCommandInterceptor
{
    private int _armed = 1;

    public BlockOnCommandInterceptor(string match)
    {
        Match = match;
    }

    public string Match { get; }

    public SemaphoreSlim Reached { get; } = new(0);

    public SemaphoreSlim Release { get; } = new(0);

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        await MaybeBlockAsync(command);
        return result;
    }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        await MaybeBlockAsync(command);
        return result;
    }

    private async Task MaybeBlockAsync(DbCommand command)
    {
        if (command.CommandText.Contains(Match, StringComparison.Ordinal) && Interlocked.Exchange(ref _armed, 0) == 1)
        {
            Reached.Release();
            Assert.True(await Release.WaitAsync(TimeSpan.FromSeconds(60)), "The blocked command was never released.");
        }
    }
}

/// <summary>Blocks the first transaction commit of its host — after every statement of the command ran and every lock is held — until released.</summary>
internal sealed class BlockBeforeCommitInterceptor : DbTransactionInterceptor
{
    private int _armed = 1;

    public SemaphoreSlim Reached { get; } = new(0);

    public SemaphoreSlim Release { get; } = new(0);

    public override async ValueTask<InterceptionResult> TransactionCommittingAsync(
        DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _armed, 0) == 1)
        {
            Reached.Release();
            Assert.True(await Release.WaitAsync(TimeSpan.FromSeconds(60)), "The held commit was never released.");
        }

        return result;
    }
}

/// <summary>Fails the first transaction commit of its host: every statement already ran, so the whole transaction must roll back.</summary>
internal sealed class FailAtCommitInterceptor : DbTransactionInterceptor
{
    private int _armed = 1;

    public override ValueTask<InterceptionResult> TransactionCommittingAsync(
        DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _armed, 0) == 1)
        {
            throw new InvalidOperationException("Simulated failure just before the commit.");
        }

        return ValueTask.FromResult(result);
    }
}

internal sealed record BlockedRequest(string WaitType, string Text);

internal static class SqlProbes
{
    /// <summary>
    /// Waits until a request of this database is blocked by another session and returns what it waits for (the SQL Server lock wait type and the
    /// statement text), so a test can prove the competitor reached the intended operation rather than merely "something waits".
    /// </summary>
    public static async Task<BlockedRequest> WaitForBlockedRequestAsync(string connectionString, string textContains, int timeoutSeconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        while (DateTime.UtcNow < deadline)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT r.wait_type, t.text
                FROM sys.dm_exec_requests r CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) t
                WHERE r.blocking_session_id <> 0 AND r.database_id = DB_ID() AND r.session_id <> @@SPID
                """;
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var text = reader.GetString(1);
                if (text.Contains(textContains, StringComparison.OrdinalIgnoreCase))
                {
                    return new BlockedRequest(reader.GetString(0), text);
                }
            }

            await Task.Delay(50);
        }

        Assert.Fail($"No request containing '{textContains}' became blocked by another session within {timeoutSeconds} s.");
        return null!;
    }

    /// <summary>Waits until at least <paramref name="count"/> requests of the database are blocked by another session (a real lock wait seen by SQL Server).</summary>
    public static async Task WaitForBlockedRequestsAsync(string connectionString, int count = 1, int timeoutSeconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        while (DateTime.UtcNow < deadline)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sys.dm_exec_requests WHERE blocking_session_id <> 0 AND database_id = DB_ID() AND session_id <> @@SPID";
            if ((int)(await command.ExecuteScalarAsync())! >= count)
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail($"No request became blocked by another session within {timeoutSeconds} s.");
    }
}
