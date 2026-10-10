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
        { AuthorizationPolicies.WorkOrderReject, [RoleCodes.Requester] },
        { AuthorizationPolicies.CostSummaryPrepare, [RoleCodes.TeamLead] },
        { AuthorizationPolicies.CostSummaryRead, [RoleCodes.TeamLead, RoleCodes.Supervisor] },
        { AuthorizationPolicies.CostSummaryReview, [RoleCodes.Supervisor] },
        { AuthorizationPolicies.CostSummaryReadPendingReview, [RoleCodes.Supervisor] },
        { AuthorizationPolicies.WorkOrderClose, [RoleCodes.Supervisor] },
        { AuthorizationPolicies.CorrectiveActionSubmitPlan, [RoleCodes.TeamLead] },
        { AuthorizationPolicies.CorrectiveActionApprovePlan, [RoleCodes.Supervisor] },
        { AuthorizationPolicies.CorrectiveActionScheduleRework, [RoleCodes.Coordinator] },
        { AuthorizationPolicies.WorkOrderCancel, [RoleCodes.Supervisor] },
        { AuthorizationPolicies.AuditTimelineRead, [RoleCodes.Requester, RoleCodes.Approver, RoleCodes.Coordinator, RoleCodes.TeamLead, RoleCodes.Supervisor] }
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
        Assert.Equal(32, AuthorizationPolicies.AllowedRoles.Count);
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
    public void CorrectiveActionScheduleRework_IsCoordinatorOnly()
    {
        // CA-API-003; `docs/13` §4.22 — Portfolio Project Owner directive, not a baseline-literal actor (baseline's
        // own ST-WO-010 actor is the assigned Technician, for the separate, later "Start Rework"/Check-in
        // transition this ticket does not implement).
        Assert.Equal([RoleCodes.Coordinator], AuthorizationPolicies.AllowedRoles[AuthorizationPolicies.CorrectiveActionScheduleRework]);
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
    public void CostSummaryPrepare_IsTeamLeadOnly()
    {
        // CST-API-001; BR-08; `docs/13` §4.18 — the first Team-Lead-only policy in this codebase (every prior
        // Team Lead capability was shared with Supervisor). Supervisor is deliberately excluded: Review is a
        // separate action, enforced with its own policy.
        Assert.Equal([RoleCodes.TeamLead], AuthorizationPolicies.AllowedRoles[AuthorizationPolicies.CostSummaryPrepare]);
    }

    [Fact]
    public void CostSummaryRead_IsTeamLeadOrSupervisor_AndExcludesEveryOtherRole()
    {
        // `docs/13` §4.19 — the same two actors as the write actions on this resource; Requester/Technician/
        // Approver/Coordinator/Administrator must never see cost amounts.
        Assert.Equal(
            new[] { RoleCodes.TeamLead, RoleCodes.Supervisor }.Order(),
            AuthorizationPolicies.AllowedRoles[AuthorizationPolicies.CostSummaryRead].Order());
        Assert.DoesNotContain(RoleCodes.Requester, AuthorizationPolicies.AllowedRoles[AuthorizationPolicies.CostSummaryRead]);
        Assert.DoesNotContain(RoleCodes.Technician, AuthorizationPolicies.AllowedRoles[AuthorizationPolicies.CostSummaryRead]);
    }

    [Fact]
    public void CostSummaryReview_IsSupervisorOnly()
    {
        // CST-API-002; BR-08; `docs/13` §4.19 — the first Supervisor-only policy in this codebase (every prior
        // Supervisor capability was shared with Team Lead). Team Lead is deliberately excluded: Prepare and
        // Review are separate actions with separate policies. Role gate only; the real check (Separation of
        // Duties — reviewer must not be preparer) is resource-specific, enforced by the service.
        Assert.Equal([RoleCodes.Supervisor], AuthorizationPolicies.AllowedRoles[AuthorizationPolicies.CostSummaryReview]);
    }

    [Fact]
    public void CostSummaryReadPendingReview_IsSupervisorOnly()
    {
        // `docs/13` §4.19 — the lookup exists only to support Review, same actor.
        Assert.Equal([RoleCodes.Supervisor], AuthorizationPolicies.AllowedRoles[AuthorizationPolicies.CostSummaryReadPendingReview]);
    }

    [Fact]
    public void WorkOrderClose_IsSupervisorOnly()
    {
        // ST-WO-006; WO-API-010; BR-08; `docs/13` §4.20 — the baseline actor is Supervisor alone. Team Lead,
        // Coordinator, Requester and Approver are deliberately excluded; site scope and the Close guards are
        // enforced by the service, and there is no Separation of Duties for Close (Q5).
        Assert.Equal([RoleCodes.Supervisor], AuthorizationPolicies.AllowedRoles[AuthorizationPolicies.WorkOrderClose]);
    }

    [Fact]
    public void WorkOrderCancel_IsSupervisorOnly()
    {
        // ST-WO-011; WO-API-011; UC-WO-026; BR-09/BR-12; `docs/13` §4.25 — three independent baseline sources
        // (UC-WO-026's Primary Actor, ST-WO-011's Actor, docs/09 WO-API-011's Actor) all agree: Supervisor alone.
        Assert.Equal([RoleCodes.Supervisor], AuthorizationPolicies.AllowedRoles[AuthorizationPolicies.WorkOrderCancel]);
    }

    [Fact]
    public void AuditTimelineRead_HasTheSameRolesAsWorkOrderRead_AndExcludesTechnicianAndAdministrator()
    {
        // UC-WO-002; AUD-API-001; `docs/15` D3: the timeline is readable by exactly the Work Order read roles.
        Assert.Equal(
            AuthorizationPolicies.AllowedRoles[AuthorizationPolicies.WorkOrderRead],
            AuthorizationPolicies.AllowedRoles[AuthorizationPolicies.AuditTimelineRead]);
        Assert.DoesNotContain(RoleCodes.Technician, AuthorizationPolicies.AllowedRoles[AuthorizationPolicies.AuditTimelineRead]);
        Assert.DoesNotContain(RoleCodes.Administrator, AuthorizationPolicies.AllowedRoles[AuthorizationPolicies.AuditTimelineRead]);
    }

    [Fact]
    public void CorrectiveActionSubmitPlan_IsTeamLeadOnly()
    {
        // ST-CA-002; CA-API-001; `docs/13` §4.21 — role gate only; site scope and the DRAFT-only state guard are
        // enforced by the Application service.
        Assert.Equal([RoleCodes.TeamLead], AuthorizationPolicies.AllowedRoles[AuthorizationPolicies.CorrectiveActionSubmitPlan]);
    }

    [Fact]
    public void CorrectiveActionApprovePlan_IsSupervisorOnly()
    {
        // ST-CA-003; CA-API-002; `docs/13` §4.21 Decision (d) — no Separation of Duties; role gate only.
        Assert.Equal([RoleCodes.Supervisor], AuthorizationPolicies.AllowedRoles[AuthorizationPolicies.CorrectiveActionApprovePlan]);
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
