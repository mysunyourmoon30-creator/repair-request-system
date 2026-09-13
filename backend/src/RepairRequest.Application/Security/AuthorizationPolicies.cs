using RepairRequest.Domain.Security;

namespace RepairRequest.Application.Security;

/// <summary>
/// Central role-capability catalog derived from RR-REQ-001 sections 3 and 13 and S1-003 decision 2.
/// Controllers reference policy names only; role codes are never compared inline.
/// Work Order / Visit / Session policies belong to Sprint 2.
/// </summary>
public static class AuthorizationPolicies
{
    /// <summary>Read Customer / Site / Equipment within data scope (e.g. selection lists).</summary>
    public const string MasterDataRead = "MasterData.Read";

    /// <summary>Configure master data (RR-REQ-001 section 3: Administrator).</summary>
    public const string MasterDataManage = "MasterData.Manage";

    /// <summary>Search / view Repair Requests within data scope (FR-09).</summary>
    public const string RepairRequestRead = "RepairRequest.Read";

    /// <summary>Create / edit / submit / cancel own Repair Request (RR-REQ-001 section 13: Requester).</summary>
    public const string RepairRequestDraft = "RepairRequest.Draft";

    /// <summary>Approve / reject / return for correction (RR-REQ-001 section 13: Approver).</summary>
    public const string RepairRequestReview = "RepairRequest.Review";

    public static IReadOnlyDictionary<string, IReadOnlyList<string>> AllowedRoles { get; } =
        new Dictionary<string, IReadOnlyList<string>>
        {
            [MasterDataRead] = RoleCodes.All,
            [MasterDataManage] = [RoleCodes.Administrator],
            [RepairRequestRead] =
            [
                RoleCodes.Requester,
                RoleCodes.Approver,
                RoleCodes.Coordinator,
                RoleCodes.Technician,
                RoleCodes.TeamLead,
                RoleCodes.Supervisor
            ],
            [RepairRequestDraft] = [RoleCodes.Requester],
            [RepairRequestReview] = [RoleCodes.Approver]
        };
}

/// <summary>Structured security-log event codes for authorization outcomes. Never carry tokens, headers or record ids.</summary>
public static class AuthorizationEventCodes
{
    public const string Unauthenticated = "AUTHZ_UNAUTHENTICATED";
    public const string AccessDenied = "AUTHZ_ACCESS_DENIED";
    public const string ResourceNotFound = "AUTHZ_RESOURCE_NOT_FOUND";
}
