using RepairRequest.Application.MasterData;
using RepairRequest.Domain.MasterData;

namespace RepairRequest.Application.Tests.MasterData;

/// <summary>
/// Site and Equipment rules: active parent on create (DEC-PS1-002/003) and on activate (decision D2), dependency guard
/// (DEC-PS1-013), code uniqueness within the parent (DEC-PS1-015) and immutable parent (decision D3).
/// </summary>
public class SiteAndEquipmentServiceTests
{
    private readonly FakeMasterDataStore _store = new();
    private readonly SiteService _sites;
    private readonly EquipmentService _equipment;
    private readonly Guid _tenantId = Guid.NewGuid();

    public SiteAndEquipmentServiceTests()
    {
        var clock = new FixedClock(DateTimeOffset.UtcNow);
        _sites = new SiteService(_store, clock);
        _equipment = new EquipmentService(_store, clock);
    }

    private MasterDataCommandContext Context() => FakeMasterDataStore.AdministratorContext(_tenantId);

    // ---------------- Site ----------------

    [Fact]
    public async Task CreateSite_UnderActiveCustomer_AddsSiteWithAuditInOneSerializableCommand()
    {
        var customer = _store.SeedCustomer(_tenantId, "CUST-1");

        var result = await _sites.CreateAsync(Context(), customer.Id, "SITE-1", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(customer.Id, result.Value!.CustomerId);
        var audit = Assert.Single(_store.Audits);
        Assert.Equal("SITE_CREATED", audit.ActionCode);
        Assert.Contains(customer.Id.ToString(), audit.NewValueJson);
        Assert.Equal(1, _store.SerializableRuns);
    }

    [Fact]
    public async Task CreateSite_UnderOtherTenantCustomer_ReturnsNotFound()
    {
        var foreign = _store.SeedCustomer(Guid.NewGuid(), "CUST-1");

        var result = await _sites.CreateAsync(Context(), foreign.Id, "SITE-1", CancellationToken.None);

        Assert.Equal(MasterDataFailure.NotFound, result.Error?.Failure);
        Assert.Empty(_store.Sites);
    }

    [Fact]
    public async Task CreateSite_UnderInactiveCustomer_Returns422OnCustomerId()
    {
        var customer = _store.SeedCustomer(_tenantId, "CUST-1", inactiveReason: "Closed");

        var result = await _sites.CreateAsync(Context(), customer.Id, "SITE-1", CancellationToken.None);

        Assert.Equal(MasterDataFailure.ValidationFailed, result.Error?.Failure);
        Assert.True(result.Error!.Errors.ContainsKey(MasterDataFields.CustomerId));
        Assert.Empty(_store.Sites);
    }

    [Fact]
    public async Task CreateSite_DuplicateWithinCustomer_Returns422_ButSameCodeUnderAnotherCustomerIsAllowed()
    {
        var customerA = _store.SeedCustomer(_tenantId, "CUST-A");
        var customerB = _store.SeedCustomer(_tenantId, "CUST-B");
        _store.SeedSite(customerA, "SITE-1");

        var duplicate = await _sites.CreateAsync(Context(), customerA.Id, "SITE-1", CancellationToken.None);
        var otherCustomer = await _sites.CreateAsync(Context(), customerB.Id, "SITE-1", CancellationToken.None);

        Assert.Equal(MasterDataFailure.ValidationFailed, duplicate.Error?.Failure);
        Assert.True(duplicate.Error!.Errors.ContainsKey(MasterDataFields.SiteCode));
        Assert.True(otherCustomer.Succeeded);
    }

    [Fact]
    public async Task UpdateSite_ChangesCodeOnly_CustomerStaysTheSame()
    {
        var customer = _store.SeedCustomer(_tenantId, "CUST-1");
        var site = _store.SeedSite(customer, "SITE-1");

        var result = await _sites.UpdateAsync(Context(), site.Id, FakeMasterDataStore.InitialRowVersion, "SITE-9", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("SITE-9", result.Value!.SiteCode);
        Assert.Equal(customer.Id, result.Value.CustomerId);
    }

    [Fact]
    public async Task ActivateSite_UnderInactiveCustomer_Returns422AndStaysInactive()
    {
        var customer = _store.SeedCustomer(_tenantId, "CUST-1", inactiveReason: "Closed");
        var site = _store.SeedSite(customer, "SITE-1", inactiveReason: "Closed");

        var result = await _sites.ActivateAsync(Context(), site.Id, FakeMasterDataStore.InitialRowVersion, CancellationToken.None);

        Assert.Equal(MasterDataFailure.ValidationFailed, result.Error?.Failure);
        Assert.True(result.Error!.Errors.ContainsKey(MasterDataFields.CustomerId));
        Assert.Equal(MasterDataStatus.Inactive, site.Status);
        Assert.Equal(0, _store.SaveCount);
    }

    [Fact]
    public async Task ActivateSite_UnderActiveCustomer_ClearsReason()
    {
        var customer = _store.SeedCustomer(_tenantId, "CUST-1");
        var site = _store.SeedSite(customer, "SITE-1", inactiveReason: "Closed");

        var result = await _sites.ActivateAsync(Context(), site.Id, FakeMasterDataStore.InitialRowVersion, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Null(site.DeactivateReason);
        Assert.Equal("SITE_ACTIVATED", Assert.Single(_store.Audits).ActionCode);
    }

    [Fact]
    public async Task DeactivateSite_WithActiveEquipment_Returns409_AndEquipmentStaysActive()
    {
        var customer = _store.SeedCustomer(_tenantId, "CUST-1");
        var site = _store.SeedSite(customer, "SITE-1");
        var equipment = _store.SeedEquipment(site, "EQ-1");

        var result = await _sites.DeactivateAsync(Context(), site.Id, FakeMasterDataStore.InitialRowVersion, "Closed", CancellationToken.None);

        Assert.Equal(MasterDataFailure.StateConflict, result.Error?.Failure);
        Assert.Equal(1, result.Error!.ActiveChildCount);
        Assert.Equal(MasterDataStatus.Active, site.Status);
        Assert.Equal(MasterDataStatus.Active, equipment.Status);
    }

    // ---------------- Equipment ----------------

    [Fact]
    public async Task CreateEquipment_UnderActiveSite_Succeeds()
    {
        var site = _store.SeedSite(_store.SeedCustomer(_tenantId, "CUST-1"), "SITE-1");

        var result = await _equipment.CreateAsync(Context(), site.Id, "EQ-1", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(site.Id, result.Value!.SiteId);
        Assert.Equal("EQUIPMENT_CREATED", Assert.Single(_store.Audits).ActionCode);
    }

    [Fact]
    public async Task CreateEquipment_UnderOtherTenantSite_ReturnsNotFound()
    {
        var foreignSite = _store.SeedSite(_store.SeedCustomer(Guid.NewGuid(), "CUST-1"), "SITE-1");

        var result = await _equipment.CreateAsync(Context(), foreignSite.Id, "EQ-1", CancellationToken.None);

        Assert.Equal(MasterDataFailure.NotFound, result.Error?.Failure);
    }

    [Fact]
    public async Task CreateEquipment_UnderInactiveSite_Returns422OnSiteId()
    {
        var site = _store.SeedSite(_store.SeedCustomer(_tenantId, "CUST-1"), "SITE-1", inactiveReason: "Closed");

        var result = await _equipment.CreateAsync(Context(), site.Id, "EQ-1", CancellationToken.None);

        Assert.Equal(MasterDataFailure.ValidationFailed, result.Error?.Failure);
        Assert.True(result.Error!.Errors.ContainsKey(MasterDataFields.SiteId));
    }

    [Fact]
    public async Task CreateEquipment_DuplicateWithinSite_Returns422()
    {
        var site = _store.SeedSite(_store.SeedCustomer(_tenantId, "CUST-1"), "SITE-1");
        _store.SeedEquipment(site, "EQ-1");

        var result = await _equipment.CreateAsync(Context(), site.Id, "EQ-1", CancellationToken.None);

        Assert.Equal(MasterDataFailure.ValidationFailed, result.Error?.Failure);
        Assert.True(result.Error!.Errors.ContainsKey(MasterDataFields.EquipmentCode));
    }

    [Fact]
    public async Task ActivateEquipment_UnderInactiveSite_Returns422()
    {
        var site = _store.SeedSite(_store.SeedCustomer(_tenantId, "CUST-1"), "SITE-1", inactiveReason: "Closed");
        var equipment = _store.SeedEquipment(site, "EQ-1", inactiveReason: "Broken");

        var result = await _equipment.ActivateAsync(Context(), equipment.Id, FakeMasterDataStore.InitialRowVersion, CancellationToken.None);

        Assert.Equal(MasterDataFailure.ValidationFailed, result.Error?.Failure);
        Assert.Equal(MasterDataStatus.Inactive, equipment.Status);
    }

    [Fact]
    public async Task DeactivateEquipment_HasNoDependencyGuard_AndRequiresReason()
    {
        var site = _store.SeedSite(_store.SeedCustomer(_tenantId, "CUST-1"), "SITE-1");
        var equipment = _store.SeedEquipment(site, "EQ-1");

        var withoutReason = await _equipment.DeactivateAsync(Context(), equipment.Id, FakeMasterDataStore.InitialRowVersion, " ", CancellationToken.None);
        var withReason = await _equipment.DeactivateAsync(Context(), equipment.Id, FakeMasterDataStore.InitialRowVersion, "Broken", CancellationToken.None);

        Assert.Equal(MasterDataFailure.ValidationFailed, withoutReason.Error?.Failure);
        Assert.True(withReason.Succeeded);
        Assert.Equal(MasterDataStatus.Inactive, equipment.Status);
        Assert.Equal("EQUIPMENT_DEACTIVATED", Assert.Single(_store.Audits).ActionCode);
    }
}
