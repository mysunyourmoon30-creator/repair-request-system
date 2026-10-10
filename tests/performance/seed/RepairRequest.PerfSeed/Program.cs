using System.Data;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RepairRequest.Application.Audit;
using RepairRequest.Application.Common;
using RepairRequest.Application.DependencyInjection;
using RepairRequest.Application.RepairRequests;
using RepairRequest.Application.WorkOrders;
using RepairRequest.Domain.MasterData;
using RepairRequest.Domain.Security;
using RepairRequest.Domain.WorkOrders;
using RepairRequest.Infrastructure.DependencyInjection;
using RepairRequest.Infrastructure.Identity;
using RepairRequest.Infrastructure.Persistence;
using RepairRequestAggregate = RepairRequest.Domain.RepairRequests.RepairRequest;

// Performance seed for the Work Order timeline evidence (docs/15 §13).
//   reset   drop + recreate the dedicated perf database from the real EF migrations
//   seed    load the profiles + background volume, write the fixture JSON (PERF_FIXTURE_PATH, never inside the repo)
//   counts  row counts of the main tables and per-profile expected vs actual timeline sizes
//   verify  walk sample timelines through the running API and compare with a SQL Server ORDER BY oracle
// Environment: PERF_DB (default RepairRequestDb_PerfTimeline), PERF_FIXTURE_PATH, PERF_NOISE_ROWS (500000),
// PERF_BACKGROUND_WORK_ORDERS (5000), PERF_BASE_URL (http://127.0.0.1:5199).
// No credential is stored in source: the test password is generated per seed run and only written to the fixture file.

var Base = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
var command = args.Length > 0 ? args[0] : "help";
var database = Environment.GetEnvironmentVariable("PERF_DB") ?? "RepairRequestDb_PerfTimeline";
var connectionString = $"Server=(localdb)\\MSSQLLocalDB;Database={database};Trusted_Connection=True;TrustServerCertificate=True;Encrypt=False";

switch (command)
{
    case "reset": await Reset(); break;
    case "seed": await Seed(); break;
    case "counts": await Counts(); break;
    case "verify": await Verify(); break;
    default: Console.WriteLine("usage: reset | seed | counts | verify"); return 2;
}

return 0;

RepairRequestDbContext NewContext() =>
    new(new DbContextOptionsBuilder<RepairRequestDbContext>().UseSqlServer(connectionString).Options);

async Task Reset()
{
    await using var db = NewContext();
    await db.Database.EnsureDeletedAsync();
    await db.Database.MigrateAsync();
    Console.WriteLine($"reset: database {database} recreated from migrations.");
}

// ------------------------------------------------------------------------------------------------ seed

