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
        { AuthorizationPolicies.RepairRequestConvert, [RoleCodes.Coordinator] }
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
        Assert.Equal(8, AuthorizationPolicies.AllowedRoles.Count);
    }

    [Theory]
    [InlineData(AuthorizationPolicies.RepairRequestRead)]
    [InlineData(AuthorizationPolicies.RepairRequestDraft)]
    [InlineData(AuthorizationPolicies.RepairRequestReview)]
    [InlineData(AuthorizationPolicies.WorkOrderRead)]
    [InlineData(AuthorizationPolicies.RepairRequestConvert)]
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
