namespace RepairRequest.Domain.Security;

/// <summary>
/// Approved role catalog (RR-REQ-001 section 3; RR-TC-001 section 2 common test data).
/// Customer Acceptance Contact is a per-Work-Order designation of a Requester or Site
/// Contact (RR-DD-001 CAC-004), not a login role. Adding or renaming a role changes
/// locked business semantics and requires a formal Change Request.
/// </summary>
public static class RoleCodes
{
    public const string Requester = "REQUESTER";
    public const string Approver = "APPROVER";
    public const string Coordinator = "COORDINATOR";
    public const string Technician = "TECHNICIAN";
    public const string TeamLead = "TEAM_LEAD";
    public const string Supervisor = "SUPERVISOR";
    public const string Administrator = "ADMINISTRATOR";

    public static IReadOnlyList<string> All { get; } =
    [
        Requester,
        Approver,
        Coordinator,
        Technician,
        TeamLead,
        Supervisor,
        Administrator
    ];
}
