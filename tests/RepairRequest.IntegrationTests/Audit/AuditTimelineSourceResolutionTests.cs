using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RepairRequest.Application.Audit;
using RepairRequest.Application.Security;
using RepairRequest.Domain.Security;
using RepairRequest.Domain.WorkOrders;
using RepairRequest.Infrastructure.Persistence;
using RepairRequest.IntegrationTests.Audit.Support;
using RepairRequest.IntegrationTests.Authentication;
using RepairRequest.IntegrationTests.Authorization;
using RepairRequest.IntegrationTests.Persistence;

namespace RepairRequest.IntegrationTests.Audit;

/// <summary>
/// The production store's server-side source resolution (docs/15 D2/D3): the Work Order is found only through
/// <see cref="IDataScope.WorkOrders"/> (tenant, Site, REQUESTER ownership) and the Visit / Work Session / Corrective
/// Action ids are derived from it on the server — never from the client. Real rows, real scope, real LocalDB.
/// </summary>
[Collection(PersistenceDatabaseCollection.Name)]
public sealed class AuditTimelineSourceResolutionTests : IAsyncLifetime
{
    private readonly AuthenticationTestHost _host = new();

    public AuditTimelineSourceResolutionTests(PersistenceDatabaseFixture database)
    {
        _ = database;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private async Task<T> WithDbAsync<T>(Func<RepairRequestDbContext, Task<T>> action)
    {
        await using var scope = _host.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<RepairRequestDbContext>());
    }

