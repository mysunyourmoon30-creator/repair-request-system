using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RepairRequest.Application.Audit;
using RepairRequest.Domain.Security;
using RepairRequest.Domain.WorkOrders;
using RepairRequest.Infrastructure.Persistence;
using RepairRequest.IntegrationTests.Audit.Support;
using RepairRequest.IntegrationTests.Authentication;
using RepairRequest.IntegrationTests.Authorization;
using RepairRequest.IntegrationTests.Persistence;
using RepairRequest.IntegrationTests.TimeCorrection;

namespace RepairRequest.IntegrationTests.Audit;

/// <summary>
/// OPT-IN evidence run (<c>RUN_TIMELINE_EVIDENCE=1</c>) for the Time Correction lookup that feeds the Work Order timeline (<c>docs/17</c> P11,
/// <c>docs/15</c> §7): the production store's real <c>correction → Work Session → Service Visit → Work Order</c> statement, captured from EF Core and
/// re-executed under SET STATISTICS IO / XML on an isolated database that holds a populated, representative set — many corrections on the
/// target Work Order next to same-tenant corrections of unrelated Work Orders and another tenant's corrections. Correctness (the exact
/// id set) is asserted; the figures are evidence, never a performance claim. The page query for a populated set is measured by the
/// <c>TC</c> scenario of <see cref="AuditTimelineEvidenceTests"/> against the 500,000-row audit fixture.
/// </summary>
[Collection(PersistenceDatabaseCollection.Name)]
public sealed class AuditTimelineTimeCorrectionLookupEvidenceTests : IAsyncLifetime
{
    private readonly CapturingInterceptor _capture = new();
    private readonly AuthenticationTestHost _host;