async Task Seed()
{
    var fixturePath = Environment.GetEnvironmentVariable("PERF_FIXTURE_PATH") ?? throw new InvalidOperationException("PERF_FIXTURE_PATH is required (a path outside the repository).");
    var noiseRows = int.TryParse(Environment.GetEnvironmentVariable("PERF_NOISE_ROWS"), out var n) ? n : 500_000;
    var background = int.TryParse(Environment.GetEnvironmentVariable("PERF_BACKGROUND_WORK_ORDERS"), out var b) ? b : 5_000;
    var password = $"Pf-{Convert.ToHexString(RandomNumberGenerator.GetBytes(12))}-aZ9!";
    var watch = Stopwatch.StartNew();

    var settings = new Dictionary<string, string?>
    {
        ["ConnectionStrings:DefaultConnection"] = connectionString,
        ["Authentication:Jwt:Issuer"] = "RepairRequest.Api",
        ["Authentication:Jwt:Audience"] = "RepairRequest.Web",
        ["Authentication:Jwt:AccessTokenLifetime"] = "00:15:00",
        ["Authentication:Jwt:SigningKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        ["Authentication:RefreshToken:Lifetime"] = "7.00:00:00",
    };
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
    services.AddApplication();
    await using var provider = services.BuildServiceProvider();

    // ---- master data: tenant T (Customer A: Site A, Site B) and another tenant (Site C)
    var tenant = Guid.NewGuid();
    var otherTenant = Guid.NewGuid();
    Site siteA, siteB, siteC;
    await using (var db = NewContext())
    {
        var customer = new Customer(tenant, "PERF-CUST");
        var otherCustomer = new Customer(otherTenant, "PERF-CUST");
        db.Customers.AddRange(customer, otherCustomer);
        siteA = new Site(tenant, customer.Id, "PERF-SITE-A");
        siteB = new Site(tenant, customer.Id, "PERF-SITE-B");
        siteC = new Site(otherTenant, otherCustomer.Id, "PERF-SITE-C");
        db.Sites.AddRange(siteA, siteB, siteC);
        db.Equipment.AddRange(new Equipment(tenant, siteA.Id, "EQ-A"), new Equipment(tenant, siteB.Id, "EQ-B"), new Equipment(otherTenant, siteC.Id, "EQ-C"));
        await db.SaveChangesAsync();
    }

    // ---- users (Identity; password hashing through UserManager)
    var users = new Dictionary<string, (Guid Id, string Email)>();
    async Task AddUser(string key, Guid tenantId, Site site, string role)
    {
        await using var scope = provider.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var email = $"perf-{key}-{Guid.NewGuid():N}@example.test";
        var user = new ApplicationUser { TenantId = tenantId, Email = email, UserName = email };
        Check(await manager.CreateAsync(user, password), key);
        Check(await manager.AddToRoleAsync(user, role), key);
        await using var db = NewContext();
        db.UserSiteScopes.Add(new UserSiteScope(tenantId, user.Id, site.Id));
        await db.SaveChangesAsync();
        users[key] = (user.Id, email);
    }

    await AddUser("supervisor", tenant, siteA, RoleCodes.Supervisor);
    await AddUser("coordinator", tenant, siteA, RoleCodes.Coordinator);
    await AddUser("teamLead", tenant, siteA, RoleCodes.TeamLead);
    await AddUser("approver", tenant, siteA, RoleCodes.Approver);
    await AddUser("requester", tenant, siteA, RoleCodes.Requester);
    await AddUser("requesterOther", tenant, siteA, RoleCodes.Requester);
    await AddUser("technician", tenant, siteA, RoleCodes.Technician);
    await AddUser("administrator", tenant, siteA, RoleCodes.Administrator);
    await AddUser("supervisorOtherSite", tenant, siteB, RoleCodes.Supervisor);
    await AddUser("supervisorOtherTenant", otherTenant, siteC, RoleCodes.Supervisor);
    Console.WriteLine($"users: {users.Count} ({watch.Elapsed.TotalSeconds:F0}s)");

    var requesterId = users["requester"].Id;
    var technicianId = users["technician"].Id;
    var knownActors = new[] { users["supervisor"].Id, users["coordinator"].Id, users["teamLead"].Id, users["approver"].Id, requesterId, technicianId };

    // ---- profile Work Orders with real children and audit rows
    var rng = new Random(20261010);
    var profiles = new[]
    {
        new Profile("SMALL", 100, 6, 2, 3, 2, 2, 1, 3),
        new Profile("MEDIUM", 50, 32, 8, 10, 8, 6, 3, 13),
        new Profile("LARGE", 20, 309, 40, 20, 40, 12, 10, 41),
        new Profile("MANYIDS", 10, 20, 100, 3, 100, 3, 30, 3),
        new Profile("BIGIDS", 5, 20, 1300, 1, 1000, 1, 100, 1),
    };

    var manifest = new Dictionary<string, List<SeededWorkOrder>>();
    using var sink = new RowSink(connectionString);
    foreach (var profile in profiles)
    {
        var list = new List<SeededWorkOrder>();
        for (var i = 0; i < profile.WorkOrders; i++)
        {
            list.Add(await SeedWorkOrderAsync(profile, i, tenant, siteA, requesterId, technicianId, knownActors, rng, sink));
        }

        manifest[profile.Name] = list;
        Console.WriteLine($"profile {profile.Name}: {list.Count} work orders, {profile.Events} events each, {profile.ChildIds} child ids each ({watch.Elapsed.TotalSeconds:F0}s)");
    }

    // ---- negative-case work orders: another Site (same tenant) and another tenant
    var otherSiteWo = await CreateBareWorkOrderAsync(tenant, siteB, requesterId, "NEG-SITEB");
    var otherTenantWo = await CreateBareWorkOrderAsync(otherTenant, siteC, users["supervisorOtherTenant"].Id, "NEG-TENANT");
    foreach (var (type, id, tenantId) in new[] { (WorkOrderAudit.WorkOrderEntityType, otherSiteWo.WorkOrderId, tenant), (WorkOrderAudit.WorkOrderEntityType, otherTenantWo.WorkOrderId, otherTenant) })
    {
        for (var i = 0; i < 5; i++)
        {
            sink.Add(tenantId, type, id, "WORK_ORDER_SCHEDULED", Base.AddDays(3).AddSeconds(i), Guid.NewGuid());
        }
    }

    await sink.FlushAsync();

    // ---- background volume: Work Orders (no audit of their own) and noise audit rows
    await SeedBackgroundWorkOrdersAsync(background, tenant, otherTenant, siteA, siteB, siteC, requesterId, users["supervisorOtherTenant"].Id);
    Console.WriteLine($"background work orders: {background} ({watch.Elapsed.TotalSeconds:F0}s)");
    await SeedNoiseAsync(noiseRows, new[] { tenant, otherTenant, Guid.NewGuid() }, sink);
    Console.WriteLine($"noise audit rows: {noiseRows} ({watch.Elapsed.TotalSeconds:F0}s)");

    await using (var connection = new SqlConnection(connectionString))
    {
        await connection.OpenAsync();
        foreach (var statement in new[] { "UPDATE STATISTICS audit_history WITH FULLSCAN", "UPDATE STATISTICS work_orders WITH FULLSCAN" })
        {
            await using var command2 = connection.CreateCommand();
            command2.CommandText = statement;
            command2.CommandTimeout = 0;
            try { await command2.ExecuteNonQueryAsync(); } catch (SqlException) { /* table name differs: statistics are auto-updated anyway */ }
        }
    }

    var fixture = new
    {
        generatedAtUtc = DateTime.UtcNow,
        database,
        password,
        tenantId = tenant,
        otherTenantId = otherTenant,
        users = users.ToDictionary(pair => pair.Key, pair => pair.Value.Email),
        profiles = manifest.ToDictionary(pair => pair.Key, pair => pair.Value),
        negative = new
        {
            otherSiteWorkOrderId = otherSiteWo.WorkOrderId,
            otherTenantWorkOrderId = otherTenantWo.WorkOrderId,
            missingWorkOrderId = Guid.NewGuid(),
            workOrderId = manifest["SMALL"][0].WorkOrderId,
        },
        background = new { workOrders = background, noiseAuditRows = noiseRows },
    };
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(fixturePath))!);
    await File.WriteAllTextAsync(fixturePath, JsonSerializer.Serialize(fixture, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    Console.WriteLine($"seed done in {watch.Elapsed.TotalMinutes:F1} min; fixture: {fixturePath}");
}

static void Check(IdentityResult result, string what)
{
    if (!result.Succeeded)
    {
        throw new InvalidOperationException($"{what}: {string.Join("; ", result.Errors.Select(e => e.Code))}");
    }
}

async Task<(Guid RepairRequestId, Guid WorkOrderId)> CreateBareWorkOrderAsync(Guid tenantId, Site site, Guid createdBy, string number)
{
    await using var db = NewContext();
    var request = RepairRequestAggregate.CreateDraft(tenantId, createdBy);
    db.RepairRequests.Add(request).Property(r => r.SiteId).CurrentValue = site.Id;
    await db.SaveChangesAsync();
    var workOrder = WorkOrder.Create(tenantId, request.Id, $"PF-{number}"[..Math.Min(14, $"PF-{number}".Length)], DateTime.UtcNow);
    db.WorkOrders.Add(workOrder);
    await db.SaveChangesAsync();
    return (request.Id, workOrder.Id);
}

async Task<SeededWorkOrder> SeedWorkOrderAsync(
    Profile profile, int index, Guid tenant, Site site, Guid requesterId, Guid technicianId, Guid[] actors, Random rng, RowSink sink)
{
    await using var db = NewContext();
    var request = RepairRequestAggregate.CreateDraft(tenant, requesterId);
    db.RepairRequests.Add(request).Property(r => r.SiteId).CurrentValue = site.Id;
    await db.SaveChangesAsync();
    var workOrder = WorkOrder.Create(tenant, request.Id, $"PF-{profile.Code}{index:D5}", DateTime.UtcNow);
    db.WorkOrders.Add(workOrder);
    await db.SaveChangesAsync();

    var visits = Enumerable.Range(0, profile.Visits)
        .Select(_ => ServiceVisit.Create(tenant, workOrder.Id, ServiceVisitType.Initial, Guid.NewGuid(), technicianId, DateTime.UtcNow.AddDays(1), DateTime.UtcNow.AddDays(1).AddHours(2)))
        .ToList();
    db.ServiceVisits.AddRange(visits);
    await db.SaveChangesAsync();

    var sessions = visits.Take(profile.Sessions).Select(v =>
    {
        var s = WorkSession.Create(tenant, v.Id, technicianId, DateTime.UtcNow.AddHours(-2));
        s.CheckOut(DateTime.UtcNow.AddHours(-1));
        return s;
    }).ToList();
    db.WorkSessions.AddRange(sessions);

    var acceptances = Enumerable.Range(1, profile.CorrectiveActions)
        .Select(round => CustomerAcceptance.Reject(tenant, workOrder.Id, round, requesterId, "Perf seed.", DateTime.UtcNow)).ToList();
    db.CustomerAcceptances.AddRange(acceptances);
    await db.SaveChangesAsync();

    var actions = acceptances.Select((a, i) => CorrectiveAction.CreateDraft(tenant, workOrder.Id, a.Id, i + 1)).ToList();
    db.CorrectiveActions.AddRange(actions);
    await db.SaveChangesAsync();

    // Audit rows: timestamps from a pool that produces frequent same-millisecond ties; actors are real users, or random (UNKNOWN).
    var events = profile.Events - 1;
    var poolSize = Math.Max(3, events / 4);
    DateTime Stamp() => Base.AddDays(5).AddSeconds(rng.Next(poolSize) * 13).AddMilliseconds(250);
    Guid Actor() => rng.Next(10) < 8 ? actors[rng.Next(actors.Length)] : Guid.NewGuid();

    void Source(string type, Guid id, int count, string[] codes)
    {
        for (var i = 0; i < count; i++)
        {
            sink.Add(tenant, type, id, codes[i % codes.Length], Stamp(), Actor());
        }
    }

    string[] woCodes = ["WORK_ORDER_SCHEDULED", "WORK_ORDER_STARTED", "WORK_SUMMARY_SUBMITTED", "WORK_ORDER_CLOSED"];
    string[] visitCodes = ["SERVICE_VISIT_CHECKED_IN", "SERVICE_VISIT_COMPLETED", "SERVICE_VISIT_CANCELLED"];
    string[] sessionCodes = ["WORK_SESSION_PAUSED", "WORK_SESSION_RESUMED", "WORK_SESSION_CHECKED_OUT"];
    string[] actionCodes = ["CORRECTIVE_ACTION_PLAN_SUBMITTED", "CORRECTIVE_ACTION_PLAN_APPROVED", "CORRECTIVE_ACTION_REWORK_SCHEDULED"];

    Source(WorkOrderAudit.WorkOrderEntityType, workOrder.Id, profile.WorkOrderEvents, woCodes);
    foreach (var v in visits) { Source(WorkOrderAudit.ServiceVisitEntityType, v.Id, profile.PerVisit, visitCodes); }
    foreach (var s in sessions) { Source(WorkOrderAudit.WorkSessionEntityType, s.Id, profile.PerSession, sessionCodes); }
    foreach (var a in actions) { Source(WorkOrderAudit.CorrectiveActionEntityType, a.Id, profile.PerCorrectiveAction, actionCodes); }
    sink.Add(tenant, RepairRequestAudit.EntityType, request.Id, RepairRequestAudit.ConvertedAction, Base.AddDays(-30), actors[0]);
    await sink.FlushAsync();

    return new SeededWorkOrder(workOrder.Id, request.Id, profile.Events, profile.ChildIds + 2, visits.Count, sessions.Count, actions.Count);
}

async Task SeedBackgroundWorkOrdersAsync(int count, Guid tenant, Guid otherTenant, Site siteA, Site siteB, Site siteC, Guid requesterId, Guid otherTenantUser)
{
    const int batch = 500;
    var sequence = 0;
    for (var done = 0; done < count; done += batch)
    {
        await using var db = NewContext();
        var size = Math.Min(batch, count - done);
        var requests = new List<(RepairRequestAggregate Request, Guid Tenant)>();
        for (var i = 0; i < size; i++)
        {
            var (t, site, creator) = ((done + i) % 3) switch { 0 => (tenant, siteA, requesterId), 1 => (tenant, siteB, requesterId), _ => (otherTenant, siteC, otherTenantUser) };
            var request = RepairRequestAggregate.CreateDraft(t, creator);
            db.RepairRequests.Add(request).Property(r => r.SiteId).CurrentValue = site.Id;
            requests.Add((request, t));
        }

        await db.SaveChangesAsync();
        foreach (var (request, t) in requests)
        {
            db.WorkOrders.Add(WorkOrder.Create(t, request.Id, $"PB-{++sequence:D9}", DateTime.UtcNow));
        }

        await db.SaveChangesAsync();
    }
}

async Task SeedNoiseAsync(int rows, Guid[] tenants, RowSink sink)
{
    string[] actions =
    [
        "WORK_ORDER_SCHEDULED", "WORK_ORDER_STARTED", "WORK_ORDER_CLOSED", "SERVICE_VISIT_CHECKED_IN", "SERVICE_VISIT_COMPLETED",
        "SERVICE_VISIT_CANCELLED", "WORK_SESSION_PAUSED", "WORK_SESSION_RESUMED", "WORK_SESSION_CHECKED_OUT", "CORRECTIVE_ACTION_PLAN_SUBMITTED",
        "CORRECTIVE_ACTION_PLAN_APPROVED", "REPAIR_REQUEST_SUBMITTED", "REPAIR_REQUEST_APPROVED", "REPAIR_REQUEST_CONVERTED", "COST_SUMMARY_PREPARED",
    ];
    string[] types = [WorkOrderAudit.WorkOrderEntityType, WorkOrderAudit.ServiceVisitEntityType, WorkOrderAudit.WorkSessionEntityType, WorkOrderAudit.CorrectiveActionEntityType, RepairRequestAudit.EntityType, "CUSTOMER"];
    var rng = new Random(20261011);
    var pool = Enumerable.Range(0, 40_000).Select(_ => Guid.NewGuid()).ToArray();
    for (var i = 0; i < rows; i++)
    {
        sink.Add(tenants[rng.Next(tenants.Length)], types[rng.Next(types.Length)], pool[rng.Next(pool.Length)], actions[rng.Next(actions.Length)],
            Base.AddMilliseconds(rng.NextInt64(0, 120L * 24 * 3600 * 1000)), Guid.NewGuid());
        await sink.FlushAsync(force: false);
    }

    await sink.FlushAsync();
}

// ------------------------------------------------------------------------------------------------ counts

async Task Counts()
{
    await using var db = NewContext();
    Console.WriteLine($"database {database}");
    Console.WriteLine($"audit_history        {await db.AuditHistory.CountAsync()}");
    Console.WriteLine($"repair_requests      {await db.RepairRequests.CountAsync()}");
    Console.WriteLine($"work_orders          {await db.WorkOrders.CountAsync()}");
    Console.WriteLine($"service_visits       {await db.ServiceVisits.CountAsync()}");
    Console.WriteLine($"work_sessions        {await db.WorkSessions.CountAsync()}");
    Console.WriteLine($"corrective_actions   {await db.CorrectiveActions.CountAsync()}");
    Console.WriteLine($"users                {await db.Users.CountAsync()}");

    var fixturePath = Environment.GetEnvironmentVariable("PERF_FIXTURE_PATH");
    if (fixturePath is null || !File.Exists(fixturePath))
    {
        return;
    }

    var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(fixturePath)).RootElement;
    var tenant = fixture.GetProperty("tenantId").GetGuid();
    foreach (var profile in fixture.GetProperty("profiles").EnumerateObject())
    {
        var first = profile.Value[0];
        var expected = first.GetProperty("expectedEvents").GetInt32();
        var actual = await Oracle(db, tenant, first.GetProperty("workOrderId").GetGuid(), first.GetProperty("repairRequestId").GetGuid()).CountAsync();
        Console.WriteLine($"{profile.Name,-8} first WO: expected {expected} events, SQL oracle {actual} {(expected == actual ? "OK" : "MISMATCH")}; {profile.Value.GetArrayLength()} work orders");
    }
}

