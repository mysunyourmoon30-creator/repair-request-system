using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RepairRequest.Application.Attachments;
using RepairRequest.Application.Common;
using RepairRequest.Application.Security;
using RepairRequest.Application.WorkEvidence;
using RepairRequest.Application.WorkOrders;
using RepairRequest.Domain.Files;
using RepairRequest.Domain.Security;
using RepairRequest.Domain.WorkEvidence;
using RepairRequest.Domain.WorkOrders;
using RepairRequest.Infrastructure.Persistence;
using RepairRequest.IntegrationTests.Authentication;
using RepairRequest.IntegrationTests.Authorization;

namespace RepairRequest.IntegrationTests.WorkEvidence;

/// <summary>In-memory private storage with failure injection; keys are opaque and never overwritten.</summary>
internal sealed class InMemoryEvidenceStorage : IFileStorage
{
    private readonly object _gate = new();
    private readonly Dictionary<string, byte[]> _objects = new();

    public bool FailSave { get; set; }

    public List<string> Deleted { get; } = [];

    public int ObjectCount
    {
        get
        {
            lock (_gate)
            {
                return _objects.Count;
            }
        }
    }

    public async Task SaveAsync(string storageKey, Stream content, CancellationToken cancellationToken)
    {
        if (FailSave)
        {
            throw new IOException("Simulated storage failure.");
        }

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        lock (_gate)
        {
            _objects.Add(storageKey, buffer.ToArray());
        }
    }

    public Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return _objects.TryGetValue(storageKey, out var bytes)
                ? Task.FromResult<Stream>(new MemoryStream(bytes, writable: false))
                : throw new FileNotFoundException("Simulated missing object.");
        }
    }

    public Task DeleteAsync(string storageKey, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            Deleted.Add(storageKey);
            _objects.Remove(storageKey);
        }

        return Task.CompletedTask;
    }
}

/// <summary>Deterministic scanner double: the content decides the outcome, so CLEAN, malware, unavailable and a throwing provider are all provable.</summary>
internal sealed class DeterministicEvidenceScanner : IFileMalwareScanner
{
    public const string InfectedMarker = "INFECTED-TEST-MARKER";
    public const string UnavailableMarker = "UNAVAILABLE-TEST-MARKER";
    public const string ThrowMarker = "THROW-TEST-MARKER";

    public async Task<MalwareScanOutcome> ScanAsync(Stream content, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(content, Encoding.ASCII);
        var text = await reader.ReadToEndAsync(cancellationToken);
        if (text.Contains(ThrowMarker, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Simulated scanner failure.");
        }

        if (text.Contains(UnavailableMarker, StringComparison.Ordinal))
        {
            return MalwareScanOutcome.Unavailable;
        }

        return text.Contains(InfectedMarker, StringComparison.Ordinal) ? MalwareScanOutcome.Infected : MalwareScanOutcome.Clean;
    }
}

/// <summary>A tenant with a Work Order IN_PROGRESS, its assigned Technician and the Work Session whose Visit the next Submit describes.</summary>
internal sealed record EvidenceWorld(
    ScopeWorld Scope,
    CurrentUser Technician,
    CurrentUser OtherTechnician,
    CurrentUser TeamLead,
    CurrentUser Supervisor,
    CurrentUser AcceptanceContact,
    CurrentUser OtherRequester,
    Guid RepairRequestId,
    Guid WorkOrderId,
    Guid VisitId,
    Guid SessionId);

internal static class EvidenceTestFiles
{
    public static byte[] Png(string payload = "evidence-bytes") => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. Encoding.ASCII.GetBytes(payload)];

    public static WorkEvidenceFile File(byte[] content, string name = "evidence.png") =>
        new(name, "image/png", content.Length, () => new MemoryStream(content, writable: false));
}

