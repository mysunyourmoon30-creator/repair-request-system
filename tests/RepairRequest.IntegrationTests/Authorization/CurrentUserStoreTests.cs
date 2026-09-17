using Microsoft.Extensions.DependencyInjection;
using RepairRequest.Application.Security;
using RepairRequest.Domain.Security;
using RepairRequest.IntegrationTests.Authentication;
using RepairRequest.IntegrationTests.Persistence;

namespace RepairRequest.IntegrationTests.Authorization;

/// <summary>Caller resolution from the identity store (RR-ARCH-001 section 10: roles resolved server-side).</summary>
[Collection(PersistenceDatabaseCollection.Name)]
public sealed class CurrentUserStoreTests : IAsyncLifetime
{
    private readonly AuthenticationTestHost _host = new();

    public CurrentUserStoreTests(PersistenceDatabaseFixture database)
    {
        _ = database;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task FindAsync_ReturnsTenantAndDatabaseRoles_InASingleSqlCommand()
    {
        var tenantId = Guid.NewGuid();
        var user = await _host.CreateUserInTenantAsync(tenantId, RoleCodes.Requester, RoleCodes.Approver);
        await using var scope = _host.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ICurrentUserStore>();

        _host.Logs.Clear();
        var resolved = await store.FindAsync(user.Id, CancellationToken.None);

        Assert.NotNull(resolved);
        Assert.Equal(1, _host.Logs.ExecutedDbCommandCount);
        Assert.Equal(user.Id, resolved.UserId);
        Assert.Equal(tenantId, resolved.TenantId);
        Assert.Equal([RoleCodes.Approver, RoleCodes.Requester], resolved.Roles.Order());
    }

    [Fact]
    public async Task FindAsync_UserWithoutRoles_ResolvesWithNoRoles()
    {
        var user = await _host.CreateUserAsync();
        await using var scope = _host.CreateScope();

        var resolved = await scope.ServiceProvider.GetRequiredService<ICurrentUserStore>().FindAsync(user.Id, CancellationToken.None);

        Assert.NotNull(resolved);
        Assert.Empty(resolved.Roles);
    }

    [Fact]
    public async Task FindAsync_UnknownOrEmptyUser_ReturnsNull()
    {
        await using var scope = _host.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ICurrentUserStore>();

        Assert.Null(await store.FindAsync(Guid.NewGuid(), CancellationToken.None));
        Assert.Null(await store.FindAsync(Guid.Empty, CancellationToken.None));
    }
}