// Independent of the production store: correlated sub-selects over the real tables, ORDER BY executed by SQL Server.
static IQueryable<RepairRequest.Domain.Auditing.AuditHistory> Oracle(RepairRequestDbContext db, Guid tenant, Guid workOrderId, Guid repairRequestId) =>
    db.AuditHistory.Where(a => a.TenantId == tenant && (
        (a.EntityType == WorkOrderAudit.WorkOrderEntityType && a.EntityId == workOrderId)
        || (a.EntityType == WorkOrderAudit.ServiceVisitEntityType && db.ServiceVisits.Any(v => v.Id == a.EntityId && v.WorkOrderId == workOrderId))
        || (a.EntityType == WorkOrderAudit.WorkSessionEntityType && db.WorkSessions.Any(s => s.Id == a.EntityId && db.ServiceVisits.Any(v => v.Id == s.ServiceVisitId && v.WorkOrderId == workOrderId)))
        || (a.EntityType == WorkOrderAudit.CorrectiveActionEntityType && db.CorrectiveActions.Any(c => c.Id == a.EntityId && c.WorkOrderId == workOrderId))
        || (a.EntityType == RepairRequestAudit.EntityType && a.EntityId == repairRequestId && a.ActionCode == RepairRequestAudit.ConvertedAction)));

