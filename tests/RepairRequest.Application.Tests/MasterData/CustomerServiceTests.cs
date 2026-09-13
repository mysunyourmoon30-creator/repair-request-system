using RepairRequest.Application.Common;
using RepairRequest.Application.MasterData;
using RepairRequest.Domain.MasterData;

namespace RepairRequest.Application.Tests.MasterData;

/// <summary>Customer rules: DEC-PS1-001/013/014/015 and S1-004 decisions D1 (reason cleared on activate) and D3 (code-only update).</summary>
public class CustomerServiceTests
{
    private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 9, 13, 10, 0, 0, 123, TimeSpan.Zero).AddTicks(4567);

    private readonly FakeMasterDataStore _store = new();
    private readonly CustomerService _service;
    private readonly Guid _tenantId = Guid.NewGuid();

    public CustomerServiceTests()
    {
        _service = new CustomerService(_store, new FixedClock(Now));
    }

    private CommandContext Context() => FakeMasterDataStore.AdministratorContext(_tenantId);

    [Fact]
    public async Task Create_AddsActiveCustomerInCallerTenant_WithCreateAudit()
    {
        var context = Context();

        var result = await _service.CreateAsync(context, "  CUST-001 ", CancellationToken.None);

        Assert.True(result.Succeeded);
        var customer = Assert.Single(_store.Customers);
        Assert.Equal(_tenantId, customer.TenantId);
        Assert.Equal("CUST-001", customer.CustomerCode);
        Assert.Equal(MasterDataStatus.Active, customer.Status);

        var audit = Assert.Single(_store.Audits);
        Assert.Equal(MasterDataAudit.CustomerEntityType, audit.EntityType);
        Assert.Equal("CUSTOMER_CREATED", audit.ActionCode);
        Assert.Equal(customer.Id, audit.EntityId);
        Assert.Equal(_tenantId, audit.TenantId);
        Assert.Null(audit.FromState);
        Assert.Equal("ACTIVE", audit.ToState);
        Assert.Contains("CUST-001", audit.NewValueJson);
        Assert.Equal(context.User.UserId, audit.ActorId);
        Assert.Equal(context.CorrelationId, audit.CorrelationId);
        Assert.Equal(new DateTime(2026, 9, 13, 10, 0, 0, 123, DateTimeKind.Utc), audit.OccurredAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("    ")]
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZ012345")]
    [InlineData("CÜST")]
    [InlineData("A\tB")]
    public async Task Create_InvalidCode_Returns422WithoutWriting(string? code)
    {
        var result = await _service.CreateAsync(Context(), code, CancellationToken.None);

        Assert.Equal(CommandFailure.ValidationFailed, result.Error?.Failure);
        Assert.True(result.Error!.Errors.ContainsKey(MasterDataFields.CustomerCode));
        Assert.Equal(0, _store.SaveCount);
        Assert.Empty(_store.Audits);
    }

    [Fact]
    public async Task Create_DuplicateCodeWithinTenant_Returns422()
    {
        _store.SeedCustomer(_tenantId, "CUST-001");

        var result = await _service.CreateAsync(Context(), "cust-001", CancellationToken.None);

        Assert.Equal(CommandFailure.ValidationFailed, result.Error?.Failure);
        Assert.True(result.Error!.Errors.ContainsKey(MasterDataFields.CustomerCode));
        Assert.Equal(0, _store.SaveCount);
    }

    [Fact]
    public async Task Create_SameCodeInAnotherTenant_IsAllowed()
    {
        _store.SeedCustomer(Guid.NewGuid(), "CUST-001");

        var result = await _service.CreateAsync(Context(), "CUST-001", CancellationToken.None);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Create_UniqueViolationAtSave_Returns422()
    {
        _store.SaveOutcome = MasterDataSaveOutcome.DuplicateCode;

        var result = await _service.CreateAsync(Context(), "CUST-RACE", CancellationToken.None);

        Assert.Equal(CommandFailure.ValidationFailed, result.Error?.Failure);
        Assert.Empty(_store.Customers);
        Assert.Empty(_store.Audits);
    }

    [Fact]
    public async Task Update_OtherTenantCustomer_ReturnsNotFound()
    {
        var foreign = _store.SeedCustomer(Guid.NewGuid(), "CUST-001");

        var result = await _service.UpdateAsync(Context(), foreign.Id, FakeMasterDataStore.InitialRowVersion, "CUST-002", CancellationToken.None);

        Assert.Equal(CommandFailure.NotFound, result.Error?.Failure);
        Assert.Equal("CUST-001", foreign.CustomerCode);
    }

    [Fact]
    public async Task Update_StaleRowVersion_ReturnsConcurrencyConflictWithoutChange()
    {
        var customer = _store.SeedCustomer(_tenantId, "CUST-001");

        var result = await _service.UpdateAsync(Context(), customer.Id, [9, 9, 9, 9, 9, 9, 9, 9], "CUST-002", CancellationToken.None);

        Assert.Equal(CommandFailure.ConcurrencyConflict, result.Error?.Failure);
        Assert.Equal("CUST-001", customer.CustomerCode);
        Assert.Equal(0, _store.SaveCount);
    }

    [Fact]
    public async Task Update_ChangesCode_AuditsOldAndNewValue()
    {
        var customer = _store.SeedCustomer(_tenantId, "CUST-001");

        var result = await _service.UpdateAsync(Context(), customer.Id, FakeMasterDataStore.InitialRowVersion, "CUST-900", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("CUST-900", result.Value!.CustomerCode);
        Assert.Equal(FakeMasterDataStore.SavedRowVersion, result.Value.RowVersion);

        var audit = Assert.Single(_store.Audits);
        Assert.Equal("CUSTOMER_UPDATED", audit.ActionCode);
        Assert.Equal("{\"customerCode\":\"CUST-001\"}", audit.OldValueJson);
        Assert.Equal("{\"customerCode\":\"CUST-900\"}", audit.NewValueJson);
    }

    [Fact]
    public async Task Update_UnchangedCode_IsNoOpWithoutWriteOrAudit()
    {
        var customer = _store.SeedCustomer(_tenantId, "CUST-001");

        var result = await _service.UpdateAsync(Context(), customer.Id, FakeMasterDataStore.InitialRowVersion, " CUST-001 ", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(0, _store.SaveCount);
        Assert.Empty(_store.Audits);
    }

    [Fact]
    public async Task Update_CodeUsedByAnotherCustomer_Returns422()
    {
        _store.SeedCustomer(_tenantId, "CUST-TAKEN");
        var customer = _store.SeedCustomer(_tenantId, "CUST-001");

        var result = await _service.UpdateAsync(Context(), customer.Id, FakeMasterDataStore.InitialRowVersion, "CUST-TAKEN", CancellationToken.None);

        Assert.Equal(CommandFailure.ValidationFailed, result.Error?.Failure);
        Assert.Equal(0, _store.SaveCount);
    }

    [Fact]
    public async Task Update_CaseOnlyChangeOfOwnCode_IsAllowed()
    {
        var customer = _store.SeedCustomer(_tenantId, "cust-001");

        var result = await _service.UpdateAsync(Context(), customer.Id, FakeMasterDataStore.InitialRowVersion, "CUST-001", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("CUST-001", customer.CustomerCode);
    }

    [Fact]
    public async Task Update_ConcurrencyConflictAtSave_Returns409()
    {
        var customer = _store.SeedCustomer(_tenantId, "CUST-001");
        _store.SaveOutcome = MasterDataSaveOutcome.ConcurrencyConflict;

        var result = await _service.UpdateAsync(Context(), customer.Id, FakeMasterDataStore.InitialRowVersion, "CUST-002", CancellationToken.None);

        Assert.Equal(CommandFailure.ConcurrencyConflict, result.Error?.Failure);
        Assert.Empty(_store.Audits);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Deactivate_WithoutReason_Returns422AndStaysActive(string? reason)
    {
        var customer = _store.SeedCustomer(_tenantId, "CUST-001");

        var result = await _service.DeactivateAsync(Context(), customer.Id, FakeMasterDataStore.InitialRowVersion, reason, CancellationToken.None);

        Assert.Equal(CommandFailure.ValidationFailed, result.Error?.Failure);
        Assert.True(result.Error!.Errors.ContainsKey(MasterDataFields.Reason));
        Assert.Equal(MasterDataStatus.Active, customer.Status);
    }

    [Fact]
    public async Task Deactivate_ReasonLongerThanLimit_Returns422()
    {
        var customer = _store.SeedCustomer(_tenantId, "CUST-001");

        var result = await _service.DeactivateAsync(
            Context(), customer.Id, FakeMasterDataStore.InitialRowVersion, new string('r', MasterDataEntity.DeactivateReasonMaxLength + 1), CancellationToken.None);

        Assert.Equal(CommandFailure.ValidationFailed, result.Error?.Failure);
    }

    [Fact]
    public async Task Deactivate_WithActiveSite_Returns409WithActiveChildCount()
    {
        var customer = _store.SeedCustomer(_tenantId, "CUST-001");
        _store.SeedSite(customer, "SITE-1");
        _store.SeedSite(customer, "SITE-2", inactiveReason: "Closed");

        var result = await _service.DeactivateAsync(Context(), customer.Id, FakeMasterDataStore.InitialRowVersion, "Contract ended", CancellationToken.None);

        Assert.Equal(CommandFailure.StateConflict, result.Error?.Failure);
        Assert.Equal(1, result.Error!.ActiveChildCount);
        Assert.Equal(MasterDataStatus.Active, customer.Status);
        Assert.Equal(1, _store.SerializableRuns);
        Assert.Equal(0, _store.SaveCount);
    }

    [Fact]
    public async Task Deactivate_WithOnlyInactiveSites_Succeeds_AuditsReason_AndDoesNotTouchSites()
    {
        var customer = _store.SeedCustomer(_tenantId, "CUST-001");
        var site = _store.SeedSite(customer, "SITE-1", inactiveReason: "Closed");

        var result = await _service.DeactivateAsync(Context(), customer.Id, FakeMasterDataStore.InitialRowVersion, " Contract ended ", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(MasterDataStatus.Inactive, customer.Status);
        Assert.Equal("Contract ended", customer.DeactivateReason);
        Assert.Equal("Closed", site.DeactivateReason);

        var audit = Assert.Single(_store.Audits);
        Assert.Equal("CUSTOMER_DEACTIVATED", audit.ActionCode);
        Assert.Equal("ACTIVE", audit.FromState);
        Assert.Equal("INACTIVE", audit.ToState);
        Assert.Equal("Contract ended", audit.Reason);
    }

    [Fact]
    public async Task Deactivate_AlreadyInactive_Returns409()
    {
        var customer = _store.SeedCustomer(_tenantId, "CUST-001", inactiveReason: "First");

        var result = await _service.DeactivateAsync(Context(), customer.Id, FakeMasterDataStore.InitialRowVersion, "Second", CancellationToken.None);

        Assert.Equal(CommandFailure.StateConflict, result.Error?.Failure);
        Assert.Equal("First", customer.DeactivateReason);
    }

    [Fact]
    public async Task Activate_Inactive_ClearsReason_AndAuditsPreviousReason()
    {
        var customer = _store.SeedCustomer(_tenantId, "CUST-001", inactiveReason: "Contract ended");

        var result = await _service.ActivateAsync(Context(), customer.Id, FakeMasterDataStore.InitialRowVersion, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(MasterDataStatus.Active, customer.Status);
        Assert.Null(result.Value!.DeactivateReason);

        var audit = Assert.Single(_store.Audits);
        Assert.Equal("CUSTOMER_ACTIVATED", audit.ActionCode);
        Assert.Equal("INACTIVE", audit.FromState);
        Assert.Equal("ACTIVE", audit.ToState);
        Assert.Contains("Contract ended", audit.OldValueJson);
    }

    [Fact]
    public async Task Activate_AlreadyActive_Returns409()
    {
        var customer = _store.SeedCustomer(_tenantId, "CUST-001");

        var result = await _service.ActivateAsync(Context(), customer.Id, FakeMasterDataStore.InitialRowVersion, CancellationToken.None);

        Assert.Equal(CommandFailure.StateConflict, result.Error?.Failure);
        Assert.Empty(_store.Audits);
    }
}
