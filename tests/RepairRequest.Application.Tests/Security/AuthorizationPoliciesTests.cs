using RepairRequest.Application.Security;
using RepairRequest.Domain.Security;

namespace RepairRequest.Application.Tests.Security;

/// <summary>Role-capability catalog must match RR-REQ-001 sections 3/13, S1-003 decision 2 and DEC-PRE-S1-007R-07/10.</summary>
public class AuthorizationPoliciesTests
{
    public static TheoryData<string, string[]> ExpectedPolicies => new()
    {
        { AuthorizationPolicies.MasterDataRead, RoleCodes.All.ToArray() },
        { AuthorizationPolicies.MasterDataManage, [RoleCodes.Administrator] },
        {
            AuthorizationPolicies.RepairRequestRead,
            [RoleCodes.Requester, RoleCodes.Approver, RoleCodes.Coordinator, RoleCodes.Technician, RoleCodes.TeamLead, RoleCodes.Supervisor]
        },
        { AuthorizationPolicies.RepairRequestDraft, [RoleCodes.Requester] },
        { AuthorizationPolicies.RepairRequestReview, [RoleCodes.Approver] },
        { AuthorizationPolicies.RoutingRecovery, [RoleCodes.Administrator] },
        {
            AuthorizationPolicies.WorkOrderRead,
            [RoleCodes.Requester, RoleCodes.Approver, RoleCodes.Coordinator, RoleCodes.TeamLead, RoleCodes.Supervisor]
        },
        { AuthorizationPolicies.RepairRequestConvert, [RoleCodes.Coordinator] },
        { AuthorizationPolicies.WorkOrderSchedule, [RoleCodes.Coordinator] },
        { AuthorizationPolicies.ServiceVisitManage, [RoleCodes.Coordinator] },
        { AuthorizationPolicies.MyVisitsRead, [RoleCodes.Technician] },
        { AuthorizationPolicies.WorkSessionCheckIn, [RoleCodes.Technician] },
        { AuthorizationPolicies.WorkSessionPause, [RoleCodes.Technician] },
        { AuthorizationPolicies.WorkSessionResume, [RoleCodes.Technician] },
        { AuthorizationPolicies.WorkSessionCheckOut, [RoleCodes.Technician] },
        { AuthorizationPolicies.WorkSessionRead, [RoleCodes.Technician] },
        { AuthorizationPolicies.WorkOrderSubmitWorkSummary, [RoleCodes.Technician] },
        { AuthorizationPolicies.WorkOrderSubmitForAcceptance, [RoleCodes.TeamLead, RoleCodes.Supervisor] },
        { AuthorizationPolicies.WorkOrderReadWorkSummary, [RoleCodes.Technician, RoleCodes.TeamLead, RoleCodes.Supervisor] },
        { AuthorizationPolicies.WorkOrderReadEligibleAcceptanceContacts, [RoleCodes.TeamLead, RoleCodes.Supervisor] },
        { AuthorizationPolicies.WorkOrderAccept, [RoleCodes.Requester] },
        { AuthorizationPolicies.WorkOrderReject, [RoleCodes.Requester] }
    };

    [Theory]
    [MemberData(nameof(ExpectedPolicies))]
    public void Policy_AllowsExactlyTheApprovedRoles(string policyName, string[] expectedRoles)
    {
        Assert.Equal(expectedRoles.Order(), AuthorizationPolicies.AllowedRoles[policyName].Order());
    }

    [Fact]
    public void Catalog_DefinesOnlyTheApprovedPolicies()
    {
        Assert.Equal(22, AuthorizationPolicies.AllowedRoles.Count);
    }

    [Theory]
    [InlineData(AuthorizationPolicies.RepairRequestRead)]
    [InlineData(AuthorizationPolicies.RepairRequestDraft)]
    [InlineData(AuthorizationPolicies.RepairRequestReview)]
    [InlineData(AuthorizationPolicies.WorkOrderRead)]
    [InlineData(AuthorizationPolicies.RepairRequestConvert)]
    [InlineData(AuthorizationPolicies.WorkOrderSchedule)]
    [InlineData(AuthorizationPolicies.ServiceVisitManage)]
    public void Administrator_IsNotGrantedRepairRequestCapabilities(string policyName)
    {
        Assert.DoesNotContain(RoleCodes.Administrator, AuthorizationPolicies.AllowedRoles[policyName]);
    }

    [Fact]
    public void RepairRequestConvert_IsCoordinatorOnly()
    {
        // BR-03; ST-RR-008; UC-WO-001 — Convert is a Coordinator-only action.
        Assert.Equal([RoleCodes.Coordinator], AuthorizationPolicies.AllowedRoles[AuthorizationPolicies.RepairRequestConvert]);
    }

    [Fact]
    public void WorkOrderSchedule_AndServiceVisitManage_AreCoordinatorOnly()
    {
        // ST-WO-001; ST-SV-004..009; UC-WO-003/005..009 — Schedule and every Visit-management action are Coordinator-only.
        Assert.Equal([RoleCodes.Coordinator], AuthorizationPolicies.AllowedRoles[AuthorizationPolicies.WorkOrderSchedule]);
        Assert.Equal([RoleCodes.Coordinator], AuthorizationPolicies.AllowedRoles[AuthorizationPolicies.ServiceVisitManage]);
    }

