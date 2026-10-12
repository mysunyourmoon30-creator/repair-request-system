using RepairRequest.Application.Security;
using RepairRequest.Domain.WorkEvidence;
using RepairRequest.Domain.WorkOrders;

namespace RepairRequest.Application.WorkEvidence;

/// <summary>Work Order header of the Service Report: codes only, as the existing Work Order detail (DEC-S2-001-01).</summary>
public sealed record ServiceReportHeaderDto(
    Guid WorkOrderId,
    string WorkOrderNo,
    WorkOrderStatus Status,
    DateTime CreatedAt,
    DateTime? ClosedAt,
    Guid RepairRequestId,
    string? RepairRequestNo,
    string? CustomerCode,
    string? SiteCode,
    string? EquipmentCode);

/// <summary>One pause period of a Work Session (start and resume only; the free-text pause reason is not part of the report).</summary>
public sealed record ServiceReportPauseDto(DateTime PausedAt, DateTime? ResumedAt);

public sealed record ServiceReportSessionDto(
    Guid WorkSessionId,
    WorkSessionStatus Status,
    DateTime CheckInAt,
    DateTime? CheckOutAt,
    IReadOnlyList<ServiceReportPauseDto> Pauses);

/// <summary>A Service Visit with its Work Sessions. Reschedule, reassign, cancel and missed reasons (free text) are not part of the report.</summary>
public sealed record ServiceReportVisitDto(
    Guid ServiceVisitId,
    ServiceVisitType VisitType,
    ServiceVisitStatus Status,
    Guid? AssignedTechnicianId,
    DateTime? ScheduledStartAt,
    DateTime? ScheduledEndAt,
    DateTime? CompletedAt,
    IReadOnlyList<ServiceReportSessionDto> Sessions);

/// <summary>One item of the evidence snapshot frozen at Submit: always CLEAN, never carries a storage reference or hash.</summary>
public sealed record ServiceReportEvidenceDto(
    Guid EvidenceId,
    WorkEvidenceType EvidenceType,
    string FileName,
    string MimeType,
    long SizeBytes,
    int SlotNo,
    DateTime BoundAt);

/// <summary>A submitted Work Summary round (one per Visit) with its bound evidence set.</summary>
public sealed record ServiceReportSummaryDto(
    Guid WorkSummaryId,
    Guid ServiceVisitId,
    int RevisionNo,
    string SummaryText,
    RepairOutcomeCode RepairOutcomeCode,
    IReadOnlyList<ServiceReportEvidenceDto> Evidence);

/// <summary>A Customer Acceptance decision (append-only history). The contact's name and e-mail snapshot are not part of the report.</summary>
public sealed record ServiceReportAcceptanceDto(
    int RoundNo,
    AcceptanceDecision Decision,
    string? DecisionReason,
    DateTime DecidedAt,
    Guid AcceptanceContactId);

/// <summary>The operational Service Report: everything except the Cost Summary. This type has no cost member of any kind.</summary>
public sealed record ServiceReportDto(
    ServiceReportHeaderDto WorkOrder,
    IReadOnlyList<ServiceReportVisitDto> Visits,
    IReadOnlyList<ServiceReportSummaryDto> WorkSummaries,
    IReadOnlyList<ServiceReportAcceptanceDto> Acceptance);

/// <summary>The Cost Summary related lifecycle section. Only ever loaded for <see cref="ReportAudience.TeamLeadSupervisor"/>.</summary>
public sealed record ServiceReportCostDto(
    decimal TotalAmount,
    string CurrencyCode,
    string? Note,
    Guid PreparedBy,
    DateTime PreparedAt,
    Guid? ReviewedBy,
    DateTime? ReviewedAt);

/// <summary>
/// The Service Report with its audience. <see cref="Cost"/> is queried and populated only for the Team Lead / Supervisor audience; for
/// every other audience the store never reads the cost tables and this member is null, and the API maps those audiences to a response type
/// that has no cost property at all.
/// </summary>
public sealed record ServiceReportResult(ReportAudience Audience, ServiceReportDto Report, ServiceReportCostDto? Cost);

/// <summary>Read port of the Service Report (SVR-API-001): a bounded set of queries, never a per-row query.</summary>
public interface IServiceReportStore
{
    /// <summary>The report of a Work Order the caller may see, or null (404) when missing, out of scope or the caller has no access.</summary>
    Task<ServiceReportResult?> GetAsync(CurrentUser user, Guid workOrderId, CancellationToken cancellationToken);
}

/// <summary>SVR-API-001 (`docs/16` D1, OD4/OD5): the read-only Service Report of a Work Order, assembled from existing persisted facts.</summary>
public sealed class ServiceReportService
{
    private readonly IServiceReportStore _store;

    public ServiceReportService(IServiceReportStore store)
    {
        _store = store;
    }

    public Task<ServiceReportResult?> GetAsync(CurrentUser user, Guid workOrderId, CancellationToken cancellationToken) =>
        _store.GetAsync(user, workOrderId, cancellationToken);
}