    private async Task<T> WithStoreAsync<T>(Func<IAuditTimelineStore, Task<T>> action)
    {
        await using var scope = _host.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<IAuditTimelineStore>());
    }

    private async Task<CurrentUser> UserAsync(ScopeWorld world, Domain.MasterData.Site[] sites, params string[] roles)
    {
        var user = await _host.CreateUserInTenantAsync(world.TenantId, roles);
        await WithDbAsync(async db =>
        {
            await ScopeWorld.AssignSitesAsync(db, world.TenantId, user.Id, sites);
            return true;
        });
        return new CurrentUser(user.Id, world.TenantId, roles);
    }

    private sealed record World(ScopeWorld Scope, Guid RequesterId, Guid RepairRequestId, Guid WorkOrderId);

    private async Task<World> NewWorldAsync()
    {
        var scope = await WithDbAsync(ScopeWorld.CreateAsync);
        var requester = await _host.CreateUserInTenantAsync(scope.TenantId, RoleCodes.Requester);
        var repairRequestId = await WithDbAsync(db => ScopeWorld.AddRequestAsync(db, scope.TenantId, requester.Id, scope.SiteA1));
        var workOrderId = await WithDbAsync(db => ScopeWorld.AddWorkOrderAsync(db, scope.TenantId, repairRequestId, $"WO-{Guid.NewGuid():N}"[..14], DateTime.UtcNow));
        return new World(scope, requester.Id, repairRequestId, workOrderId);
    }

    /// <summary>A CHECKED_OUT session: the unique "one active session per technician" index only covers active sessions.</summary>
    private static WorkSession NewClosedSession(Guid tenantId, Guid visitId, Guid technicianId)
    {
        var session = WorkSession.Create(tenantId, visitId, technicianId, DateTime.UtcNow.AddHours(-2));
        session.CheckOut(DateTime.UtcNow.AddHours(-1));
        return session;
    }

    private static ServiceVisit NewVisit(Guid tenantId, Guid workOrderId, Guid technicianId) =>
        ServiceVisit.Create(tenantId, workOrderId, ServiceVisitType.Initial, Guid.NewGuid(), technicianId, DateTime.UtcNow.AddDays(1), DateTime.UtcNow.AddDays(1).AddHours(2));

    // ------------------------------------------------------------------ scope

    [Theory]
    [InlineData(RoleCodes.Supervisor)]
    [InlineData(RoleCodes.Coordinator)]
    [InlineData(RoleCodes.TeamLead)]
    [InlineData(RoleCodes.Approver)]
    public async Task SiteWideRoles_AtTheWorkOrdersSite_ResolveTheSources(string role)
    {
        var world = await NewWorldAsync();
        var user = await UserAsync(world.Scope, [world.Scope.SiteA1], role);

        var sources = await WithStoreAsync(store => store.ResolveWorkOrderSourcesAsync(user, world.WorkOrderId, CancellationToken.None));

        Assert.NotNull(sources);
        Assert.Equal(world.WorkOrderId, sources.WorkOrderId);
        Assert.Equal(world.RepairRequestId, sources.RepairRequestId);
        Assert.Equal(world.Scope.TenantId, sources.TenantId);
    }

    [Fact]
    public async Task Requester_ResolvesOnlyTheirOwnWorkOrder()
    {
        var world = await NewWorldAsync();
        var owner = new CurrentUser(world.RequesterId, world.Scope.TenantId, [RoleCodes.Requester]);
        var other = await UserAsync(world.Scope, [world.Scope.SiteA1], RoleCodes.Requester);

        Assert.NotNull(await WithStoreAsync(store => store.ResolveWorkOrderSourcesAsync(owner, world.WorkOrderId, CancellationToken.None)));
        Assert.Null(await WithStoreAsync(store => store.ResolveWorkOrderSourcesAsync(other, world.WorkOrderId, CancellationToken.None)));
    }

    [Fact]
    public async Task OutOfScope_Missing_OtherTenant_Technician_Administrator_AllResolveToNull()
    {
        var world = await NewWorldAsync();
        var otherSite = await UserAsync(world.Scope, [world.Scope.SiteA2], RoleCodes.Supervisor);
        var noSites = await UserAsync(world.Scope, [], RoleCodes.Supervisor);
        var technician = await UserAsync(world.Scope, [world.Scope.SiteA1], RoleCodes.Technician);
        var administrator = await UserAsync(world.Scope, [world.Scope.SiteA1], RoleCodes.Administrator);
        var otherTenantUser = await _host.CreateUserInTenantAsync(world.Scope.OtherTenantId, RoleCodes.Supervisor);
        await WithDbAsync(async db =>
        {
            await ScopeWorld.AssignSitesAsync(db, world.Scope.OtherTenantId, otherTenantUser.Id, world.Scope.OtherTenantSite);
            return true;
        });
        var otherTenant = new CurrentUser(otherTenantUser.Id, world.Scope.OtherTenantId, [RoleCodes.Supervisor]);
        var supervisor = await UserAsync(world.Scope, [world.Scope.SiteA1], RoleCodes.Supervisor);

        foreach (var user in new[] { otherSite, noSites, technician, administrator, otherTenant })
        {
            Assert.Null(await WithStoreAsync(store => store.ResolveWorkOrderSourcesAsync(user, world.WorkOrderId, CancellationToken.None)));
        }

        Assert.Null(await WithStoreAsync(store => store.ResolveWorkOrderSourcesAsync(supervisor, Guid.NewGuid(), CancellationToken.None)));
    }

    // ------------------------------------------------------------------ child-id derivation

    [Fact]
    public async Task ChildIds_AreDerivedOnTheServer_OnlyFromThisWorkOrder()
    {
        var world = await NewWorldAsync();
        var tenantId = world.Scope.TenantId;
        var user = await UserAsync(world.Scope, [world.Scope.SiteA1], RoleCodes.Supervisor);

        // A second Work Order with its own children must never leak into the first one's source set.
        var otherRequest = await WithDbAsync(db => ScopeWorld.AddRequestAsync(db, tenantId, world.RequesterId, world.Scope.SiteA1));
        var otherWorkOrder = await WithDbAsync(db => ScopeWorld.AddWorkOrderAsync(db, tenantId, otherRequest, $"WO-{Guid.NewGuid():N}"[..14], DateTime.UtcNow));

        var technicianId = (await _host.CreateUserInTenantAsync(tenantId, RoleCodes.Technician)).Id;
        var (visitIds, sessionIds, actionIds) = await WithDbAsync(async db =>
        {
            var visits = new[] { NewVisit(tenantId, world.WorkOrderId, technicianId), NewVisit(tenantId, world.WorkOrderId, technicianId), NewVisit(tenantId, otherWorkOrder, technicianId) };
            db.ServiceVisits.AddRange(visits);
            await db.SaveChangesAsync();

            var sessions = new[]
            {
                NewClosedSession(tenantId, visits[0].Id, technicianId),
                NewClosedSession(tenantId, visits[1].Id, technicianId),
                NewClosedSession(tenantId, visits[2].Id, technicianId),
            };
            db.WorkSessions.AddRange(sessions);

            var acceptance1 = CustomerAcceptance.Reject(tenantId, world.WorkOrderId, 1, world.RequesterId, "Leak persists.", DateTime.UtcNow);
            var acceptance2 = CustomerAcceptance.Reject(tenantId, world.WorkOrderId, 2, world.RequesterId, "Still leaking.", DateTime.UtcNow);
            var acceptanceOther = CustomerAcceptance.Reject(tenantId, otherWorkOrder, 1, world.RequesterId, "Other.", DateTime.UtcNow);
            db.CustomerAcceptances.AddRange(acceptance1, acceptance2, acceptanceOther);
            await db.SaveChangesAsync();

            var actions = new[]
            {
                CorrectiveAction.CreateDraft(tenantId, world.WorkOrderId, acceptance1.Id, 1),
                CorrectiveAction.CreateDraft(tenantId, world.WorkOrderId, acceptance2.Id, 2),
                CorrectiveAction.CreateDraft(tenantId, otherWorkOrder, acceptanceOther.Id, 1),
            };
            db.CorrectiveActions.AddRange(actions);
            await db.SaveChangesAsync();

            return (visits.Take(2).Select(v => v.Id).ToList(), sessions.Take(2).Select(s => s.Id).ToList(), actions.Take(2).Select(a => a.Id).ToList());
        });

        var sources = await WithStoreAsync(store => store.ResolveWorkOrderSourcesAsync(user, world.WorkOrderId, CancellationToken.None));

        Assert.NotNull(sources);
        Assert.Equal(visitIds.OrderBy(g => g), sources.VisitIds.OrderBy(g => g));
        Assert.Equal(sessionIds.OrderBy(g => g), sources.SessionIds.OrderBy(g => g));
        Assert.Equal(actionIds.OrderBy(g => g), sources.CorrectiveActionIds.OrderBy(g => g));
    }

    [Fact]
    public async Task MoreThan2100RealChildIds_ResolveAndReadThroughOneBoundedCommand()
    {
        var world = await NewWorldAsync();
        var tenantId = world.Scope.TenantId;
        var user = await UserAsync(world.Scope, [world.Scope.SiteA1], RoleCodes.Supervisor);

        // 1,150 Visits, each with a Work Session (2,300 real child ids, above SQL Server's 2,100 parameters-per-command limit).
        var technicianId = (await _host.CreateUserInTenantAsync(tenantId, RoleCodes.Technician)).Id;
        await WithDbAsync(async db =>
        {
            var visits = Enumerable.Range(0, 1150).Select(_ => NewVisit(tenantId, world.WorkOrderId, technicianId)).ToList();
            db.ServiceVisits.AddRange(visits);
            await db.SaveChangesAsync();
            db.WorkSessions.AddRange(visits.Select(v => NewClosedSession(tenantId, v.Id, technicianId)));
            await db.SaveChangesAsync();
            return true;
        });

        var sources = await WithStoreAsync(store => store.ResolveWorkOrderSourcesAsync(user, world.WorkOrderId, CancellationToken.None));
        Assert.NotNull(sources);
        Assert.Equal(1150, sources.VisitIds.Count);
        Assert.Equal(1150, sources.SessionIds.Count);
        Assert.True(sources.VisitIds.Count + sources.SessionIds.Count > 2100);

        // One audit row per child id (real audit_history entities); the whole timeline is then read through the production
        // query and walked exactly once.
        var expected = await WithDbAsync(async db =>
        {
            var now = DateTime.UtcNow;
            var rows = sources.VisitIds.Select((id, i) => new Domain.Auditing.AuditHistory(
                    tenantId, "SERVICE_VISIT", id, "SERVICE_VISIT_CHECKED_IN", null, null, null, null, null, Guid.NewGuid(), now.AddSeconds(-i % 50), Guid.NewGuid()))
                .Concat(sources.SessionIds.Select((id, i) => new Domain.Auditing.AuditHistory(
                    tenantId, "WORK_SESSION", id, "WORK_SESSION_PAUSED", null, null, null, null, null, Guid.NewGuid(), now.AddSeconds(-i % 50), Guid.NewGuid())))
                .ToList();
            db.AuditHistory.AddRange(rows);
            await db.SaveChangesAsync();
            return rows.Select(r => r.Id).ToHashSet();
        });

        var pages = await WithStoreAsync(store => TimelineWalker.WalkAsync(store, sources, 100));
        var ids = pages.SelectMany(p => p.Items).Select(r => r.AuditId).ToList();

        Assert.Equal(2300, ids.Count);
        Assert.Equal(2300, ids.Distinct().Count());
        Assert.Empty(expected.Except(ids));
        Assert.Equal(23, pages.Count);
    }
}
