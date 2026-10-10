using System.Data;
using System.Security.Cryptography;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using RepairRequest.Application.Audit;
using RepairRequest.Application.RepairRequests;
using RepairRequest.Application.WorkOrders;
using RepairRequest.Infrastructure.Persistence;
using RepairRequest.IntegrationTests.Authentication;

namespace RepairRequest.IntegrationTests.Audit.Support;

/// <summary>
/// Describes one seeded Work Order timeline. Counts are audit rows per entity id; <see cref="Groups"/> (tie-group
/// sizes, newest first) or <see cref="SingleTimestamp"/> control occurred_at, otherwise timestamps come from a pool
/// that produces frequent ties.
/// </summary>
public sealed record ScenarioSpec(
    string Name,
    int WorkOrderEvents,
    int Visits, int PerVisit,
    int Sessions, int PerSession,
    int CorrectiveActions, int PerCorrectiveAction,
    bool Convert,
    bool SingleTimestamp = false,
    int[]? Groups = null)
{
    public int SourceEvents => WorkOrderEvents + Visits * PerVisit + Sessions * PerSession + CorrectiveActions * PerCorrectiveAction;
}

public sealed record Scenario(
    ScenarioSpec Spec,
    AuditTimelineSources Sources,
    IReadOnlySet<Guid> ExpectedAuditIds,
    IReadOnlySet<Guid> DecoyAuditIds)
{
    public int TotalExpected => ExpectedAuditIds.Count;

    /// <summary>Distinct entity ids the timeline query has to cover (Work Order + Repair Request + children).</summary>
    public int EntityIdCount => 2 + Sources.VisitIds.Count + Sources.SessionIds.Count + Sources.CorrectiveActionIds.Count;
}

/// <summary>
/// A disposable LocalDB database created from the real EF Core migrations (so the real <c>audit_history</c> table and
/// the real <c>IX_audit_history_timeline</c> are under test), loaded with random "noise" audit rows plus named Work
/// Order scenarios and their decoys. The production <see cref="IAuditTimelineStore"/> is resolved from the real
/// Infrastructure composition against this database. SQL Server/LocalDB only — no other provider.
/// Noise defaults to 100,000 rows; <c>RUN_TIMELINE_EVIDENCE=1</c> raises it to 500,000 (override with
/// <c>TIMELINE_NOISE_ROWS</c>) for the opt-in evidence run.
/// </summary>
public sealed class AuditTimelineDatabaseFixture : IAsyncLifetime
{
    public const string ConnectionString =
        "Server=(localdb)\\MSSQLLocalDB;Database=RepairRequestDb_AuditTimeline;Trusted_Connection=True;TrustServerCertificate=True";

    public static readonly DateTime Base = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    public static bool EvidenceRequested => Environment.GetEnvironmentVariable("RUN_TIMELINE_EVIDENCE") == "1";

    private ServiceProvider _services = null!;

    public Guid MainTenant { get; } = Guid.NewGuid();

    public Guid OtherTenant { get; } = Guid.NewGuid();

    public int NoiseRows { get; private set; }

    public int CompatibilityLevel { get; private set; }

    /// <summary>Commands sent by the production store's DbContext (the interceptor is added to the real DI registration).</summary>
    public CapturingInterceptor Capture { get; } = new();

    public Dictionary<string, Scenario> Scenarios { get; } = [];

    public static readonly ScenarioSpec[] Standard =
    [
        new("WO20", 6, 2, 3, 2, 2, 1, 3, true),
        new("WO200", 32, 8, 10, 8, 6, 3, 13, true),
        new("WO2000", 309, 40, 20, 40, 12, 10, 41, true),
        new("MANYIDS", 20, 100, 3, 100, 3, 30, 3, true),
        // More than 2,100 child entity ids (1,300 visits + 1,000 sessions + 100 corrective actions): the SQL Server limit for
        // parameters per command, which the one-parameter-per-id shape could never serve.
        new("BIGIDS", 20, 1300, 1, 1000, 1, 100, 1, true),
        new("KS1", 30, 3, 15, 3, 15, 2, 15, false, SingleTimestamp: true),
        new("MIX", 12, 6, 10, 6, 10, 3, 10, false, Groups: [1, 25, 1, 60, 3, 40, 1, 30, 1]),
    ];

