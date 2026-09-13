using RepairRequest.Domain.MasterData;
using RepairRequest.Domain.RepairRequests;

namespace RepairRequest.Application.RepairRequests;

/// <summary>
/// The S1-005 editable Draft field set (UC-RR-001; RR-DD-001). Every field is optional while DRAFT. Category, priority,
/// location and contact are not accepted until their masters are defined (S1-005 decision E1). Tenant, requester,
/// status and Request No are never client input (D-11 / BR-16).
/// </summary>
public sealed record RepairRequestDraftFields(
    Guid? SiteId,
    Guid? EquipmentId,
    string? Description,
    DateTimeOffset? PreferredStartAt,
    DateTimeOffset? PreferredEndAt);

public sealed record RepairRequestDraftDto(
    Guid Id,
    RepairRequestStatus Status,
    string? RequestNo,
    Guid? SiteId,
    Guid? EquipmentId,
    string? Description,
    DateTime? PreferredStartAt,
    DateTime? PreferredEndAt,
    Guid CreatedBy,
    byte[] RowVersion);

/// <summary>
/// Scope and status of a Site selection, read in one query. <see cref="EquipmentStatus"/> is null when no Equipment
/// was requested or the requested Equipment does not belong to the Site.
/// </summary>
public sealed record DraftSiteSelection(MasterDataStatus SiteStatus, MasterDataStatus? EquipmentStatus);

/// <summary>Canonical upper-snake Repair Request status codes (RR-DD-001 RR-004).</summary>
public static class RepairRequestStatusCodes
{
    public static string ToCode(RepairRequestStatus status) => status switch
    {
        RepairRequestStatus.Draft => "DRAFT",
        RepairRequestStatus.Submitted => "SUBMITTED",
        RepairRequestStatus.UnderReview => "UNDER_REVIEW",
        RepairRequestStatus.Approved => "APPROVED",
        RepairRequestStatus.Rejected => "REJECTED",
        RepairRequestStatus.Cancelled => "CANCELLED",
        RepairRequestStatus.Converted => "CONVERTED",
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };
}

/// <summary>API field names used as keys of the 422 VALIDATION_FAILED error map and in audit values.</summary>
public static class RepairRequestFields
{
    public const string SiteId = "siteId";
    public const string EquipmentId = "equipmentId";
    public const string Description = "description";
    public const string PreferredStartAt = "preferredStartAt";
    public const string PreferredEndAt = "preferredEndAt";
}