// ------------------------------------------------------------------------------------------------ verify

async Task Verify()
{
    var fixturePath = Environment.GetEnvironmentVariable("PERF_FIXTURE_PATH") ?? throw new InvalidOperationException("PERF_FIXTURE_PATH is required.");
    var baseUrl = Environment.GetEnvironmentVariable("PERF_BASE_URL") ?? "http://127.0.0.1:5199";
    var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(fixturePath)).RootElement;
    var tenant = fixture.GetProperty("tenantId").GetGuid();
    using var http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(60) };

    var login = await http.PostAsJsonAsync("/api/v1/auth/login", new { email = fixture.GetProperty("users").GetProperty("supervisor").GetString(), password = fixture.GetProperty("password").GetString() });
    login.EnsureSuccessStatusCode();
    var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    var failures = 0;
    await using var db = NewContext();
    foreach (var profile in fixture.GetProperty("profiles").EnumerateObject())
    {
        // First, a middle and the last work order of each profile.
        var count = profile.Value.GetArrayLength();
        foreach (var position in new[] { 0, count / 2, count - 1 }.Distinct())
        {
            var wo = profile.Value[position];
            var workOrderId = wo.GetProperty("workOrderId").GetGuid();
            var expected = await Oracle(db, tenant, workOrderId, wo.GetProperty("repairRequestId").GetGuid())
                .OrderByDescending(a => a.OccurredAt).ThenByDescending(a => a.Id).Select(a => a.Id).ToListAsync();

            var actual = new List<Guid>();
            string? cursor = null;
            DateTime? previous = null;
            var pages = 0;
            do
            {
                var url = $"/api/v1/entities/WORK_ORDER/{workOrderId}/timeline?pageSize=100" + (cursor is null ? string.Empty : $"&cursor={Uri.EscapeDataString(cursor)}");
                var response = await http.GetAsync(url);
                if (response.StatusCode != System.Net.HttpStatusCode.OK)
                {
                    Console.WriteLine($"FAIL {profile.Name}[{position}] HTTP {(int)response.StatusCode}");
                    failures++;
                    break;
                }

                var body = await response.Content.ReadFromJsonAsync<JsonElement>();
                foreach (var item in body.GetProperty("items").EnumerateArray())
                {
                    actual.Add(item.GetProperty("auditId").GetGuid());
                    var at = item.GetProperty("occurredAt").GetDateTime();
                    if (previous is not null && at > previous)
                    {
                        Console.WriteLine($"FAIL {profile.Name}[{position}] occurredAt increased");
                        failures++;
                    }

                    previous = at;
                }

                cursor = body.GetProperty("nextCursor").ValueKind == JsonValueKind.Null ? null : body.GetProperty("nextCursor").GetString();
                pages++;
            }
            while (cursor is not null);

            var ok = actual.SequenceEqual(expected) && actual.Distinct().Count() == actual.Count;
            failures += ok ? 0 : 1;
            Console.WriteLine($"{(ok ? "OK  " : "FAIL")} {profile.Name}[{position}] pages {pages}, api {actual.Count} rows, oracle {expected.Count} rows, order+exactly-once {ok}");
        }
    }

    Console.WriteLine(failures == 0 ? "verify: all timelines match the SQL Server oracle." : $"verify: {failures} FAILURE(S).");
    Environment.ExitCode = failures == 0 ? 0 : 1;
}

