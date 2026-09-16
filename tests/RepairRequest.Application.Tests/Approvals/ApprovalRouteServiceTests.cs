using RepairRequest.Application.Approvals;
using RepairRequest.Application.Common;
using RepairRequest.Application.MasterData;
using RepairRequest.Application.RepairRequests;
using RepairRequest.Application.Security;
using RepairRequest.Application.Tests.MasterData;
using RepairRequest.Domain.Approvals;
using RepairRequest.Domain.Auditing;
using RepairRequest.Domain.MasterData;
using RepairRequest.Domain.Security;

namespace RepairRequest.Application.Tests.Approvals;

/// <summary>Approval route configuration rules (DEC-PRE-S1-007R-04/05/06).</summary>
public class ApprovalRouteServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 10, 0, 0, TimeSpan.Zero);

    private readonly FakeApprovalRouteStore _store = new();
    private readonly ApprovalRouteService _service;
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _siteId = Guid.NewGuid();
    private readonly Guid _approverId = Guid.NewGuid();

    public ApprovalRouteServiceTests()
    {
        _service = new ApprovalRouteService(_store, new FixedClock(Now));
        _store.Categories["ELECTRICAL"] = MasterDataStatus.Active;
        _store.Categories["PLUMBING"] = MasterDataStatus.Inactive;
        _store.Sites[_siteId] = MasterDataStatus.Active;
        _store.ApproverUsers.Add(_approverId);
    }

    private CommandContext Admin(Guid? tenantId = null) =>
        new(new CurrentUser(Guid.NewGuid(), tenantId ?? _tenantId, [RoleCodes.Administrator]), Guid.NewGuid());

    private Task<CommandResult<ApprovalRouteDto>> CreateAsync(string? category = "ELECTRICAL", Guid? siteId = null, string? role = RoleCodes.Approver, Guid? approverUserId = null, CommandContext? context = null) =>
        _service.CreateAsync(context ?? Admin(), new ApprovalRouteFields(category, siteId, role, approverUserId), CancellationToken.None);

    // ---------------- Create ----------------

    [Fact]
    public async Task Create_ValidSiteRoute_IsActive_WithOneApproverStep_CanonicalCategory_AndAudit()
    {
        var result = await CreateAsync(" electrical ", _siteId, RoleCodes.Approver, _approverId);

        Assert.True(result.Succeeded);
        var dto = result.Value!;
        Assert.Equal("ELECTRICAL", dto.RequestCategoryCode);
        Assert.Equal(_siteId, dto.SiteId);
        Assert.Equal(MasterDataStatus.Active, dto.Status);
        Assert.Equal((short)1, dto.StepNo);
        Assert.Equal(RoleCodes.Approver, dto.ApproverRoleCode);
        Assert.Equal(_approverId, dto.ApproverUserId);

        var route = Assert.Single(_store.Routes);
        Assert.Equal(_tenantId, route.TenantId);
        Assert.Single(_store.Steps);
        var audit = Assert.Single(_store.Audits);
        Assert.Equal(ApprovalRouteAudit.CreatedAction, audit.ActionCode);
        Assert.Equal("ACTIVE", audit.ToState);
    }

    [Fact]
    public async Task Create_RoleOnlyTenantDefaultRoute_IsAllowed()
    {
        var result = await CreateAsync();

        Assert.True(result.Succeeded);
        Assert.Null(result.Value!.SiteId);
        Assert.Null(result.Value.ApproverUserId);
    }

    public static TheoryData<string?, Guid?, string?, Guid?, string> InvalidBodies => new()
    {
        { null, null, RoleCodes.Approver, null, ApprovalRouteFieldNames.RequestCategoryCode },
        { "ELECTRICAL", null, null, null, ApprovalRouteFieldNames.ApproverRoleCode },
        { "ELECTRICAL", null, RoleCodes.Coordinator, null, ApprovalRouteFieldNames.ApproverRoleCode },
        { "ELECTRICAL", null, RoleCodes.Administrator, null, ApprovalRouteFieldNames.ApproverRoleCode },
        { "ELECTRICAL", Guid.Empty, RoleCodes.Approver, null, ApprovalRouteFieldNames.SiteId },
        { "ELECTRICAL", null, RoleCodes.Approver, Guid.Empty, ApprovalRouteFieldNames.ApproverUserId }
    };

    [Theory]
    [MemberData(nameof(InvalidBodies))]
    public async Task Create_InvalidBody_Returns422_OnTheField_AndWritesNothing(string? category, Guid? siteId, string? role, Guid? approverUserId, string field)
    {
        var result = await CreateAsync(category, siteId, role, approverUserId);

        Assert.Equal(CommandFailure.ValidationFailed, result.Error?.Failure);
        Assert.True(result.Error!.Errors.ContainsKey(field), field);
        Assert.Empty(_store.Routes);
        Assert.Empty(_store.Audits);
    }

    [Fact]
    public async Task Create_UnknownOrInactiveReferences_Return422()
    {
        var unknownCategory = await CreateAsync("NOT-A-CATEGORY");
        var inactiveCategory = await CreateAsync("PLUMBING");
        var unknownSite = await CreateAsync(siteId: Guid.NewGuid());
        _store.Sites[_siteId] = MasterDataStatus.Inactive;
        var inactiveSite = await CreateAsync(siteId: _siteId);
        var notApprover = await CreateAsync(approverUserId: Guid.NewGuid());

        Assert.Equal(["The Category is not valid."], unknownCategory.Error!.Errors[ApprovalRouteFieldNames.RequestCategoryCode]);
        Assert.Equal(["The Category is inactive."], inactiveCategory.Error!.Errors[ApprovalRouteFieldNames.RequestCategoryCode]);
        Assert.Equal(["The Site is not valid."], unknownSite.Error!.Errors[ApprovalRouteFieldNames.SiteId]);
        Assert.Equal(["The Site is inactive."], inactiveSite.Error!.Errors[ApprovalRouteFieldNames.SiteId]);
        Assert.True(notApprover.Error!.Errors.ContainsKey(ApprovalRouteFieldNames.ApproverUserId));
        Assert.Empty(_store.Routes);
    }

    [Fact]
    public async Task Create_SecondActiveRouteForTheSameKey_Returns422_ButSiteAndDefaultKeysAreIndependent()
    {
        Assert.True((await CreateAsync(siteId: _siteId)).Succeeded);
        Assert.True((await CreateAsync()).Succeeded);

        var duplicateSite = await CreateAsync(siteId: _siteId);
        var duplicateDefault = await CreateAsync();

        Assert.Equal(CommandFailure.ValidationFailed, duplicateSite.Error?.Failure);
        Assert.Equal(CommandFailure.ValidationFailed, duplicateDefault.Error?.Failure);
        Assert.Equal(2, _store.Routes.Count);
    }

    [Fact]
    public async Task Create_WhenTheDatabaseRejectsASecondActiveRoute_Returns422()
    {
        _store.SaveOutcome = ApprovalRouteSaveOutcome.DuplicateActiveRoute;

        var result = await CreateAsync();

        Assert.Equal(CommandFailure.ValidationFailed, result.Error?.Failure);
        Assert.Empty(_store.Routes);
    }

    // ---------------- Activate / Deactivate ----------------

    [Fact]
    public async Task Deactivate_ThenActivate_TogglesStatus_WithAudit_AndInactiveHistoryCoexists()
    {
        var created = (await CreateAsync()).Value!;

        var deactivated = await _service.DeactivateAsync(Admin(), created.Id, created.RowVersion, CancellationToken.None);
        Assert.Equal(MasterDataStatus.Inactive, deactivated.Value!.Status);

        var replacement = await CreateAsync();
        Assert.True(replacement.Succeeded);

        var reactivate = await _service.ActivateAsync(Admin(), created.Id, deactivated.Value.RowVersion, CancellationToken.None);
        Assert.Equal(CommandFailure.ValidationFailed, reactivate.Error?.Failure);

        await _service.DeactivateAsync(Admin(), replacement.Value!.Id, replacement.Value.RowVersion, CancellationToken.None);
        var activated = await _service.ActivateAsync(Admin(), created.Id, deactivated.Value.RowVersion, CancellationToken.None);
        Assert.Equal(MasterDataStatus.Active, activated.Value!.Status);

        Assert.Contains(_store.Audits, audit => audit.ActionCode == ApprovalRouteAudit.DeactivatedAction);
        Assert.Contains(_store.Audits, audit => audit.ActionCode == ApprovalRouteAudit.ActivatedAction);
    }

    [Fact]
    public async Task ActivateAndDeactivate_GuardStateAndRowVersion()
    {
        var created = (await CreateAsync()).Value!;

        var alreadyActive = await _service.ActivateAsync(Admin(), created.Id, created.RowVersion, CancellationToken.None);
        var stale = await _service.DeactivateAsync(Admin(), created.Id, [9, 9, 9, 9, 9, 9, 9, 9], CancellationToken.None);
        var deactivated = await _service.DeactivateAsync(Admin(), created.Id, created.RowVersion, CancellationToken.None);
        var alreadyInactive = await _service.DeactivateAsync(Admin(), created.Id, deactivated.Value!.RowVersion, CancellationToken.None);

        Assert.Equal(CommandFailure.StateConflict, alreadyActive.Error?.Failure);
        Assert.Equal(CommandFailure.ConcurrencyConflict, stale.Error?.Failure);
        Assert.Equal(CommandFailure.StateConflict, alreadyInactive.Error?.Failure);
    }

    [Fact]
    public async Task Activate_RevalidatesReferences()
    {
        var created = (await CreateAsync(approverUserId: _approverId)).Value!;
        var deactivated = (await _service.DeactivateAsync(Admin(), created.Id, created.RowVersion, CancellationToken.None)).Value!;
        _store.ApproverUsers.Remove(_approverId);

        var result = await _service.ActivateAsync(Admin(), created.Id, deactivated.RowVersion, CancellationToken.None);

        Assert.True(result.Error!.Errors.ContainsKey(ApprovalRouteFieldNames.ApproverUserId));
    }

    [Fact]
    public async Task OtherTenantOrNonAdministrator_GetsNotFound()
    {
        var created = (await CreateAsync()).Value!;
        var businessUser = new CommandContext(new CurrentUser(Guid.NewGuid(), _tenantId, [RoleCodes.Approver]), Guid.NewGuid());

        var otherTenant = await _service.DeactivateAsync(Admin(Guid.NewGuid()), created.Id, created.RowVersion, CancellationToken.None);
        var nonAdmin = await _service.DeactivateAsync(businessUser, created.Id, created.RowVersion, CancellationToken.None);

        Assert.Equal(CommandFailure.NotFound, otherTenant.Error?.Failure);
        Assert.Equal(CommandFailure.NotFound, nonAdmin.Error?.Failure);
    }
}

