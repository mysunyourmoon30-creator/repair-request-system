using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RepairRequest.Application.Security;
using RepairRequest.Application.Sla;
using RepairRequest.Domain.Security;
using RepairRequest.Domain.Sla;
using RepairRequest.Infrastructure.Persistence;
using RepairRequest.IntegrationTests.Audit.Support;
using RepairRequest.IntegrationTests.Authentication;
using RepairRequest.IntegrationTests.Authorization;
using RepairRequest.IntegrationTests.Persistence;

namespace RepairRequest.IntegrationTests.Sla;

/// <summary>
/// Evidence for `docs/16` D5-D7 on the SLA store: the stop is one server-side conditional update (never a load-modify-save of the
/// record) that seeks the request's row, and the SLA-API-001 read is one no-tracking query that seeks it too.
/// </summary>
[Collection(PersistenceDatabaseCollection.Name)]
public sealed class SlaStopEvidenceTests : IAsyncLifetime
{
    private static readonly CancellationToken None = CancellationToken.None;

    private readonly CapturingInterceptor _capture = new();
    private readonly AuthenticationTestHost _host;

    public SlaStopEvidenceTests(PersistenceDatabaseFixture database)
    {
        _ = database;
        _host = new AuthenticationTestHost(services =>
            services.AddDbContext<RepairRequestDbContext>(options => options.AddInterceptors(_capture)));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private async Task<(CurrentUser Requester, Guid RequestId)> SeedSubmittedRequestWithRecordAsync()
    {
        await using var scope = _host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RepairRequestDbContext>();
        var world = await ScopeWorld.CreateAsync(db);
        var user = await _host.CreateUserInTenantAsync(world.TenantId, RoleCodes.Requester);
        await ScopeWorld.AssignSitesAsync(db, world.TenantId, user.Id, world.SiteA1);
        var requestId = await ScopeWorld.AddRequestAsync(db, world.TenantId, user.Id, world.SiteA1);

        // The read requires a previously submitted request; mark it submitted the way Submit would.
        var submittedAt = DateTime.UtcNow.AddHours(-1);
        submittedAt = submittedAt.AddTicks(-(submittedAt.Ticks % TimeSpan.TicksPerMillisecond));
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [repair_request] SET [submitted_at] = {submittedAt} WHERE [repair_request_id] = {requestId}");
        db.SlaRecords.Add(SlaRecord.Start(world.TenantId, requestId, submittedAt));
        await db.SaveChangesAsync();
        return (new CurrentUser(user.Id, world.TenantId, [RoleCodes.Requester]), requestId);
    }

    [Fact]
    public async Task Stop_IsOneServerSideConditionalUpdate_ThatSeeksTheRequestsRow_AndWritesBothStopFieldsTogether()
    {
        var (requester, requestId) = await SeedSubmittedRequestWithRecordAsync();
        var stoppedAt = DateTime.UtcNow;
        stoppedAt = stoppedAt.AddTicks(-(stoppedAt.Ticks % TimeSpan.TicksPerMillisecond));

        await using var scope = _host.CreateScope();
        var sla = scope.ServiceProvider.GetRequiredService<ISlaRecordStore>();
        _capture.Commands.Clear();

        var stopped = await sla.StopAsync(requester.TenantId, requestId, SlaStopReason.RequestRejected, stoppedAt, None);

        Assert.NotNull(stopped);

        // Exactly one command: no SELECT of the record, no entity load, no separate save.
        var command = Assert.Single(_capture.Commands);
        Assert.StartsWith("UPDATE [sla_record]", command.Sql.TrimStart(), StringComparison.Ordinal);
        Assert.Contains("[stopped_at] IS NULL", command.Sql, StringComparison.Ordinal);
        Assert.Contains("[tenant_id] =", command.Sql, StringComparison.Ordinal);
        Assert.Contains("[repair_request_id] =", command.Sql, StringComparison.Ordinal);
        Assert.Contains("SET [stopped_at] =", command.Sql, StringComparison.Ordinal);
        Assert.Contains("[stop_reason] =", command.Sql, StringComparison.Ordinal);
        Assert.Contains("OUTPUT", command.Sql, StringComparison.Ordinal);

        // Re-run the very SQL EF sent against a fresh active record: the plan seeks sla_record, it never scans it.
        var (_, freshRequestId) = await SeedSubmittedRequestWithRecordAsync();
        var parameters = command.Parameters.Select(parameter => (SqlParameter)((ICloneable)parameter).Clone()).ToList();
        var requestParameter = parameters.Single(parameter => parameter.Value is Guid value && value == requestId);
        requestParameter.Value = freshRequestId;
        var tenantParameter = parameters.Single(parameter => parameter.Value is Guid value && value == requester.TenantId);
        await using (var scope2 = _host.CreateScope())
        {
            var fresh = await scope2.ServiceProvider.GetRequiredService<RepairRequestDbContext>().SlaRecords
                .AsNoTracking().SingleAsync(record => record.RepairRequestId == freshRequestId);
            tenantParameter.Value = fresh.TenantId;
        }

        var measurement = await SqlPlanEvidence.MeasureAsync(PersistenceDatabaseFixture.ConnectionString, command.Sql, parameters);

        Assert.Equal(1, measurement.RowsReturned);
        Assert.Contains(measurement.Operators, op => op.Index is not null && op.Index.Contains("sla_record", StringComparison.OrdinalIgnoreCase)
                                                    && op.PhysicalOp.Contains("Seek", StringComparison.Ordinal));
        Assert.DoesNotContain(measurement.Operators, op => op.Index is not null && op.Index.Contains("sla_record", StringComparison.OrdinalIgnoreCase)
                                                          && op.PhysicalOp.Contains("Scan", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Read_IsOneNoTrackingQuery_ThatSeeksTheRecord_AndNeverWrites()
    {
        var (requester, requestId) = await SeedSubmittedRequestWithRecordAsync();

        await using var scope = _host.CreateScope();
        var sla = scope.ServiceProvider.GetRequiredService<ISlaRecordStore>();
        var db = scope.ServiceProvider.GetRequiredService<RepairRequestDbContext>();
        _capture.Commands.Clear();

        var model = await sla.FindForReadAsync(requester, requestId, None);

        Assert.NotNull(model);
        Assert.NotNull(model.StartedAt);
        Assert.Empty(db.ChangeTracker.Entries());
        var command = Assert.Single(_capture.Commands);
        Assert.StartsWith("SELECT", command.Sql.TrimStart(), StringComparison.Ordinal);
        Assert.DoesNotContain("INSERT", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE", command.Sql, StringComparison.OrdinalIgnoreCase);

        var measurement = await SqlPlanEvidence.MeasureAsync(PersistenceDatabaseFixture.ConnectionString, command.Sql, command.Parameters);

        Assert.Equal(1, measurement.RowsReturned);
        Assert.Contains(measurement.Operators, op => op.Index is not null && op.Index.Contains("sla_record", StringComparison.OrdinalIgnoreCase)
                                                    && op.PhysicalOp.Contains("Seek", StringComparison.Ordinal));
        Assert.DoesNotContain(measurement.Operators, op => op.Index is not null && op.Index.Contains("sla_record", StringComparison.OrdinalIgnoreCase)
                                                          && op.PhysicalOp.Contains("Scan", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Read_OfALegacyOrNeverSubmittedRequest_IsSideEffectFree()
    {
        var (requester, requestId) = await SeedSubmittedRequestWithRecordAsync();
        await using (var cleanup = _host.CreateScope())
        {
            await cleanup.ServiceProvider.GetRequiredService<RepairRequestDbContext>().Database
                .ExecuteSqlInterpolatedAsync($"DELETE FROM [sla_record] WHERE [repair_request_id] = {requestId}");
        }

        await using var scope = _host.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<SlaRecordReadService>();
        var db = scope.ServiceProvider.GetRequiredService<RepairRequestDbContext>();
        _capture.Commands.Clear();

        var legacy = await service.GetAsync(requester, requestId, None);

        Assert.NotNull(legacy);
        Assert.Equal(SlaTrackingStatus.NotTrackedLegacy, legacy.TrackingStatus);
        Assert.Single(_capture.Commands);
        Assert.Empty(db.ChangeTracker.Entries());
        Assert.Equal(0, await db.SlaRecords.CountAsync(record => record.RepairRequestId == requestId));
    }
}
