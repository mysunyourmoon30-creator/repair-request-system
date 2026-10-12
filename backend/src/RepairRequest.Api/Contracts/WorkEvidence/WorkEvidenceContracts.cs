using RepairRequest.Application.Attachments;
using RepairRequest.Application.WorkEvidence;
using RepairRequest.Application.WorkOrders;
using RepairRequest.Domain.WorkEvidence;
using RepairRequest.Domain.WorkOrders;

namespace RepairRequest.Api.Contracts.WorkEvidence;

/// <summary>
/// WEV-API-001/002 evidence metadata (`docs/13` §4.28). There is deliberately no storage reference, content hash, uploader identity or
/// tenant in any response, and the file name is the sanitized display name only.
/// </summary>
public sealed record WorkEvidenceItemResponse(
    Guid EvidenceId,
    Guid ServiceVisitId,
    string EvidenceType,
    string FileName,
    string MimeType,
    long SizeBytes,
    string ScanStatus,
    bool Bound,
    Guid? WorkSummaryId,
    int? SlotNo,
    DateTime UploadedAt);

public sealed record WorkEvidenceListResponse(IReadOnlyList<WorkEvidenceItemResponse> Items);

public static class WorkEvidenceResponses
{
    public static WorkEvidenceItemResponse ToResponse(WorkEvidenceItemDto dto) =>
        new(
            dto.EvidenceId,
            dto.ServiceVisitId,
            WorkEvidenceTypeCodes.ToCode(dto.EvidenceType),
            dto.FileName,
            dto.MimeType,
            dto.SizeBytes,
            MalwareScanStatusCodes.ToCode(dto.ScanStatus),
            dto.Bound,
            dto.WorkSummaryId,
            dto.SlotNo,
            dto.UploadedAt);

    public static string ContentLocation(Guid workOrderId, Guid evidenceId) =>
        $"/api/v1/work-orders/{workOrderId}/evidence/{evidenceId}/content";
}

/// <summary>Multipart form of WEV-API-001: the file and the closed evidence type. Tenant, uploader, Visit and scan status are never client input.</summary>
public sealed class WorkEvidenceUploadForm
{
    public IFormFile? File { get; init; }

    public string? EvidenceType { get; init; }
}

public sealed record ServiceReportPauseResponse(DateTime PausedAt, DateTime? ResumedAt);

public sealed record ServiceReportSessionResponse(
    Guid WorkSessionId, string Status, DateTime CheckInAt, DateTime? CheckOutAt, IReadOnlyList<ServiceReportPauseResponse> Pauses);

public sealed record ServiceReportVisitResponse(
    Guid ServiceVisitId,
    string VisitType,
    string Status,
    Guid? AssignedTechnicianId,
    DateTime? ScheduledStartAt,
    DateTime? ScheduledEndAt,
    DateTime? CompletedAt,
    IReadOnlyList<ServiceReportSessionResponse> Sessions);

public sealed record ServiceReportEvidenceResponse(
    Guid EvidenceId, string EvidenceType, string FileName, string MimeType, long SizeBytes, int SlotNo, DateTime BoundAt);

public sealed record ServiceReportSummaryResponse(
    Guid WorkSummaryId,
    Guid ServiceVisitId,
    int RevisionNo,
    string SummaryText,
    string RepairOutcomeCode,
    IReadOnlyList<ServiceReportEvidenceResponse> Evidence);

public sealed record ServiceReportAcceptanceResponse(int RoundNo, string Decision, string? DecisionReason, DateTime DecidedAt, Guid AcceptanceContactId);

public sealed record ServiceReportHeaderResponse(
    Guid WorkOrderId,
    string WorkOrderNo,
    string Status,
    DateTime CreatedAt,
    DateTime? ClosedAt,
    Guid RepairRequestId,
    string? RepairRequestNo,
    string? CustomerCode,
    string? SiteCode,
    string? EquipmentCode);

/// <summary>
/// SVR-API-001 response for the Technician and the Acceptance Contact (`docs/16` OD5). This type has <b>no</b> cost member of any kind —
/// not a nullable property, not a flag, not metadata — so no cost property name can ever be serialized for those audiences.
/// </summary>
public sealed record ServiceReportResponse(
    ServiceReportHeaderResponse WorkOrder,
    IReadOnlyList<ServiceReportVisitResponse> Visits,
    IReadOnlyList<ServiceReportSummaryResponse> WorkSummaries,
    IReadOnlyList<ServiceReportAcceptanceResponse> Acceptance);