    public AuditTimelineTimeCorrectionLookupEvidenceTests(PersistenceDatabaseFixture database)
    {
        _ = database;
        _host = new AuthenticationTestHost(services => services.ConfigureDbContext<RepairRequestDbContext>(options => options.AddInterceptors(_capture)));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _host.DisposeAsync();

    /// <summary>A Work Order with <paramref name="sessions"/> Visits, one closed Work Session each, and <paramref name="perSession"/> decided corrections per session.</summary>
    private async Task<(Guid WorkOrderId, HashSet<Guid> CorrectionIds)> AddWorkOrderAsync(TimeCorrectionWorld world, int sessions, int perSession)
    {
        var tenantId = world.TenantId;
        var technicianId = (await _host.CreateUserInTenantAsync(tenantId, RoleCodes.Technician)).Id;
        await using var scope = _host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RepairRequestDbContext>();

        var requestId = await ScopeWorld.AddRequestAsync(db, tenantId, world.Base.OtherRequester.UserId, world.Base.Scope.SiteA1);
        var workOrderId = await ScopeWorld.AddWorkOrderAsync(db, tenantId, requestId, $"WO-{Guid.NewGuid():N}"[..14], DateTime.UtcNow);

        var visits = Enumerable.Range(0, sessions).Select(_ => ServiceVisit.Create(
            tenantId, workOrderId, ServiceVisitType.Initial, Guid.NewGuid(), technicianId, DateTime.UtcNow.AddDays(1), DateTime.UtcNow.AddDays(1).AddHours(2))).ToList();
        db.ServiceVisits.AddRange(visits);
        await db.SaveChangesAsync();

        var workSessions = visits.Select(visit =>
        {
            var session = WorkSession.Create(tenantId, visit.Id, technicianId, DateTime.UtcNow.AddHours(-3));
            session.CheckOut(DateTime.UtcNow.AddHours(-2));
            return session;
        }).ToList();
        db.WorkSessions.AddRange(workSessions);
        await db.SaveChangesAsync();

        var corrections = new List<Domain.WorkOrders.TimeCorrection>();
        foreach (var session in workSessions)
        {
            for (var i = 0; i < perSession; i++)
            {
                var correction = Domain.WorkOrders.TimeCorrection.Request(
                    tenantId, session.Id, null, TimeCorrectionTimestampType.CheckIn, session.CheckInAt, session.CheckInAt.AddMinutes(-(i + 1)), "Clock was wrong.", world.Base.TeamLead.UserId);
                if (i % 3 == 2)
                {
                    correction.Reject("Not supported by the visit log.", DateTime.UtcNow, world.Base.Supervisor.UserId);
                }
                else
                {
                    correction.Approve(DateTime.UtcNow, world.Base.Supervisor.UserId);
                }

                corrections.Add(correction);
            }
        }

        db.TimeCorrections.AddRange(corrections);
        await db.SaveChangesAsync();
        return (workOrderId, corrections.Select(correction => correction.Id).ToHashSet());
    }

    [EvidenceFact]
    public async Task CollectCorrectionLookupPlanEvidence()
    {
        var target = await TimeCorrectionWorlds.CreateAsync(_host);
        var other = await TimeCorrectionWorlds.CreateAsync(_host);                  // another tenant

        var (targetWorkOrder, targetCorrections) = await AddWorkOrderAsync(target, sessions: 20, perSession: 6);        // 120 on the target
        var sameTenantCorrections = 0;
        for (var i = 0; i < 300; i++)                                                                                   // 300 unrelated Work Orders x 12
        {
            sameTenantCorrections += (await AddWorkOrderAsync(target, sessions: 2, perSession: 6)).CorrectionIds.Count;
        }

        var otherTenantCorrections = (await AddWorkOrderAsync(other, sessions: 30, perSession: 6)).CorrectionIds.Count;  // 180 in another tenant

        await using var scope = _host.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IAuditTimelineStore>();
        var connectionString = await new TimeCorrectionDriver(_host).ConnectionStringAsync();
        await new TimeCorrectionDriver(_host).WithDbAsync(async db =>
        {
            foreach (var table in new[] { "time_correction", "work_session", "service_visit" })
            {
                await db.Database.ExecuteSqlRawAsync($"UPDATE STATISTICS [{table}] WITH FULLSCAN");
            }

            return 0;
        });

        _capture.Commands.Clear();
        var sources = await store.ResolveWorkOrderSourcesAsync(target.Base.Supervisor, targetWorkOrder, CancellationToken.None);

        // Correctness first: exactly the target Work Order's corrections — none of the unrelated or other-tenant ones.
        Assert.NotNull(sources);
        Assert.Equal(targetCorrections.Order(), sources.TimeCorrectionIds.Order());
        Assert.Equal(120, sources.TimeCorrectionIds.Count);

        var command = _capture.Commands.Single(item => item.Sql.Contains("[time_correction]", StringComparison.Ordinal));
        var measurement = await SqlPlanEvidence.MeasureAsync(connectionString, command.Sql, command.Parameters);
        Assert.Equal(120, measurement.RowsReturned);
        var median = await SqlPlanEvidence.MedianMillisecondsAsync(connectionString, command.Sql, command.Parameters);

        // The existing Work Session id lookup has the same join shape (session -> Visit -> Work Order); measured for comparison.
        var sessionCommand = _capture.Commands.Single(item => item.Sql.Contains("[work_session]", StringComparison.Ordinal) && !item.Sql.Contains("[time_correction]", StringComparison.Ordinal));
        var sessionMeasurement = await SqlPlanEvidence.MeasureAsync(connectionString, sessionCommand.Sql, sessionCommand.Parameters);
        var sessionMedian = await SqlPlanEvidence.MedianMillisecondsAsync(connectionString, sessionCommand.Sql, sessionCommand.Parameters);
        var totals = await new TimeCorrectionDriver(_host).WithDbAsync(async db =>
            (await db.TimeCorrections.CountAsync(), await db.WorkSessions.CountAsync(), await db.ServiceVisits.CountAsync()));

        var md = new StringBuilder();
        md.AppendLine("# Work Order timeline — Time Correction lookup evidence");
        md.AppendLine();
        md.AppendLine("Isolated database from the real migrations. Populated set: ");
        md.AppendLine($"{targetCorrections.Count} corrections on the target Work Order (20 Visits/Sessions x 6; every third rejected), {sameTenantCorrections} corrections of 300 unrelated Work Orders of the same tenant, ");
        md.AppendLine($"{otherTenantCorrections} corrections in another tenant. Table totals: {totals.Item1} time_correction rows, {totals.Item2} work_session rows, {totals.Item3} service_visit rows. Statistics refreshed with FULLSCAN. The statement below is the production store's captured command, re-executed under SET STATISTICS IO/XML.");
        md.AppendLine();
        md.AppendLine("| Rows returned | Logical reads (other tables) | Parameters | Median ms (15 warm runs, informational) |");
        md.AppendLine("|---|---|---|---|");
        md.AppendLine($"| {measurement.RowsReturned} | {measurement.OtherTables} | {measurement.ParameterCount} | {median:F2} |");
        md.AppendLine();
        md.AppendLine($"Operators: {measurement.OperatorSummary}");
        md.AppendLine();
        md.AppendLine("```sql");
        md.AppendLine(command.Sql);
        md.AppendLine("```");
        md.AppendLine();
        md.AppendLine("## Comparison — the existing Work Session id lookup (same join shape)");
        md.AppendLine();
        md.AppendLine("| Rows returned | Logical reads (other tables) | Median ms (informational) |");
        md.AppendLine("|---|---|---|");
        md.AppendLine($"| {sessionMeasurement.RowsReturned} | {sessionMeasurement.OtherTables} | {sessionMedian:F2} |");
        md.AppendLine();
        md.AppendLine($"Operators: {sessionMeasurement.OperatorSummary}");
        var path = Path.Combine(Path.GetDirectoryName(SqlPlanEvidence.EvidencePath)!, "time-correction-lookup-evidence.md");
        await File.WriteAllTextAsync(path, md.ToString());
        Assert.True(File.Exists(path));
    }
}
