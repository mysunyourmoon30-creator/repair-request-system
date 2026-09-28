using RepairRequest.Application.Common;

namespace RepairRequest.Application.WorkOrders;

/// <summary>
/// Cost Summary projection (CST-001/003..011; `docs/13` §4.18) — returned by Prepare. Note that this ticket
/// (Prepare only) never sets <see cref="ReviewedBy"/>/<see cref="ReviewedAt"/>; both are always null in every
/// response this ticket can produce, present only so the shape is stable once the future Review ticket populates
/// them.
/// </summary>
public sealed record CostSummaryDto(
    Guid CostSummaryId,
    Guid WorkOrderId,
    decimal TotalAmount,
    string CurrencyCode,
    string? Note,
    Guid PreparedBy,
    DateTime PreparedAt,
    Guid? ReviewedBy,
    DateTime? ReviewedAt,
    byte[] RowVersion);

/// <summary>CST-API-001 Prepare body fields (`docs/13` §4.18) — used as 422 error keys.</summary>
public static class CostSummaryFields
{
    public const string TotalAmount = "totalAmount";
    public const string CurrencyCode = "currencyCode";
    public const string Note = "note";
}

/// <summary>
/// One Work Order awaiting Cost Summary Review, for the Supervisor pending-review queue (`docs/13` §4.19 — a
/// technical addition, not baseline-documented). A lightweight projection, never the full Work Order aggregate
/// graph, mirroring <see cref="MyVisitSummaryDto"/>'s own "no full aggregate graph for list" convention.
/// </summary>
public sealed record PendingCostSummaryReviewDto(
    Guid WorkOrderId,
    string WorkOrderNo,
    string? SiteCode,
    decimal TotalAmount,
    string CurrencyCode,
    Guid PreparedBy,
    DateTime PreparedAt);

/// <summary>Pending Cost Summary Review queue: paging only, always the Supervisor's own site-wide unreviewed set, newest-prepared-first.</summary>
public sealed record PendingCostSummaryReviewQuery(PageRequest Paging);
