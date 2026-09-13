using RepairRequest.Application.Security;
using RepairRequest.Domain.MasterData;

namespace RepairRequest.Application.MasterData;

/// <summary>The caller and correlation id of one master-data command (RR-API-001 section 1 audit linkage).</summary>
public sealed record MasterDataCommandContext(CurrentUser User, Guid CorrelationId);

/// <summary>A bounded page request. Values are validated and clamped by the API before reaching the Application layer.</summary>
public sealed record PageRequest(int Page, int PageSize);

/// <summary>List query: paging plus an optional status filter (inactive masters stay listable, RR-DBD-001 section 6).</summary>
public sealed record MasterDataListQuery(PageRequest Paging, MasterDataStatus? Status);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

public sealed record CustomerDto(Guid Id, string CustomerCode, MasterDataStatus Status, string? DeactivateReason, byte[] RowVersion);

public sealed record SiteDto(Guid Id, Guid CustomerId, string SiteCode, MasterDataStatus Status, string? DeactivateReason, byte[] RowVersion);

public sealed record EquipmentDto(Guid Id, Guid SiteId, string EquipmentCode, MasterDataStatus Status, string? DeactivateReason, byte[] RowVersion);

/// <summary>Canonical upper-snake status codes used by the API contract and audit from/to states.</summary>
public static class MasterDataStatusCodes
{
    public const string Active = "ACTIVE";
    public const string Inactive = "INACTIVE";

    public static string ToCode(MasterDataStatus status) => status switch
    {
        MasterDataStatus.Active => Active,
        MasterDataStatus.Inactive => Inactive,
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };

    public static bool TryParse(string? code, out MasterDataStatus status)
    {
        switch (code)
        {
            case Active:
                status = MasterDataStatus.Active;
                return true;
            case Inactive:
                status = MasterDataStatus.Inactive;
                return true;
            default:
                status = default;
                return false;
        }
    }
}

/// <summary>API field names used as keys of the 422 VALIDATION_FAILED error map.</summary>
public static class MasterDataFields
{
    public const string CustomerCode = "customerCode";
    public const string SiteCode = "siteCode";
    public const string EquipmentCode = "equipmentCode";
    public const string CustomerId = "customerId";
    public const string SiteId = "siteId";
    public const string Reason = "reason";
}