    [Fact]
    public void MyVisitsRead_AndWorkSessionCheckIn_AreTechnicianOnly()
    {
        // ST-WS-001; UC-WO-016; BR-05; S3-001 — My Visits and Check-in are Technician-only.
        Assert.Equal([RoleCodes.Technician], AuthorizationPolicies.AllowedRoles[AuthorizationPolicies.MyVisitsRead]);
        Assert.Equal([RoleCodes.Technician], AuthorizationPolicies.AllowedRoles[AuthorizationPolicies.WorkSessionCheckIn]);
    }

    [Fact]
    public void WorkSessionPause_AndWorkSessionRead_AreTechnicianOnly()
    {
        // ST-WS-002; UC-WO-012; S3-002 — Pause and the current-session read are Technician-only.
        Assert.Equal([RoleCodes.Technician], AuthorizationPolicies.AllowedRoles[AuthorizationPolicies.WorkSessionPause]);
        Assert.Equal([RoleCodes.Technician], AuthorizationPolicies.AllowedRoles[AuthorizationPolicies.WorkSessionRead]);
    }

    [Fact]
    public void WorkSessionResume_IsTechnicianOnly()
    {
        // ST-WS-003; UC-WO-012; S3-003 — Resume is Technician-only.
        Assert.Equal([RoleCodes.Technician], AuthorizationPolicies.AllowedRoles[AuthorizationPolicies.WorkSessionResume]);
    }

    [Fact]
    public void WorkSessionCheckOut_IsTechnicianOnly()
    {
        // ST-WS-004; UC-WO-016; S3-004 — Check-out is Technician-only.
        Assert.Equal([RoleCodes.Technician], AuthorizationPolicies.AllowedRoles[AuthorizationPolicies.WorkSessionCheckOut]);
    }

    [Fact]
    public void WorkOrderSubmitWorkSummary_IsTechnicianOnly()
    {
        // ST-WO-003; UC-WO-020; `docs/13` §4.15 Decision 2 — Submit Work Summary is Technician-only (corrected
        // from the baseline's "Team Lead").
        Assert.Equal([RoleCodes.Technician], AuthorizationPolicies.AllowedRoles[AuthorizationPolicies.WorkOrderSubmitWorkSummary]);
    }

    [Fact]
    public void WorkOrderSubmitForAcceptance_IsTeamLeadOrSupervisor()
    {
        // ST-WO-004; `docs/13` §4.15 Decision 2 — either the Team Lead or the Supervisor, not Supervisor alone.
        Assert.Equal(
            new[] { RoleCodes.TeamLead, RoleCodes.Supervisor }.Order(),
            AuthorizationPolicies.AllowedRoles[AuthorizationPolicies.WorkOrderSubmitForAcceptance].Order());
    }

    [Fact]
    public void WorkOrderReadWorkSummary_IsTechnicianTeamLeadOrSupervisor()
    {
        Assert.Equal(
            new[] { RoleCodes.Technician, RoleCodes.TeamLead, RoleCodes.Supervisor }.Order(),
            AuthorizationPolicies.AllowedRoles[AuthorizationPolicies.WorkOrderReadWorkSummary].Order());
    }

    [Fact]
    public void WorkOrderReadEligibleAcceptanceContacts_IsTeamLeadOrSupervisor()
    {
        // `docs/13` §4.16 — the lookup exists only to support Submit for Acceptance, same actors.
        Assert.Equal(
            new[] { RoleCodes.TeamLead, RoleCodes.Supervisor }.Order(),
            AuthorizationPolicies.AllowedRoles[AuthorizationPolicies.WorkOrderReadEligibleAcceptanceContacts].Order());
    }

    [Fact]
    public void WorkOrderAccept_IsRequesterOnly()
    {
        // ST-WO-005; `docs/13` §4.16 Decision 1 — the existing REQUESTER role, not a new "Customer" role.
        // Role gate only; the real check (exact AcceptanceContactId match) is resource-specific, enforced by the service.
        Assert.Equal([RoleCodes.Requester], AuthorizationPolicies.AllowedRoles[AuthorizationPolicies.WorkOrderAccept]);
    }

    [Fact]
    public void WorkOrderReject_IsRequesterOnly()
    {
        // ST-WO-007; `docs/13` §4.17 — the same REQUESTER role as WorkOrderAccept, its own named policy.
        // Role gate only; the real check (exact AcceptanceContactId match) is resource-specific, enforced by the service.
        Assert.Equal([RoleCodes.Requester], AuthorizationPolicies.AllowedRoles[AuthorizationPolicies.WorkOrderReject]);
    }

    [Fact]
    public void WorkOrderRead_ExcludesTechnician_UntilAssignmentRulesAreDefined()
    {
        // DEC-S2-001-03: TECHNICIAN is deliberately excluded from Work Order read scope in S2-001.
        Assert.DoesNotContain(RoleCodes.Technician, AuthorizationPolicies.AllowedRoles[AuthorizationPolicies.WorkOrderRead]);
    }

    [Fact]
    public void RoutingRecovery_IsAdministratorOnly_AndNoBusinessRoleGetsIt()
    {
        Assert.Equal([RoleCodes.Administrator], AuthorizationPolicies.AllowedRoles[AuthorizationPolicies.RoutingRecovery]);
        Assert.DoesNotContain(RoleCodes.Approver, AuthorizationPolicies.AllowedRoles[AuthorizationPolicies.RoutingRecovery]);
    }

    [Fact]
    public void Catalog_ReferencesOnlyApprovedRoleCodes()
    {
        Assert.All(
            AuthorizationPolicies.AllowedRoles.Values.SelectMany(roles => roles),
            role => Assert.Contains(role, RoleCodes.All));
    }
}
