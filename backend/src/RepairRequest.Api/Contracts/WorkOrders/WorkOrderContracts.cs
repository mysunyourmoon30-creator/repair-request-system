using RepairRequest.Application.WorkOrders;

namespace RepairRequest.Api.Contracts.WorkOrders;

/// <summary>Query string of the Work Order list: page (1-based), pageSize (clamped to the configured maximum), status.</summary>
public sealed class WorkOrderListRequest
{
    public int? Page { get; init; }

    public int? PageSize { get; init; }

    /// <summary>One of the Work Order status codes (RR-DD-001 WO-005); omitted lists every status.</summary>
    public string? Status { get; init; }
}

/// <summary>
/// Work Order list/detail response (S2-001). Reused for both the list and the detail action per the "minimal
/// necessary" detail page (DEC-S2-001-01..04). Customer/Site/Equipment are codes, not display names — the entities
/// expose no name field. Scheduled Date and Assigned Team/Technician are intentionally absent.
/// </summary>
public sealed record WorkOrderResponse(
    Guid WorkOrderId,
    string WorkOrderNo,
    string Status,
    Guid RepairRequestId,
    string? RepairRequestNo,
    string? CustomerCode,
    string? SiteCode,
    string? EquipmentCode,
    string RowVersion);

/// <summary>Focused response projection; the EF entity is never serialized.</summary>
public static class WorkOrderResponses
{
    public static WorkOrderResponse ToResponse(WorkOrderDto dto) =>
        new(
            dto.WorkOrderId,
            dto.WorkOrderNo,
            WorkOrderStatusCodes.ToCode(dto.Status),
            dto.RepairRequestId,
            dto.RepairRequestNo,
            dto.CustomerCode,
            dto.SiteCode,
            dto.EquipmentCode,
            Convert.ToBase64String(dto.RowVersion));
}