internal static class EvidenceWorlds
{
    /// <summary>
    /// Seeds, directly through the domain model, a Work Order that is IN_PROGRESS with an Initial Visit assigned to the Technician and one Work
    /// Session (CHECKED_OUT by default, so a Work Summary can be submitted). A direct seed keeps every test independent of the lifecycle endpoints.
    /// </summary>
    public static async Task<EvidenceWorld> CreateAsync(AuthenticationTestHost host, bool checkedOut = true)
    {
        Guid tenantId;
        ScopeWorld scope;
        await using (var scopeHandle = host.CreateScope())
        {
            scope = await ScopeWorld.CreateAsync(scopeHandle.ServiceProvider.GetRequiredService<RepairRequestDbContext>());
            tenantId = scope.TenantId;
        }

        async Task<CurrentUser> UserAsync(Guid tenant, Domain.MasterData.Site[] sites, string role)
        {
            var user = await host.CreateUserInTenantAsync(tenant, role);
            await using var handle = host.CreateScope();
            if (sites.Length > 0)
            {
                await ScopeWorld.AssignSitesAsync(handle.ServiceProvider.GetRequiredService<RepairRequestDbContext>(), tenant, user.Id, sites);
            }

            return new CurrentUser(user.Id, tenant, [role]);
        }

        var technician = await UserAsync(tenantId, [scope.SiteA1], RoleCodes.Technician);
        var otherTechnician = await UserAsync(tenantId, [scope.SiteA1], RoleCodes.Technician);
        var teamLead = await UserAsync(tenantId, [scope.SiteA1], RoleCodes.TeamLead);
        var supervisor = await UserAsync(tenantId, [scope.SiteA1], RoleCodes.Supervisor);
        var contact = await UserAsync(tenantId, [scope.SiteA1], RoleCodes.Requester);
        var otherRequester = await UserAsync(tenantId, [scope.SiteA1], RoleCodes.Requester);

        await using var seed = host.CreateScope();
        var db = seed.ServiceProvider.GetRequiredService<RepairRequestDbContext>();

        // The Repair Request was created by another Requester: being the creator or a Site Requester grants nothing; only the designated contact does.
        var requestId = await ScopeWorld.AddRequestAsync(db, tenantId, otherRequester.UserId, scope.SiteA1);
        var workOrderId = await ScopeWorld.AddWorkOrderAsync(db, tenantId, requestId, $"WO-{Guid.NewGuid():N}"[..14], DateTime.UtcNow);

        var workOrder = await db.WorkOrders.SingleAsync(item => item.Id == workOrderId);
        workOrder.Schedule(Guid.NewGuid());
        workOrder.BeginWork();
        await db.SaveChangesAsync();

        // WO-008: the exact Acceptance Contact (designated by Submit for Acceptance in the real flow).
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [work_order] SET [acceptance_contact_id] = {contact.UserId} WHERE [work_order_id] = {workOrderId}");

        var visit = ServiceVisit.Create(
            tenantId, workOrderId, ServiceVisitType.Initial, Guid.NewGuid(), technician.UserId, DateTime.UtcNow.AddHours(1), DateTime.UtcNow.AddHours(3));
        visit.CheckIn();
        db.ServiceVisits.Add(visit);

        var session = WorkSession.Create(tenantId, visit.Id, technician.UserId, DateTime.UtcNow.AddHours(-2));
        if (checkedOut)
        {
            session.CheckOut(DateTime.UtcNow.AddHours(-1));
        }

        db.WorkSessions.Add(session);
        await db.SaveChangesAsync();

        return new EvidenceWorld(scope, technician, otherTechnician, teamLead, supervisor, contact, otherRequester, requestId, workOrderId, visit.Id, session.Id);
    }

    /// <summary>Adds a further Visit with an own, later Work Session for the Technician (the corrective round) and puts the Work Order back IN_PROGRESS.</summary>
    public static async Task<(Guid VisitId, Guid SessionId)> AddSecondRoundAsync(AuthenticationTestHost host, EvidenceWorld world)
    {
        await using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RepairRequestDbContext>();

        var visit = ServiceVisit.Create(
            world.Scope.TenantId, world.WorkOrderId, ServiceVisitType.Corrective, Guid.NewGuid(), world.Technician.UserId,
            DateTime.UtcNow.AddHours(5), DateTime.UtcNow.AddHours(7));
        visit.CheckIn();
        db.ServiceVisits.Add(visit);
        var session = WorkSession.Create(world.Scope.TenantId, visit.Id, world.Technician.UserId, DateTime.UtcNow);
        session.CheckOut(DateTime.UtcNow.AddMinutes(30));
        db.WorkSessions.Add(session);
        await db.SaveChangesAsync();

        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [work_order] SET [status] = 'IN_PROGRESS' WHERE [work_order_id] = {world.WorkOrderId}");
        return (visit.Id, session.Id);
    }

    /// <summary>Another IN_PROGRESS Work Order of the same tenant and Site, with its own Visit and Work Session of the same Technician.</summary>
    public static async Task<(Guid WorkOrderId, Guid VisitId)> AddOtherWorkOrderAsync(AuthenticationTestHost host, EvidenceWorld world)
    {
        await using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RepairRequestDbContext>();

        var tenantId = world.Scope.TenantId;
        var requestId = await ScopeWorld.AddRequestAsync(db, tenantId, world.OtherRequester.UserId, world.Scope.SiteA1);
        var workOrderId = await ScopeWorld.AddWorkOrderAsync(db, tenantId, requestId, $"WO-{Guid.NewGuid():N}"[..14], DateTime.UtcNow);
        var workOrder = await db.WorkOrders.SingleAsync(item => item.Id == workOrderId);
        workOrder.Schedule(Guid.NewGuid());
        workOrder.BeginWork();
        await db.SaveChangesAsync();

        var visit = ServiceVisit.Create(
            tenantId, workOrderId, ServiceVisitType.Initial, Guid.NewGuid(), world.Technician.UserId, DateTime.UtcNow.AddHours(1), DateTime.UtcNow.AddHours(3));
        visit.CheckIn();
        db.ServiceVisits.Add(visit);
        var session = WorkSession.Create(tenantId, visit.Id, world.Technician.UserId, DateTime.UtcNow.AddHours(-2));
        session.CheckOut(DateTime.UtcNow.AddHours(-1));
        db.WorkSessions.Add(session);
        await db.SaveChangesAsync();
        return (workOrderId, visit.Id);
    }

    public static async Task<byte[]> RowVersionAsync(AuthenticationTestHost host, Guid workOrderId)
    {
        await using var scope = host.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<RepairRequestDbContext>().WorkOrders.AsNoTracking()
            .Where(item => item.Id == workOrderId).Select(item => item.RowVersion).SingleAsync();
    }

    public static CommandContext Context(CurrentUser user) => new(user, Guid.NewGuid());
}
