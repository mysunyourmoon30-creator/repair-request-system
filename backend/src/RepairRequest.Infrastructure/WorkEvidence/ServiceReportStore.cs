using Microsoft.EntityFrameworkCore;
using RepairRequest.Application.Security;
using RepairRequest.Application.WorkEvidence;
using RepairRequest.Domain.Files;
using RepairRequest.Infrastructure.Persistence;

namespace RepairRequest.Infrastructure.WorkEvidence;

/// <summary>
/// EF Core implementation of the Service Report read model (SVR-API-001; `docs/16` D1, OD4/OD5). A fixed number of bounded, no-tracking
/// queries — header, Visits, Sessions, Pauses, Work Summaries, bound evidence, Acceptance rounds and, only for the Team Lead / Supervisor
/// audience, the Cost Summary — each filtered by the Work Order and joined through foreign keys, never one query per row (no N+1). For
/// every other audience the cost table is not queried at all.
/// </summary>
internal sealed class ServiceReportStore : IServiceReportStore
{
    private readonly RepairRequestDbContext _db;
    private readonly IDataScope _scope;

    public ServiceReportStore(RepairRequestDbContext db, IDataScope scope)
    {
        _db = db;
        _scope = scope;
    }

    public async Task<ServiceReportResult?> GetAsync(CurrentUser user, Guid workOrderId, CancellationToken cancellationToken)
    {
        var audience = await ReportAudienceResolver.ResolveAsync(_db, _scope, user, workOrderId, cancellationToken);
        if (audience == ReportAudience.None)
        {
            return null;
        }

        var tenantId = user.TenantId;

        var header = await (
            from workOrder in _db.WorkOrders
            join request in _db.RepairRequests on workOrder.RepairRequestId equals request.Id
            where workOrder.TenantId == tenantId && workOrder.Id == workOrderId
            select new ServiceReportHeaderDto(
                workOrder.Id,
                workOrder.WorkOrderNo,
                workOrder.Status,
                workOrder.CreatedAt,
                workOrder.ClosedAt,
                workOrder.RepairRequestId,
                request.RequestNo,
                _db.Customers
                    .Where(customer => customer.Id ==
                        _db.Sites.Where(site => site.Id == request.SiteId).Select(site => site.CustomerId).FirstOrDefault())
                    .Select(customer => customer.CustomerCode)
                    .FirstOrDefault(),
                _db.Sites.Where(site => site.Id == request.SiteId).Select(site => site.SiteCode).FirstOrDefault(),
                _db.Equipment.Where(item => item.Id == request.EquipmentId).Select(item => item.EquipmentCode).FirstOrDefault()))
            .AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken);
        if (header is null)
        {
            return null;
        }

        var visits = await _db.ServiceVisits.AsNoTracking()
            .Where(visit => visit.TenantId == tenantId && visit.WorkOrderId == workOrderId)
            .OrderBy(visit => visit.ScheduledStartAt).ThenBy(visit => visit.Id)
            .Select(visit => new
            {
                visit.Id,
                visit.VisitType,
                visit.Status,
                visit.AssignedTechnicianId,
                visit.ScheduledStartAt,
                visit.ScheduledEndAt,
                visit.CompletedAt
            })
            .ToListAsync(cancellationToken);

        var sessions = await (
            from session in _db.WorkSessions
            join visit in _db.ServiceVisits on session.ServiceVisitId equals visit.Id
            where visit.TenantId == tenantId && visit.WorkOrderId == workOrderId
            orderby session.CheckInAt, session.Id
            select new { session.Id, session.ServiceVisitId, session.Status, session.CheckInAt, session.CheckOutAt })
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var pauses = await (
            from pause in _db.WorkSessionPauses
            join session in _db.WorkSessions on pause.WorkSessionId equals session.Id
            join visit in _db.ServiceVisits on session.ServiceVisitId equals visit.Id
            where visit.TenantId == tenantId && visit.WorkOrderId == workOrderId
            orderby pause.PausedAt, pause.Id
            select new { pause.WorkSessionId, pause.PausedAt, pause.ResumedAt })
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var summaries = await _db.WorkSummaries.AsNoTracking()
            .Where(summary => summary.TenantId == tenantId && summary.WorkOrderId == workOrderId)
            .OrderBy(summary => summary.Id)
            .Select(summary => new { summary.Id, summary.ServiceVisitId, summary.RevisionNo, summary.SummaryText, summary.RepairOutcomeCode })
            .ToListAsync(cancellationToken);