public sealed record ServiceReportCostResponse(
    decimal TotalAmount, string CurrencyCode, string? Note, Guid PreparedBy, DateTime PreparedAt, Guid? ReviewedBy, DateTime? ReviewedAt);

/// <summary>SVR-API-001 response for an in-scope Team Lead or Supervisor: the full report including the Cost Summary section (null until one exists).</summary>
public sealed record ServiceReportFullResponse(
    ServiceReportHeaderResponse WorkOrder,
    IReadOnlyList<ServiceReportVisitResponse> Visits,
    IReadOnlyList<ServiceReportSummaryResponse> WorkSummaries,
    IReadOnlyList<ServiceReportAcceptanceResponse> Acceptance,
    ServiceReportCostResponse? CostSummary);

public static class ServiceReportResponses
{
    /// <summary>
    /// Actor-specific shaping: the full type only for the Team Lead / Supervisor audience; every other audience is mapped to the
    /// cost-free type. The cost section of the application result is not even read for them.
    /// </summary>
    public static object ToResponse(ServiceReportResult result)
    {
        var report = result.Report;
        var header = ToResponse(report.WorkOrder);
        var visits = report.Visits.Select(ToResponse).ToList();
        var summaries = report.WorkSummaries.Select(ToResponse).ToList();
        var acceptance = report.Acceptance.Select(ToResponse).ToList();

        if (result.Audience != ReportAudience.TeamLeadSupervisor)
        {
            return new ServiceReportResponse(header, visits, summaries, acceptance);
        }

        return new ServiceReportFullResponse(
            header,
            visits,
            summaries,
            acceptance,
            result.Cost is { } cost
                ? new ServiceReportCostResponse(cost.TotalAmount, cost.CurrencyCode, cost.Note, cost.PreparedBy, cost.PreparedAt, cost.ReviewedBy, cost.ReviewedAt)
                : null);
    }

    private static ServiceReportHeaderResponse ToResponse(ServiceReportHeaderDto dto) =>
        new(
            dto.WorkOrderId,
            dto.WorkOrderNo,
            WorkOrderStatusCodes.ToCode(dto.Status),
            dto.CreatedAt,
            dto.ClosedAt,
            dto.RepairRequestId,
            dto.RepairRequestNo,
            dto.CustomerCode,
            dto.SiteCode,
            dto.EquipmentCode);

    private static ServiceReportVisitResponse ToResponse(ServiceReportVisitDto dto) =>
        new(
            dto.ServiceVisitId,
            ServiceVisitTypeCodes.ToCode(dto.VisitType),
            ServiceVisitStatusCodes.ToCode(dto.Status),
            dto.AssignedTechnicianId,
            dto.ScheduledStartAt,
            dto.ScheduledEndAt,
            dto.CompletedAt,
            dto.Sessions.Select(session => new ServiceReportSessionResponse(
                session.WorkSessionId,
                WorkSessionStatusCodes.ToCode(session.Status),
                session.CheckInAt,
                session.CheckOutAt,
                session.Pauses.Select(pause => new ServiceReportPauseResponse(pause.PausedAt, pause.ResumedAt)).ToList())).ToList());

    private static ServiceReportSummaryResponse ToResponse(ServiceReportSummaryDto dto) =>
        new(
            dto.WorkSummaryId,
            dto.ServiceVisitId,
            dto.RevisionNo,
            dto.SummaryText,
            RepairOutcomeCodes.ToCode(dto.RepairOutcomeCode),
            dto.Evidence.Select(item => new ServiceReportEvidenceResponse(
                item.EvidenceId, WorkEvidenceTypeCodes.ToCode(item.EvidenceType), item.FileName, item.MimeType, item.SizeBytes, item.SlotNo, item.BoundAt)).ToList());

    private static ServiceReportAcceptanceResponse ToResponse(ServiceReportAcceptanceDto dto) =>
        new(dto.RoundNo, dto.Decision == AcceptanceDecision.Accept ? "ACCEPT" : "REJECT", dto.DecisionReason, dto.DecidedAt, dto.AcceptanceContactId);
}
