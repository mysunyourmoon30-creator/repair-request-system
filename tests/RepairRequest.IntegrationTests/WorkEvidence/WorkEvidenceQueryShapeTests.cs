using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RepairRequest.Application.Security;
using RepairRequest.Application.WorkEvidence;
using RepairRequest.Domain.Files;
using RepairRequest.Domain.WorkEvidence;
using RepairRequest.Domain.WorkOrders;
using RepairRequest.Infrastructure.Persistence;
using RepairRequest.IntegrationTests.Audit.Support;
using RepairRequest.IntegrationTests.Authentication;
using RepairRequest.IntegrationTests.Persistence;

namespace RepairRequest.IntegrationTests.WorkEvidence;

/// <summary>
/// Query shape of the Service Report and the evidence list (`docs/16` §4; `docs/13` §4.28): a fixed number of bounded set-based commands
/// regardless of the amount of data (no N+1), and the cost table is never even queried for the Technician or the Acceptance Contact.
/// </summary>
[Collection(PersistenceDatabaseCollection.Name)]
public sealed class WorkEvidenceQueryShapeTests : IAsyncLifetime
{
    private const string ValidHash = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";
    private static readonly CancellationToken None = CancellationToken.None;

    private readonly CapturingInterceptor _capture = new();
    private readonly AuthenticationTestHost _host;

