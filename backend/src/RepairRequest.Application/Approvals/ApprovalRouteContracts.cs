using RepairRequest.Application.RepairRequests;
using RepairRequest.Domain.MasterData;

namespace RepairRequest.Application.Approvals;

/// <summary>
/// Create body of an approval route (DEC-PRE-S1-007R-04). Tenant, status, step number and route identity are never
/// client input; the single step is created by the server (DEC-PRE-S1-007R-03).
/// </summary>
public sealed record ApprovalRouteFields(string? RequestCategoryCode, Guid? SiteId, string? ApproverRoleCode, Guid? ApproverUserId);

/// <summary>An approval route with its single step. <see cref="Status"/> reflects ARC-008 is_active.</summary>
public sealed record ApprovalRouteDto(
    Guid Id,
    string RequestCategoryCode,
    Guid? SiteId,
    MasterDataStatus Status,
    short StepNo,
    string ApproverRoleCode,
    Guid? ApproverUserId,
    byte[] RowVersion);

/// <summary>
/// Database view of the references of a route, read in one query: the canonical Category (null when the code does not
/// exist in the tenant), the Site status (null when not requested or not in the tenant) and whether the specific approver
/// user is an APPROVER of the tenant.
/// </summary>
public sealed record ApprovalRouteReferences(LookupSelection? Category, MasterDataStatus? SiteStatus, bool ApproverUserIsApprover);

public enum ApprovalRouteSaveOutcome
{
    Saved,

    /// <summary>A filtered unique index rejected a second ACTIVE route for the same key (DEC-PRE-S1-007R-06).</summary>
    DuplicateActiveRoute,

    /// <summary>The row version no longer matched, or the transaction lost a lock conflict. Nothing was written.</summary>
    ConcurrencyConflict
}

/// <summary>API field names used as keys of the 422 VALIDATION_FAILED error map.</summary>
public static class ApprovalRouteFieldNames
{
    public const string RequestCategoryCode = "requestCategoryCode";
    public const string SiteId = "siteId";
    public const string ApproverRoleCode = "approverRoleCode";
    public const string ApproverUserId = "approverUserId";
}
