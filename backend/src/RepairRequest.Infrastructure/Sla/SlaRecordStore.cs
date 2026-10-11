using Microsoft.EntityFrameworkCore;
using RepairRequest.Application.Security;
using RepairRequest.Application.Sla;
using RepairRequest.Domain.Auditing;
using RepairRequest.Domain.Sla;
using RepairRequest.Infrastructure.Persistence;

namespace RepairRequest.Infrastructure.Sla;

/// <summary>
/// EF Core implementation of the SLA record port (`docs/16` D5-D7).
/// <list type="bullet">
/// <item>Start: tracked inserts of the record and its audit row, persisted by the Submit store's single save.</item>
/// <item>Stop: one server-side <c>UPDATE ... OUTPUT</c> guarded by <c>stopped_at IS NULL</c> on the unique request index, inside
/// the lifecycle command's transaction. The first stop wins atomically; a second stop (same or different reason) matches no row
/// and never overwrites the first reason or time.</item>
/// <item>Read: one scoped, no-tracking query: the caller's <see cref="IDataScope.RepairRequests"/> left-joined to the record by
/// primary-key/unique-index seeks. It never writes.</item>
/// </list>
/// </summary>
internal sealed class SlaRecordStore : ISlaRecordStore
{
    private readonly RepairRequestDbContext _db;
    private readonly IDataScope _scope;

    public SlaRecordStore(RepairRequestDbContext db, IDataScope scope)
    {
        _db = db;
        _scope = scope;
    }

    public void StageStart(SlaRecord record, AuditHistory audit)
    {
        _db.SlaRecords.Add(record);
        _db.AuditHistory.Add(audit);
    }

    public async Task<Guid?> StopAsync(
        Guid tenantId, Guid repairRequestId, SlaStopReason reason, DateTime stoppedAt, CancellationToken cancellationToken)
    {
        var code = SlaStopReasonCodes.ToCode(reason);

        // A tracked sla_record is never modified through EF (the type has no mutators); the conditional update is the only stop.
        var stopped = await _db.Database.SqlQuery<Guid>($"""
            UPDATE [sla_record]
            SET [stopped_at] = {stoppedAt}, [stop_reason] = {code}
            OUTPUT [inserted].[sla_record_id] AS [Value]
            WHERE [tenant_id] = {tenantId} AND [repair_request_id] = {repairRequestId} AND [stopped_at] IS NULL;
            """).ToListAsync(cancellationToken);

        return stopped.Count == 1 ? stopped[0] : null;
    }

    public Task<SlaReadModel?> FindForReadAsync(CurrentUser user, Guid repairRequestId, CancellationToken cancellationToken) =>
        (from request in _scope.RepairRequests(user)
         where request.Id == repairRequestId && request.SubmittedAt != null
         join record in _db.SlaRecords on new { request.TenantId, RepairRequestId = request.Id } equals new { record.TenantId, record.RepairRequestId } into records
         from record in records.DefaultIfEmpty()
         select new SlaReadModel(
             request.Id,
             record.SlaPolicyVersion,
             (DateTime?)record.StartedAt,
             (DateTime?)record.OriginalDueAt,
             (DateTime?)record.RiskAt,
             record.StoppedAt,
             record.StopReason))
        .AsNoTracking()
        .SingleOrDefaultAsync(cancellationToken);
}
