using RepairRequest.Application.MasterData;

namespace RepairRequest.Api.Contracts.MasterData;

// Request bodies carry only the editable value. Tenant, parent and status are never accepted from the client
// (D-11 / BR-16); unknown JSON members such as tenantId or customerId are ignored. Properties are nullable so a
// missing value is reported as 422 VALIDATION_FAILED by the Application layer rather than as a 400.

public sealed record CustomerCodeRequest(string? CustomerCode);

public sealed record SiteCodeRequest(string? SiteCode);

public sealed record EquipmentCodeRequest(string? EquipmentCode);

public sealed record DeactivateRequest(string? Reason);

/// <summary>Query string of list endpoints: page (1-based), pageSize (clamped to the configured maximum), status.</summary>
public sealed class MasterDataListRequest
{
    public int? Page { get; init; }

    public int? PageSize { get; init; }

    /// <summary>ACTIVE or INACTIVE; omitted lists both.</summary>
    public string? Status { get; init; }
}

public sealed record CustomerResponse(Guid Id, string CustomerCode, string Status, string? DeactivateReason, string RowVersion);

public sealed record SiteResponse(Guid Id, Guid CustomerId, string SiteCode, string Status, string? DeactivateReason, string RowVersion);

public sealed record EquipmentResponse(Guid Id, Guid SiteId, string EquipmentCode, string Status, string? DeactivateReason, string RowVersion);

/// <summary>Focused response projections; EF entities are never serialized.</summary>
public static class MasterDataResponses
{
    public static CustomerResponse ToResponse(CustomerDto dto) =>
        new(dto.Id, dto.CustomerCode, MasterDataStatusCodes.ToCode(dto.Status), dto.DeactivateReason, Convert.ToBase64String(dto.RowVersion));

    public static SiteResponse ToResponse(SiteDto dto) =>
        new(dto.Id, dto.CustomerId, dto.SiteCode, MasterDataStatusCodes.ToCode(dto.Status), dto.DeactivateReason, Convert.ToBase64String(dto.RowVersion));

    public static EquipmentResponse ToResponse(EquipmentDto dto) =>
        new(dto.Id, dto.SiteId, dto.EquipmentCode, MasterDataStatusCodes.ToCode(dto.Status), dto.DeactivateReason, Convert.ToBase64String(dto.RowVersion));
}