// ------------------------------------------------------------------------------------------------ support

internal sealed record Profile(string Name, int WorkOrders, int WorkOrderEvents, int Visits, int PerVisit, int Sessions, int PerSession, int CorrectiveActions, int PerCorrectiveAction)
{
    public int Events => WorkOrderEvents + Visits * PerVisit + Sessions * PerSession + CorrectiveActions * PerCorrectiveAction + 1;

    public int ChildIds => Visits + Sessions + CorrectiveActions;

    /// <summary>One distinct letter per profile keeps Work Order numbers unique per tenant.</summary>
    public char Code => Name switch { "SMALL" => 'S', "MEDIUM" => 'M', "LARGE" => 'L', "MANYIDS" => 'Y', _ => 'B' };
}

internal sealed record SeededWorkOrder(Guid WorkOrderId, Guid RepairRequestId, int ExpectedEvents, int EntityIds, int Visits, int Sessions, int CorrectiveActions);

internal sealed class RowSink(string connectionString) : IDisposable
{
    private readonly DataTable _table = NewTable();

    private static DataTable NewTable()
    {
        var t = new DataTable();
        t.Columns.Add("audit_history_id", typeof(Guid));
        t.Columns.Add("tenant_id", typeof(Guid));
        t.Columns.Add("entity_type", typeof(string));
        t.Columns.Add("entity_id", typeof(Guid));
        t.Columns.Add("action_code", typeof(string));
        t.Columns.Add("from_state", typeof(string));
        t.Columns.Add("to_state", typeof(string));
        t.Columns.Add("old_value_json", typeof(string));
        t.Columns.Add("new_value_json", typeof(string));
        t.Columns.Add("reason", typeof(string));
        t.Columns.Add("actor_id", typeof(Guid));
        t.Columns.Add("occurred_at", typeof(DateTime));
        t.Columns.Add("correlation_id", typeof(Guid));
        return t;
    }

    public void Add(Guid tenant, string type, Guid entityId, string action, DateTime occurredAt, Guid actor)
    {
        var json = AuditTimelineDetails.CodesWithDetails.Contains(action) ? "{\"serviceVisitId\":\"00000000-0000-0000-0000-000000000001\"}" : null;
        _table.Rows.Add(Guid.NewGuid(), tenant, type, entityId, action, "FROM", "TO", null, json, null, actor, occurredAt, Guid.NewGuid());
    }

    public async Task FlushAsync(bool force = true)
    {
        if (_table.Rows.Count == 0 || (!force && _table.Rows.Count < 100_000))
        {
            return;
        }

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        using var bulk = new SqlBulkCopy(connection) { DestinationTableName = "audit_history", BulkCopyTimeout = 0, BatchSize = 50_000 };
        foreach (DataColumn column in _table.Columns)
        {
            bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
        }

        await bulk.WriteToServerAsync(_table);
        _table.Clear();
    }

    public void Dispose() => _table.Dispose();
}