/// <summary>In-memory approval route port; the filtered unique indexes are covered by integration tests.</summary>
internal sealed class FakeApprovalRouteStore : IApprovalRouteStore
{
    private readonly List<ApprovalRoute> _pendingRoutes = [];
    private readonly List<ApprovalRouteStep> _pendingSteps = [];
    private readonly List<AuditHistory> _pendingAudits = [];
    private long _rowVersion = 1;

    public List<ApprovalRoute> Routes { get; } = [];

    public List<ApprovalRouteStep> Steps { get; } = [];

    public List<AuditHistory> Audits { get; } = [];

    public Dictionary<string, MasterDataStatus> Categories { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<Guid, MasterDataStatus> Sites { get; } = new();

    public HashSet<Guid> ApproverUsers { get; } = [];

    public ApprovalRouteSaveOutcome SaveOutcome { get; set; } = ApprovalRouteSaveOutcome.Saved;

    public Task<PagedResult<ApprovalRouteDto>> ListAsync(CurrentUser user, MasterDataListQuery query, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<ApprovalRouteDto?> GetAsync(CurrentUser user, Guid approvalRouteId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<ApprovalRoute?> FindAsync(CurrentUser user, Guid approvalRouteId, CancellationToken cancellationToken) =>
        Task.FromResult(user.HasTenantWideConfigurationScope
            ? Routes.SingleOrDefault(route => route.Id == approvalRouteId && route.TenantId == user.TenantId)
            : null);

    public Task<ApprovalRouteStep?> FindFirstStepAsync(Guid tenantId, Guid approvalRouteId, CancellationToken cancellationToken) =>
        Task.FromResult(Steps.SingleOrDefault(step => step.TenantId == tenantId && step.ApprovalRouteId == approvalRouteId));

    public Task<ApprovalRouteReferences> GetReferencesAsync(
        CurrentUser user,
        string requestCategoryCode,
        Guid? siteId,
        Guid? approverUserId,
        CancellationToken cancellationToken)
    {
        var category = Categories.FirstOrDefault(pair => string.Equals(pair.Key, requestCategoryCode, StringComparison.OrdinalIgnoreCase));
        MasterDataStatus? siteStatus = siteId is { } site && Sites.TryGetValue(site, out var status) ? status : null;

        return Task.FromResult(new ApprovalRouteReferences(
            category.Key is null ? null : new LookupSelection(category.Key, category.Value),
            siteStatus,
            approverUserId is { } approver && ApproverUsers.Contains(approver)));
    }

    public Task<bool> OtherActiveRouteExistsAsync(
        Guid tenantId,
        string requestCategoryCode,
        Guid? siteId,
        Guid excludeRouteId,
        CancellationToken cancellationToken) =>
        Task.FromResult(Routes.Any(route =>
            route.TenantId == tenantId
            && route.RequestCategoryCode == requestCategoryCode
            && route.IsActive
            && route.Id != excludeRouteId
            && route.SiteId == siteId));

    public void Add(ApprovalRoute route)
    {
        FakeApprovalRoutingStore.Set(route, nameof(ApprovalRoute.Id), Guid.NewGuid());
        _pendingRoutes.Add(route);
    }

    public void AddStep(ApprovalRouteStep step) => _pendingSteps.Add(step);

    public void AddAudit(AuditHistory audit) => _pendingAudits.Add(audit);

    public Task<ApprovalRouteSaveOutcome> SaveChangesAsync(ApprovalRoute route, byte[]? expectedRowVersion, CancellationToken cancellationToken)
    {
        var outcome = SaveOutcome;
        if (outcome == ApprovalRouteSaveOutcome.Saved && expectedRowVersion is not null && !route.RowVersion.AsSpan().SequenceEqual(expectedRowVersion))
        {
            outcome = ApprovalRouteSaveOutcome.ConcurrencyConflict;
        }

        if (outcome == ApprovalRouteSaveOutcome.Saved)
        {
            Routes.AddRange(_pendingRoutes);
            Steps.AddRange(_pendingSteps);
            Audits.AddRange(_pendingAudits);
            FakeApprovalRoutingStore.Set(route, nameof(ApprovalRoute.RowVersion), BitConverter.GetBytes(++_rowVersion));
        }

        _pendingRoutes.Clear();
        _pendingSteps.Clear();
        _pendingAudits.Clear();
        return Task.FromResult(outcome);
    }
}