        // The frozen set of every submitted round; only files that are CLEAN right now are reported.
        var evidence = await (
            from binding in _db.WorkSummaryEvidence
            join summary in _db.WorkSummaries on binding.WorkSummaryId equals summary.Id
            join file in _db.FileAssets on binding.FileAssetId equals file.Id
            where summary.TenantId == tenantId
                  && summary.WorkOrderId == workOrderId
                  && file.TenantId == binding.TenantId
                  && file.MalwareScanStatus == MalwareScanStatus.Clean
            orderby binding.SlotNo
            select new
            {
                binding.WorkSummaryId,
                Item = new ServiceReportEvidenceDto(
                    binding.Id, binding.EvidenceType, file.FileName, file.MimeType, file.SizeBytes, binding.SlotNo, binding.BoundAt)
            })
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var acceptance = await _db.CustomerAcceptances.AsNoTracking()
            .Where(item => item.TenantId == tenantId && item.WorkOrderId == workOrderId)
            .OrderBy(item => item.AcceptanceRoundNo).ThenBy(item => item.Id)
            .Select(item => new ServiceReportAcceptanceDto(item.AcceptanceRoundNo, item.Decision, item.DecisionReason, item.DecidedAt, item.AcceptanceContactId))
            .ToListAsync(cancellationToken);

        // Cost is read only for the Team Lead / Supervisor audience: for every other audience this query is never issued.
        ServiceReportCostDto? cost = null;
        if (audience == ReportAudience.TeamLeadSupervisor)
        {
            cost = await _db.CostSummaries.AsNoTracking()
                .Where(summary => summary.TenantId == tenantId && summary.WorkOrderId == workOrderId)
                .Select(summary => new ServiceReportCostDto(
                    summary.TotalAmount, summary.CurrencyCode, summary.Note, summary.PreparedBy, summary.PreparedAt, summary.ReviewedBy, summary.ReviewedAt))
                .SingleOrDefaultAsync(cancellationToken);
        }

        var pausesBySession = pauses.ToLookup(pause => pause.WorkSessionId);
        var sessionsByVisit = sessions.ToLookup(session => session.ServiceVisitId);
        var evidenceBySummary = evidence.ToLookup(row => row.WorkSummaryId, row => row.Item);

        var report = new ServiceReportDto(
            header,
            visits.Select(visit => new ServiceReportVisitDto(
                visit.Id,
                visit.VisitType,
                visit.Status,
                visit.AssignedTechnicianId,
                visit.ScheduledStartAt,
                visit.ScheduledEndAt,
                visit.CompletedAt,
                sessionsByVisit[visit.Id].Select(session => new ServiceReportSessionDto(
                    session.Id,
                    session.Status,
                    session.CheckInAt,
                    session.CheckOutAt,
                    pausesBySession[session.Id].Select(pause => new ServiceReportPauseDto(pause.PausedAt, pause.ResumedAt)).ToList())).ToList())).ToList(),
            summaries.Select(summary => new ServiceReportSummaryDto(
                summary.Id,
                summary.ServiceVisitId,
                summary.RevisionNo,
                summary.SummaryText,
                summary.RepairOutcomeCode,
                evidenceBySummary[summary.Id].ToList())).ToList(),
            acceptance);

        return new ServiceReportResult(audience, report, cost);
    }
}
