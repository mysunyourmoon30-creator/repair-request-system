using RepairRequest.Application.Security;
using RepairRequest.Domain.Security;

namespace RepairRequest.Application.Tests.Security;

/// <summary>Role-capability catalog must match RR-REQ-001 sections 3/13 and S1-003 decision 2.</summary>
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
        { AuthorizationPolicies.RepairRequestReview, [RoleCodes.Approver] }
    };

    [Theory]
    [MemberData(nameof(ExpectedPolicies))]
    public void Policy_AllowsExactlyTheApprovedRoles(string policyName, string[] expectedRoles)
    {
        Assert.Equal(expectedRoles.Order(), AuthorizationPolicies.AllowedRoles[policyName].Order());
    }

    [Fact]
    public void Catalog_DefinesOnlyTheSprintOnePolicies()
    {
        Assert.Equal(5, AuthorizationPolicies.AllowedRoles.Count);
    }

    [Theory]
    [InlineData(AuthorizationPolicies.RepairRequestRead)]
    [InlineData(AuthorizationPolicies.RepairRequestDraft)]
    [InlineData(AuthorizationPolicies.RepairRequestReview)]
    public void Administrator_IsNotGrantedRepairRequestCapabilities(string policyName)
    {
        Assert.DoesNotContain(RoleCodes.Administrator, AuthorizationPolicies.AllowedRoles[policyName]);
    }

    [Fact]
    public void Catalog_ReferencesOnlyApprovedRoleCodes()
    {
        Assert.All(
            AuthorizationPolicies.AllowedRoles.Values.SelectMany(roles => roles),
            role => Assert.Contains(role, RoleCodes.All));
    }
}
