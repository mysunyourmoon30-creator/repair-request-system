using RepairRequest.Domain.Security;

namespace RepairRequest.Domain.Tests.Security;

/// <summary>Role catalog must match RR-REQ-001 section 3 / RR-TC-001 section 2 exactly.</summary>
public class RoleCodesTests
{
    [Fact]
    public void Catalog_MatchesBaselineRolesExactly()
    {
        Assert.Equal(
            ["REQUESTER", "APPROVER", "COORDINATOR", "TECHNICIAN", "TEAM_LEAD", "SUPERVISOR", "ADMINISTRATOR"],
            RoleCodes.All);
    }

    [Fact]
    public void Catalog_HasNoDuplicates()
    {
        Assert.Equal(RoleCodes.All.Count, RoleCodes.All.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Theory]
    [InlineData("CUSTOMER")]
    [InlineData("ADMIN")]
    [InlineData("WAREHOUSE")]
    [InlineData("MANAGEMENT")]
    [InlineData("SYSTEM_ADMIN")]
    public void Catalog_DoesNotContainUnapprovedAliases(string alias)
    {
        Assert.DoesNotContain(alias, RoleCodes.All);
    }
}