    public RepairRequestDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<RepairRequestDbContext>().UseSqlServer(ConnectionString).Options;
        return new RepairRequestDbContext(options);
    }

    public AsyncServiceScope CreateScope() => _services.CreateAsyncScope();

    public async Task InitializeAsync()
    {
        await using (var context = CreateContext())
        {
            await context.Database.EnsureDeletedAsync();
            await context.Database.MigrateAsync();
        }

        var settings = AuthenticationTestHost.CreateSettings(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        settings["ConnectionStrings:DefaultConnection"] = ConnectionString;
        // EF Core 9+ composes repeated AddDbContext calls, so this adds the capture interceptor to the real registration.
        _services = AuthenticationTestHost.BuildProvider(
            settings,
            configureServices: services => services.AddDbContext<RepairRequestDbContext>(options => options.AddInterceptors(Capture)));

        var configured = int.TryParse(Environment.GetEnvironmentVariable("TIMELINE_NOISE_ROWS"), out var n) ? n : (EvidenceRequested ? 500_000 : 100_000);
        NoiseRows = configured;
        await SeedNoiseAsync(NoiseRows);
        foreach (var spec in Standard)
        {
            Scenarios[spec.Name] = await SeedAsync(spec);
        }

        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using (var stats = connection.CreateCommand())
        {
            stats.CommandText = "UPDATE STATISTICS audit_history WITH FULLSCAN";
            await stats.ExecuteNonQueryAsync();
        }

        await using var level = connection.CreateCommand();
        level.CommandText = "SELECT compatibility_level FROM sys.databases WHERE name = DB_NAME()";
        CompatibilityLevel = Convert.ToInt32(await level.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await using var context = CreateContext();
        await context.Database.EnsureDeletedAsync();
    }

    // ------------------------------------------------------------------ seeding

    private static readonly string[] NoiseActions =
    [
        "WORK_ORDER_SCHEDULED", "WORK_ORDER_STARTED", "WORK_ORDER_CLOSED", "SERVICE_VISIT_CHECKED_IN", "SERVICE_VISIT_COMPLETED",
        "SERVICE_VISIT_CANCELLED", "WORK_SESSION_PAUSED", "WORK_SESSION_RESUMED", "WORK_SESSION_CHECKED_OUT", "CORRECTIVE_ACTION_PLAN_SUBMITTED",
        "CORRECTIVE_ACTION_PLAN_APPROVED", "REPAIR_REQUEST_SUBMITTED", "REPAIR_REQUEST_APPROVED", "REPAIR_REQUEST_CONVERTED", "COST_SUMMARY_PREPARED",
    ];

    private const string WorkOrder = WorkOrderAudit.WorkOrderEntityType;
    private const string Visit = WorkOrderAudit.ServiceVisitEntityType;
    private const string Session = WorkOrderAudit.WorkSessionEntityType;
    private const string Corrective = WorkOrderAudit.CorrectiveActionEntityType;
    private const string RepairRequest = RepairRequestAudit.EntityType;
    private const string Converted = RepairRequestAudit.ConvertedAction;

    private static readonly string[] NoiseTypes = [WorkOrder, Visit, Session, Corrective, RepairRequest, "CUSTOMER"];

    /// <summary>Buffers audit rows and bulk-inserts them (no FKs on audit_history, so subjects need not exist).</summary>
    public sealed class RowSink : IDisposable
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

        public Guid Add(Guid tenant, string type, Guid entityId, string action, DateTime occurredAt, Guid? id = null, string? newValueJson = null, Guid? actor = null)
        {
            var auditId = id ?? Guid.NewGuid();
            var json = newValueJson ?? (AuditTimelineDetails.CodesWithDetails.Contains(action) ? "{\"serviceVisitId\":\"00000000-0000-0000-0000-000000000001\"}" : null);
            _table.Rows.Add(auditId, tenant, type, entityId, action, "FROM", "TO", null, json, null, actor ?? Guid.NewGuid(), occurredAt, Guid.NewGuid());
            return auditId;
        }

        public async Task FlushAsync(bool force = true)
        {
            if (_table.Rows.Count == 0 || (!force && _table.Rows.Count < 100_000))
            {
                return;
            }

            await using var connection = new SqlConnection(ConnectionString);
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

    private async Task SeedNoiseAsync(int rows)
    {
        var rng = new Random(20261007);
        var pool = Enumerable.Range(0, 40_000).Select(_ => Guid.NewGuid()).ToArray();
        var tenants = new[] { MainTenant, OtherTenant, Guid.NewGuid() };
        using var sink = new RowSink();
        for (var i = 0; i < rows; i++)
        {
            sink.Add(
                tenants[rng.Next(tenants.Length)],
                NoiseTypes[rng.Next(NoiseTypes.Length)],
                pool[rng.Next(pool.Length)],
                NoiseActions[rng.Next(NoiseActions.Length)],
                Base.AddMilliseconds(rng.NextInt64(0, 120L * 24 * 3600 * 1000)));
            await sink.FlushAsync(force: false);
        }

        await sink.FlushAsync();
    }

    public async Task<Scenario> SeedAsync(ScenarioSpec spec)
    {
        var rng = new Random(spec.Name.GetHashCode(StringComparison.Ordinal) ^ 17);
        var tenant = MainTenant;
        var workOrder = Guid.NewGuid();
        var repairRequest = Guid.NewGuid();
        List<Guid> Ids(int n) => Enumerable.Range(0, n).Select(_ => Guid.NewGuid()).ToList();
        var visits = Ids(spec.Visits);
        var sessions = Ids(spec.Sessions);
        var actions = Ids(spec.CorrectiveActions);

        var stamps = new List<DateTime>();
        if (spec.SingleTimestamp)
        {
            stamps.AddRange(Enumerable.Repeat(Base.AddDays(10).AddMilliseconds(123), spec.SourceEvents));
        }
        else if (spec.Groups is not null)
        {
            for (var g = 0; g < spec.Groups.Length; g++)
            {
                stamps.AddRange(Enumerable.Repeat(Base.AddDays(20 - g).AddMilliseconds(500 + g), spec.Groups[g]));
            }

            if (stamps.Count != spec.SourceEvents)
            {
                throw new InvalidOperationException($"{spec.Name}: groups sum {stamps.Count} != events {spec.SourceEvents}.");
            }
        }
        else
        {
            var poolSize = Math.Max(3, spec.SourceEvents / 4);
            for (var i = 0; i < spec.SourceEvents; i++)
            {
                stamps.Add(Base.AddDays(5).AddSeconds(rng.Next(poolSize) * 13).AddMilliseconds(250));
            }
        }

        for (var i = stamps.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (stamps[i], stamps[j]) = (stamps[j], stamps[i]);
        }

        using var sink = new RowSink();
        var expected = new HashSet<Guid>();
        var decoys = new HashSet<Guid>();
        var next = 0;

        static string Action(string type, int i) => type switch
        {
            WorkOrder => new[] { "WORK_ORDER_SCHEDULED", "WORK_ORDER_STARTED", "WORK_SUMMARY_SUBMITTED", "WORK_ORDER_CLOSED" }[i % 4],
            Visit => new[] { "SERVICE_VISIT_CHECKED_IN", "SERVICE_VISIT_COMPLETED", "SERVICE_VISIT_CANCELLED" }[i % 3],
            Session => new[] { "WORK_SESSION_PAUSED", "WORK_SESSION_RESUMED", "WORK_SESSION_CHECKED_OUT" }[i % 3],
            _ => new[] { "CORRECTIVE_ACTION_PLAN_SUBMITTED", "CORRECTIVE_ACTION_PLAN_APPROVED", "CORRECTIVE_ACTION_REWORK_SCHEDULED" }[i % 3],
        };

        void Source(string type, Guid entityId, int count)
        {
            for (var i = 0; i < count; i++)
            {
                expected.Add(sink.Add(tenant, type, entityId, Action(type, i), stamps[next++]));
            }
        }

        Source(WorkOrder, workOrder, spec.WorkOrderEvents);
        foreach (var id in visits) { Source(Visit, id, spec.PerVisit); }
        foreach (var id in sessions) { Source(Session, id, spec.PerSession); }
        foreach (var id in actions) { Source(Corrective, id, spec.PerCorrectiveAction); }
        if (spec.Convert)
        {
            expected.Add(sink.Add(tenant, RepairRequest, repairRequest, Converted, Base.AddDays(-30)));
        }

        // Decoys that must never appear: another tenant with the same ids/types, the same ids under the wrong entity type,
        // the originating request's other events, an unrelated converted request, another Work Order of the same tenant.
        var sample = new List<(string Type, Guid Id)> { (WorkOrder, workOrder) };
        sample.AddRange(visits.Take(10).Select(v => (Visit, v)));
        sample.AddRange(sessions.Take(10).Select(v => (Session, v)));
        sample.AddRange(actions.Take(5).Select(v => (Corrective, v)));
        foreach (var (type, id) in sample)
        {
            for (var i = 0; i < 2; i++)
            {
                decoys.Add(sink.Add(OtherTenant, type, id, Action(type, i), Base.AddDays(10)));
            }
        }

        foreach (var v in visits.Take(3)) { decoys.Add(sink.Add(tenant, Session, v, "WORK_SESSION_PAUSED", Base.AddDays(10))); }
        decoys.Add(sink.Add(tenant, Visit, workOrder, "SERVICE_VISIT_CHECKED_IN", Base.AddDays(10)));
        decoys.Add(sink.Add(tenant, "CUSTOMER", workOrder, "WORK_ORDER_SCHEDULED", Base.AddDays(10)));
        decoys.Add(sink.Add(tenant, RepairRequest, repairRequest, "REPAIR_REQUEST_SUBMITTED", Base.AddDays(-31)));
        decoys.Add(sink.Add(tenant, RepairRequest, repairRequest, "REPAIR_REQUEST_APPROVED", Base.AddDays(-30)));
        decoys.Add(sink.Add(tenant, RepairRequest, Guid.NewGuid(), Converted, Base.AddDays(-30)));
        var otherWorkOrder = Guid.NewGuid();
        for (var i = 0; i < 5; i++)
        {
            decoys.Add(sink.Add(tenant, WorkOrder, otherWorkOrder, Action(WorkOrder, i), Base.AddDays(10)));
            decoys.Add(sink.Add(tenant, Visit, Guid.NewGuid(), Action(Visit, i), Base.AddDays(10)));
        }

        await sink.FlushAsync();
        return new Scenario(spec, new AuditTimelineSources(tenant, workOrder, repairRequest, visits, sessions, actions), expected, decoys);
    }

    /// <summary>Adds one audit row (used by the append / late-commit tests).</summary>
    public async Task<Guid> InsertAsync(Guid tenant, string type, Guid entityId, string action, DateTime occurredAt, Guid? id = null)
    {
        using var sink = new RowSink();
        var auditId = sink.Add(tenant, type, entityId, action, occurredAt, id);
        await sink.FlushAsync();
        return auditId;
    }
}

[CollectionDefinition(Name)]
public sealed class AuditTimelineCollection : ICollectionFixture<AuditTimelineDatabaseFixture>
{
    public const string Name = "Audit timeline database";
}

/// <summary>Skipped unless <c>RUN_TIMELINE_EVIDENCE=1</c>: the heavy (500,000-row) plan/IO/timing evidence runs are opt-in, never default CI.</summary>
public sealed class EvidenceFactAttribute : FactAttribute
{
    public EvidenceFactAttribute()
    {
        if (!AuditTimelineDatabaseFixture.EvidenceRequested)
        {
            Skip = "Opt-in evidence run: set RUN_TIMELINE_EVIDENCE=1 (seeds 500,000 audit rows and writes plan/IO evidence).";
        }
    }
}

/// <summary>Walks the production store page by page exactly as the service does: take = pageSize + 1, the cursor is the last returned row.</summary>
public static class TimelineWalker
{
    public sealed record Page(IReadOnlyList<AuditTimelineRow> Items, bool HasMore, TimelinePosition? Next);

    public static async Task<List<Page>> WalkAsync(IAuditTimelineStore store, AuditTimelineSources sources, int pageSize, TimelinePosition? start = null)
    {
        var pages = new List<Page>();
        var cursor = start;
        for (var guard = 0; guard < 100_000; guard++)
        {
            var rows = await store.ReadPageAsync(sources, cursor, pageSize + 1, CancellationToken.None);
            var hasMore = rows.Count > pageSize;
            var items = rows.Take(pageSize).ToList();
            TimelinePosition? next = hasMore ? new TimelinePosition(items[^1].OccurredAt, items[^1].AuditId) : null;
            pages.Add(new Page(items, hasMore, next));
            if (!hasMore)
            {
                return pages;
            }

            cursor = next;
        }

        throw new InvalidOperationException("Timeline walk did not terminate.");
    }
}