    public WorkEvidenceQueryShapeTests(PersistenceDatabaseFixture database)
    {
        _ = database;
        _host = new AuthenticationTestHost(services => services.AddDbContext<RepairRequestDbContext>(options => options.AddInterceptors(_capture)));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _host.DisposeAsync();

    /// <summary>Seeds <paramref name="rounds"/> submitted Work Summary rounds with <paramref name="perRound"/> bound CLEAN items each, plus acceptance and cost.</summary>
    private async Task SeedAsync(EvidenceWorld world, int rounds, int perRound)
    {
        await using var scope = _host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RepairRequestDbContext>();

        for (var round = 0; round < rounds; round++)
        {
            var visitId = world.VisitId;
            if (round > 0)
            {
                var visit = ServiceVisit.Create(
                    world.Scope.TenantId, world.WorkOrderId, ServiceVisitType.Corrective, Guid.NewGuid(), world.Technician.UserId,
                    DateTime.UtcNow.AddHours(round), DateTime.UtcNow.AddHours(round + 2));
                visit.CheckIn();
                db.ServiceVisits.Add(visit);
                var session = WorkSession.Create(world.Scope.TenantId, visit.Id, world.Technician.UserId, DateTime.UtcNow.AddMinutes(round));
                session.CheckOut(DateTime.UtcNow.AddMinutes(round + 10));
                db.WorkSessions.Add(session);
                await db.SaveChangesAsync();
                visitId = visit.Id;
            }

            var summary = WorkSummary.Create(world.Scope.TenantId, world.WorkOrderId, visitId, $"Round {round} summary.", RepairOutcomeCode.Repaired);
            db.WorkSummaries.Add(summary);
            await db.SaveChangesAsync();

            for (var slot = 1; slot <= perRound; slot++)
            {
                var file = new FileAsset(world.Scope.TenantId, $"photo-{round}-{slot}.png", "image/png", 1024, ValidHash, $"{world.Scope.TenantId:N}/{Guid.NewGuid():N}", world.Technician.UserId, DateTime.UtcNow);
                file.MarkClean();
                db.FileAssets.Add(file);
                await db.SaveChangesAsync();

                var upload = WorkEvidenceUpload.Create(world.Scope.TenantId, world.WorkOrderId, visitId, file.Id, WorkEvidenceType.AfterWork, DateTime.UtcNow);
                db.WorkEvidenceUploads.Add(upload);
                await db.SaveChangesAsync();
                db.WorkSummaryEvidence.Add(WorkSummaryEvidence.Bind(upload, summary.Id, slot, DateTime.UtcNow));
                await db.SaveChangesAsync();
            }

            db.CustomerAcceptances.Add(round == rounds - 1
                ? CustomerAcceptance.Accept(world.Scope.TenantId, world.WorkOrderId, round + 1, world.AcceptanceContact.UserId, DateTime.UtcNow)
                : CustomerAcceptance.Reject(world.Scope.TenantId, world.WorkOrderId, round + 1, world.AcceptanceContact.UserId, "Not fixed.", DateTime.UtcNow));
            await db.SaveChangesAsync();
        }

        db.CostSummaries.Add(CostSummary.Create(world.Scope.TenantId, world.WorkOrderId, 4821.37m, "THB", "COST-SENTINEL", world.TeamLead.UserId, DateTime.UtcNow));
        await db.SaveChangesAsync();
    }

    private async Task<(ServiceReportResult? Result, List<CapturedCommand> Commands)> ReportAsync(CurrentUser user, Guid workOrderId)
    {
        await using var scope = _host.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IServiceReportStore>();
        _capture.Commands.Clear();
        var result = await store.GetAsync(user, workOrderId, None);
        return (result, _capture.Commands.ToList());
    }

    [Fact]
    public async Task ServiceReport_UsesAFixedNumberOfCommands_NoMatterHowMuchDataTheWorkOrderHolds()
    {
        var small = await EvidenceWorlds.CreateAsync(_host);
        await SeedAsync(small, rounds: 1, perRound: 1);
        var large = await EvidenceWorlds.CreateAsync(_host);
        await SeedAsync(large, rounds: 3, perRound: 10);

        var smallReport = await ReportAsync(small.TeamLead, small.WorkOrderId);
        var largeReport = await ReportAsync(large.TeamLead, large.WorkOrderId);

        Assert.NotNull(smallReport.Result);
        Assert.NotNull(largeReport.Result);
        Assert.Equal(30, largeReport.Result.Report.WorkSummaries.Sum(summary => summary.Evidence.Count));
        Assert.Equal(3, largeReport.Result.Report.WorkSummaries.Count);
        Assert.Equal(smallReport.Commands.Count, largeReport.Commands.Count);
        Assert.InRange(largeReport.Commands.Count, 8, 14);
    }

    [Fact]
    public async Task ForTheTechnicianAndTheAcceptanceContact_TheCostTableIsNeverQueried_AndTheCostSectionIsAbsent()
    {
        var world = await EvidenceWorlds.CreateAsync(_host);
        await SeedAsync(world, rounds: 2, perRound: 2);

        var technician = await ReportAsync(world.Technician, world.WorkOrderId);
        var contact = await ReportAsync(world.AcceptanceContact, world.WorkOrderId);
        var supervisor = await ReportAsync(world.Supervisor, world.WorkOrderId);
        var teamLead = await ReportAsync(world.TeamLead, world.WorkOrderId);

        Assert.Equal(ReportAudience.Technician, technician.Result!.Audience);
        Assert.Equal(ReportAudience.AcceptanceContact, contact.Result!.Audience);
        foreach (var report in new[] { technician, contact })
        {
            Assert.Null(report.Result!.Cost);
            Assert.DoesNotContain(report.Commands, command => command.Sql.Contains("cost_summary", StringComparison.OrdinalIgnoreCase));
        }

        foreach (var report in new[] { supervisor, teamLead })
        {
            Assert.Equal(ReportAudience.TeamLeadSupervisor, report.Result!.Audience);
            var cost = report.Result.Cost;
            Assert.NotNull(cost);
            Assert.Equal(4821.37m, cost.TotalAmount);
            Assert.Single(report.Commands, command => command.Sql.Contains("cost_summary", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task ServiceReport_ReportsOnlyBoundCleanEvidence_AndNeverStorageReferencesOrHashes()
    {
        var world = await EvidenceWorlds.CreateAsync(_host);
        await SeedAsync(world, rounds: 1, perRound: 2);
        // A leftover candidate that was never bound, and a bound file that later (impossibly) is not CLEAN, must not be reported.
        await using (var scope = _host.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RepairRequestDbContext>();
            var file = new FileAsset(world.Scope.TenantId, "leftover.png", "image/png", 1024, ValidHash, $"{world.Scope.TenantId:N}/{Guid.NewGuid():N}", world.Technician.UserId, DateTime.UtcNow);
            file.MarkClean();
            db.FileAssets.Add(file);
            await db.SaveChangesAsync();
            db.WorkEvidenceUploads.Add(WorkEvidenceUpload.Create(world.Scope.TenantId, world.WorkOrderId, world.VisitId, file.Id, WorkEvidenceType.Other, DateTime.UtcNow));
            await db.SaveChangesAsync();
        }

        var report = await ReportAsync(world.Supervisor, world.WorkOrderId);

        var evidence = Assert.Single(report.Result!.Report.WorkSummaries).Evidence;
        Assert.Equal(2, evidence.Count);
        Assert.DoesNotContain(evidence, item => item.FileName == "leftover.png");
        Assert.Equal(new[] { 1, 2 }, evidence.Select(item => item.SlotNo));
        var serialized = System.Text.Json.JsonSerializer.Serialize(report.Result);
        Assert.DoesNotContain(ValidHash, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("storage", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EvidenceList_IsOneSetBasedQueryPerCall_ForEveryAudience()
    {
        var world = await EvidenceWorlds.CreateAsync(_host);
        await SeedAsync(world, rounds: 3, perRound: 10);

        foreach (var user in new[] { world.Technician, world.TeamLead, world.AcceptanceContact })
        {
            await using var scope = _host.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IWorkEvidenceStore>();
            _capture.Commands.Clear();

            var list = await store.ListAsync(user, world.WorkOrderId, None);

            Assert.NotNull(list);
            Assert.Equal(30, list.Value.Items.Count);
            Assert.InRange(_capture.Commands.Count, 2, 4);
            Assert.Single(_capture.Commands, command => command.Sql.Contains("work_evidence_upload", StringComparison.Ordinal));
            Assert.DoesNotContain(_capture.Commands, command => command.Sql.Contains("storage_reference", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task SubmitSelection_IsOneParameterizedQuery_ScopedByTenantWorkOrderAndVisit_ForAtMostTenIds()
    {
        var world = await EvidenceWorlds.CreateAsync(_host);
        await SeedAsync(world, rounds: 1, perRound: 10);
        var ids = await WithIdsAsync(world);

        await using var scope = _host.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IWorkEvidenceStore>();
        _capture.Commands.Clear();

        var rows = await store.LoadSelectionAsync(world.Scope.TenantId, world.WorkOrderId, world.VisitId, ids, None);

        Assert.Equal(10, rows.Count);
        var command = Assert.Single(_capture.Commands);
        Assert.Contains("[tenant_id]", command.Sql, StringComparison.Ordinal);
        Assert.Contains("[work_order_id]", command.Sql, StringComparison.Ordinal);
        Assert.Contains("[service_visit_id]", command.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain(ids[0].ToString(), command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("storage_reference", command.Sql, StringComparison.OrdinalIgnoreCase);

        // The same ids asked for under another tenant, Work Order or Visit resolve to nothing at all.
        Assert.Empty(await store.LoadSelectionAsync(Guid.NewGuid(), world.WorkOrderId, world.VisitId, ids, None));
        Assert.Empty(await store.LoadSelectionAsync(world.Scope.TenantId, Guid.NewGuid(), world.VisitId, ids, None));
        Assert.Empty(await store.LoadSelectionAsync(world.Scope.TenantId, world.WorkOrderId, Guid.NewGuid(), ids, None));
    }

    private async Task<List<Guid>> WithIdsAsync(EvidenceWorld world)
    {
        await using var scope = _host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RepairRequestDbContext>();
        return await db.WorkEvidenceUploads.AsNoTracking().Where(item => item.WorkOrderId == world.WorkOrderId).Select(item => item.Id).ToListAsync();
    }

    [Fact]
    public async Task EvidenceAudience_IsNoneForEveryoneElse_AcrossRolesAndTenants()
    {
        var world = await EvidenceWorlds.CreateAsync(_host);
        var other = await EvidenceWorlds.CreateAsync(_host);
        await using var scope = _host.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IWorkEvidenceStore>();

        Assert.Equal(ReportAudience.Technician, await store.ResolveAudienceAsync(world.Technician, world.WorkOrderId, None));
        Assert.Equal(ReportAudience.TeamLeadSupervisor, await store.ResolveAudienceAsync(world.TeamLead, world.WorkOrderId, None));
        Assert.Equal(ReportAudience.TeamLeadSupervisor, await store.ResolveAudienceAsync(world.Supervisor, world.WorkOrderId, None));
        Assert.Equal(ReportAudience.AcceptanceContact, await store.ResolveAudienceAsync(world.AcceptanceContact, world.WorkOrderId, None));

        foreach (var user in new[] { world.OtherTechnician, world.OtherRequester, other.Technician, other.TeamLead, other.Supervisor, other.AcceptanceContact })
        {
            Assert.Equal(ReportAudience.None, await store.ResolveAudienceAsync(user, world.WorkOrderId, None));
        }

        Assert.Equal(ReportAudience.None, await store.ResolveAudienceAsync(world.Technician, Guid.NewGuid(), None));
    }
}
