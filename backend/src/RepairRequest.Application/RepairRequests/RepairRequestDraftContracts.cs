using RepairRequest.Domain.MasterData;
using RepairRequest.Domain.RepairRequests;
using RepairRequestAggregate = RepairRequest.Domain.RepairRequests.RepairRequest;

namespace RepairRequest.Application.RepairRequests;

/// <summary>
/// The editable Draft field set (UC-RR-001; RR-DD-001). Every field is optional while DRAFT. Category and Priority are
/// tenant-scoped lookup codes and the request contact is a user reference (DEC-PRE-S1-007-01/02/04, superseding S1-005
/// decision E1 for these fields). Location is not accepted in Sprint 1 (DEC-PRE-S1-007-05). Tenant, requester, status
/// and Request No are never client input (D-11 / BR-16).
/// </summary>
public sealed record RepairRequestDraftFields(
    Guid? SiteId,
    Guid? EquipmentId,
    string? RequestCategoryCode,
    string? PriorityCode,
    Guid? RequestContactId,
    string? Description,
    DateTimeOffset? PreferredStartAt,
    DateTimeOffset? PreferredEndAt);

public sealed record RepairRequestDraftDto(
    Guid Id,
    RepairRequestStatus Status,
    string? RequestNo,
    Guid? SiteId,
    Guid? EquipmentId,
    string? RequestCategoryCode,
    string? PriorityCode,
    Guid? RequestContactId,
    string? Description,
    DateTime? PreferredStartAt,
    DateTime? PreferredEndAt,
    Guid CreatedBy,
    DateTime? SubmittedAt,
    byte[] RowVersion)
{
    public static RepairRequestDraftDto From(RepairRequestAggregate request) =>
        new(
            request.Id,
            request.Status,
            request.RequestNo,
            request.SiteId,
            request.EquipmentId,
            request.RequestCategoryCode,
            request.PriorityCode,
            request.RequestContactId,
            request.Description,
            request.PreferredStartAt,
            request.PreferredEndAt,
            request.CreatedBy,
            request.SubmittedAt,
            request.RowVersion);
}

/// <summary>Selections to validate against the database in one query. Null members are not looked up.</summary>
public sealed record DraftSelectionQuery(
    Guid? SiteId,
    Guid? EquipmentId,
    string? RequestCategoryCode,
    string? PriorityCode,
    Guid? RequestContactId)
{
    public bool IsEmpty => SiteId is null && RequestCategoryCode is null && PriorityCode is null && RequestContactId is null;
}

/// <summary>
/// Database view of a Draft selection, read in one query. <see cref="SiteStatus"/> is null when no Site was requested or the
/// Site is outside the caller's business Site scope (S1-003). <see cref="EquipmentStatus"/> is null when the Equipment does
/// not belong to that Site. A lookup is null when the code does not exist in the caller's tenant; its code is the stored
/// canonical code. <see cref="ContactEligible"/> is true only for a user of the tenant who holds a business role and is
/// assigned to the selected Site (DEC-PRE-S1-007-04).
/// </summary>
public sealed record DraftSelection(
    MasterDataStatus? SiteStatus,
    MasterDataStatus? EquipmentStatus,
    LookupSelection? Category,
    LookupSelection? Priority,
    bool ContactEligible)
{
    public static DraftSelection Nothing { get; } = new(null, null, null, null, false);
}

public sealed record LookupSelection(string Code, MasterDataStatus Status);

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
    public const string RequestCategoryCode = "requestCategoryCode";
    public const string PriorityCode = "priorityCode";
    public const string RequestContactId = "requestContactId";
    public const string Description = "description";
    public const string PreferredStartAt = "preferredStartAt";
    public const string PreferredEndAt = "preferredEndAt";
    public const string DuplicateContinuationReason = "duplicateContinuationReason";
    public const string Attachments = "attachments";
    public const string RequestNo = "requestNo";
    public const string SubmittedAt = "submittedAt";
    public const string DuplicateCount = "duplicateCount";
    public const string Reason = "reason";
}
