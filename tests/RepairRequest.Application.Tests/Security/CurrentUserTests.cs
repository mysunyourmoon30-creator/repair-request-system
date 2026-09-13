using RepairRequest.Application.Security;
using RepairRequest.Domain.Security;

namespace RepairRequest.Application.Tests.Security;

public class CurrentUserTests
{
    private static CurrentUser Create(params string[] roles) => new(Guid.NewGuid(), Guid.NewGuid(), roles);

    [Fact]
    public void UnapprovedRoleNames_AreIgnored()
    {
        var user = Create(RoleCodes.Requester, "ADMIN", "SYSTEM_ADMIN", "administrator");

        Assert.Equal([RoleCodes.Requester], user.Roles);
    }

    [Fact]
    public void AdministratorOnly_HasTenantWideConfigurationScope_ButNoBusinessRole()
    {
        var user = Create(RoleCodes.Administrator);

        Assert.True(user.HasTenantWideConfigurationScope);
        Assert.False(user.HasBusinessRole);
        Assert.False(user.HasSiteWideRequestScope);
    }

    [Fact]
    public void RequesterOnly_HasBusinessRole_ButNotSiteWideRequestScope()
    {
        var user = Create(RoleCodes.Requester);

        Assert.True(user.HasBusinessRole);
        Assert.True(user.IsRequester);
        Assert.False(user.HasSiteWideRequestScope);
        Assert.False(user.HasTenantWideConfigurationScope);
    }

    [Theory]
    [InlineData(RoleCodes.Approver)]
    [InlineData(RoleCodes.Coordinator)]
    [InlineData(RoleCodes.Technician)]
    [InlineData(RoleCodes.TeamLead)]
    [InlineData(RoleCodes.Supervisor)]
    public void NonRequesterBusinessRoles_HaveSiteWideRequestScope(string role)
    {
        var user = Create(role);

        Assert.True(user.HasSiteWideRequestScope);
        Assert.False(user.HasTenantWideConfigurationScope);
    }

    [Fact]
    public void MultipleRoles_CombineCapabilities()
    {
        var user = Create(RoleCodes.Administrator, RoleCodes.Requester);

        Assert.True(user.HasTenantWideConfigurationScope);
        Assert.True(user.HasBusinessRole);
        Assert.True(user.IsInAnyRole([RoleCodes.Approver, RoleCodes.Requester]));
        Assert.False(user.IsInAnyRole([RoleCodes.Approver, RoleCodes.Supervisor]));
    }

    [Fact]
    public void MissingIdentifiers_AreRejected()
    {
        Assert.Throws<ArgumentException>(() => new CurrentUser(Guid.Empty, Guid.NewGuid(), []));
        Assert.Throws<ArgumentException>(() => new CurrentUser(Guid.NewGuid(), Guid.Empty, []));
    }
}
