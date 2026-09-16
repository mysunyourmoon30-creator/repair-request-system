namespace RepairRequest.Application.Common;

/// <summary>
/// Fixed identities recorded as the audit actor for system-performed actions (RR-DD-001 AUD-011 "derived actor or
/// system identity"). They are not users and grant no access.
/// </summary>
public static class SystemActors
{
    /// <summary>Actor of malware-scan result audit records.</summary>
    public static readonly Guid MalwareScanner = new("5c4a9b1e-0000-4000-8000-000000000001");

    /// <summary>
    /// Actor of approval routing audit records (ST-RR-003: SUBMITTED -> UNDER_REVIEW is performed by System). The user who
    /// initiated the command that triggered routing is recorded separately in the audit value document.
    /// </summary>
    public static readonly Guid ApprovalRouting = new("5c4a9b1e-0000-4000-8000-000000000002");
}
