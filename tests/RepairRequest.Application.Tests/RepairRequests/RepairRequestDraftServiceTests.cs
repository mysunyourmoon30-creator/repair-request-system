using RepairRequest.Application.Common;
using RepairRequest.Application.RepairRequests;
using RepairRequest.Application.Tests.MasterData;
using RepairRequest.Domain.MasterData;
using RepairRequest.Domain.RepairRequests;

namespace RepairRequest.Application.Tests.RepairRequests;

/// <summary>
/// UC-RR-001 Draft rules: owner/tenant from the caller (D-11), optional Draft fields (RR-DD-001 Y@Submit), Site scope
/// and active masters, Equipment belongs to Site, owner-only DRAFT edit with row version, whole-Draft revalidation
/// (decision E2) and create/edit audit.
/// </summary>
public class RepairRequestDraftServiceTests
{
    private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 9, 14, 10, 0, 0, 456, TimeSpan.Zero).AddTicks(789);

    private readonly FakeRepairRequestDraftStore _store = new();
    private readonly RepairRequestDraftService _service;
    private readonly Guid _tenantId = Guid.NewGuid();

    public RepairRequestDraftServiceTests()
    {
        _service = new RepairRequestDraftService(_store, new FixedClock(Now));
    }

    private CommandContext Requester(Guid? userId = null) => FakeRepairRequestDraftStore.RequesterContext(_tenantId, userId);

    private static RepairRequestDraftFields Fields(
        Guid? siteId = null,
        Guid? equipmentId = null,
        string? description = null,
        DateTimeOffset? start = null,
        DateTimeOffset? end = null) =>
        new(siteId, equipmentId, description, start, end);

    // ---------------- Create ----------------

    [Fact]
    public async Task Create_EmptyDraft_IsOwnedByCallerInCallerTenant_WithoutSubmitDataOrSelectionQuery()
    {
        var context = Requester();

        var result = await _service.CreateAsync(context, Fields(), CancellationToken.None);

        Assert.True(result.Succeeded);
        var draft = Assert.Single(_store.Requests);
        Assert.Equal(_tenantId, draft.TenantId);
        Assert.Equal(context.User.UserId, draft.CreatedBy);
        Assert.Equal(RepairRequestStatus.Draft, draft.Status);
        Assert.Null(draft.RequestNo);
        Assert.Null(draft.SiteId);
        Assert.Equal(0, _store.SelectionQueries);

        var audit = Assert.Single(_store.Audits);
        Assert.Equal(RepairRequestAudit.EntityType, audit.EntityType);
        Assert.Equal(RepairRequestAudit.DraftCreatedAction, audit.ActionCode);
        Assert.Equal(draft.Id, audit.EntityId);
        Assert.Null(audit.FromState);
        Assert.Equal("DRAFT", audit.ToState);
        Assert.Equal("{}", audit.NewValueJson);
        Assert.Equal(context.User.UserId, audit.ActorId);
        Assert.Equal(context.CorrelationId, audit.CorrelationId);
        Assert.Equal(new DateTime(2026, 9, 14, 10, 0, 0, 456, DateTimeKind.Utc), audit.OccurredAt);
    }

    [Fact]
    public async Task Create_WithAllDraftFields_TrimsDescription_StoresUtcMilliseconds_AndAuditsValues()
    {
        var siteId = _store.AddSite();
        var equipmentId = _store.AddEquipment(siteId);
        var start = new DateTimeOffset(2026, 9, 20, 15, 0, 0, TimeSpan.FromHours(7)).AddTicks(1234);

        var result = await _service.CreateAsync(
            Requester(), Fields(siteId, equipmentId, "  Pump leaking  ", start, start.AddHours(2)), CancellationToken.None);

        Assert.True(result.Succeeded);
        var dto = result.Value!;
        Assert.Equal(siteId, dto.SiteId);
        Assert.Equal(equipmentId, dto.EquipmentId);
        Assert.Equal("Pump leaking", dto.Description);
        Assert.Equal(new DateTime(2026, 9, 20, 8, 0, 0, DateTimeKind.Utc), dto.PreferredStartAt);
        Assert.Equal(DateTimeKind.Utc, dto.PreferredStartAt!.Value.Kind);
        Assert.Equal(1, _store.SelectionQueries);
        Assert.Contains("Pump leaking", Assert.Single(_store.Audits).NewValueJson);
    }

    [Fact]
    public async Task Create_SiteOutsideScope_ReturnsNotFoundWithoutWriting()
    {
        var result = await _service.CreateAsync(Requester(), Fields(Guid.NewGuid()), CancellationToken.None);

        Assert.Equal(CommandFailure.NotFound, result.Error?.Failure);
        Assert.Equal(0, _store.SaveCount);
        Assert.Empty(_store.Audits);
    }

    [Fact]
    public async Task Create_InactiveSite_Returns422OnSiteId()
    {
        var siteId = _store.AddSite(MasterDataStatus.Inactive);

        var result = await _service.CreateAsync(Requester(), Fields(siteId), CancellationToken.None);

        Assert.Equal(CommandFailure.ValidationFailed, result.Error?.Failure);
        Assert.True(result.Error!.Errors.ContainsKey(RepairRequestFields.SiteId));
        Assert.Empty(_store.Requests);
    }

    [Fact]
    public async Task Create_EquipmentOfAnotherSite_OrUnknown_Returns422OnEquipmentId()
    {
        var siteA = _store.AddSite();
        var siteB = _store.AddSite();
        var equipmentOfB = _store.AddEquipment(siteB);

        var otherSite = await _service.CreateAsync(Requester(), Fields(siteA, equipmentOfB), CancellationToken.None);
        var unknown = await _service.CreateAsync(Requester(), Fields(siteA, Guid.NewGuid()), CancellationToken.None);

        Assert.True(otherSite.Error!.Errors.ContainsKey(RepairRequestFields.EquipmentId));
        Assert.Equal(otherSite.Error.Errors[RepairRequestFields.EquipmentId], unknown.Error!.Errors[RepairRequestFields.EquipmentId]);
        Assert.Empty(_store.Requests);
    }

    [Fact]
    public async Task Create_InactiveEquipment_Returns422()
    {
        var siteId = _store.AddSite();
        var equipmentId = _store.AddEquipment(siteId, MasterDataStatus.Inactive);

        var result = await _service.CreateAsync(Requester(), Fields(siteId, equipmentId), CancellationToken.None);

        Assert.Equal(new[] { "The Equipment is inactive." }, result.Error!.Errors[RepairRequestFields.EquipmentId]);
    }

    [Fact]
    public async Task Create_FieldErrors_AreReportedBeforeAnyScopeQuery()
    {
        var start = DateTimeOffset.UtcNow;

        var result = await _service.CreateAsync(
            Requester(),
            Fields(null, Guid.NewGuid(), new string('d', 2001), start, start.AddMinutes(-5)),
            CancellationToken.None);

        Assert.Equal(CommandFailure.ValidationFailed, result.Error?.Failure);
        Assert.True(result.Error!.Errors.ContainsKey(RepairRequestFields.EquipmentId));
        Assert.True(result.Error.Errors.ContainsKey(RepairRequestFields.Description));
        Assert.True(result.Error.Errors.ContainsKey(RepairRequestFields.PreferredEndAt));
        Assert.Equal(0, _store.SelectionQueries);
    }

    [Fact]
    public async Task Create_SaveConflict_Returns409WithoutAudit()
    {
        _store.SaveOutcome = RepairRequestSaveOutcome.ConcurrencyConflict;

        var result = await _service.CreateAsync(Requester(), Fields(), CancellationToken.None);

        Assert.Equal(CommandFailure.ConcurrencyConflict, result.Error?.Failure);
        Assert.Empty(_store.Audits);
    }

    // ---------------- Edit ----------------

    [Fact]
    public async Task Update_ByAnotherUser_ReturnsNotFound()
    {
        var owner = Requester();
        var draft = _store.SeedDraft(owner, description: "Mine");

        var result = await _service.UpdateAsync(Requester(), draft.Id, FakeRepairRequestDraftStore.InitialRowVersion, Fields(description: "Hijack"), CancellationToken.None);

        Assert.Equal(CommandFailure.NotFound, result.Error?.Failure);
        Assert.Equal("Mine", draft.Description);
    }

    [Fact]
    public async Task Update_StaleRowVersion_Returns409WithoutChange()
    {
        var owner = Requester();
        var draft = _store.SeedDraft(owner, description: "Before");

        var result = await _service.UpdateAsync(owner, draft.Id, [9, 9, 9, 9, 9, 9, 9, 9], Fields(description: "After"), CancellationToken.None);

        Assert.Equal(CommandFailure.ConcurrencyConflict, result.Error?.Failure);
        Assert.Equal("Before", draft.Description);
        Assert.Equal(0, _store.SaveCount);
    }

    [Theory]
    [InlineData(RepairRequestStatus.Submitted)]
    [InlineData(RepairRequestStatus.Approved)]
    [InlineData(RepairRequestStatus.Cancelled)]
    public async Task Update_WhenNoLongerDraft_Returns409StateConflict(RepairRequestStatus status)
    {
        var owner = Requester();
        var draft = _store.SeedDraft(owner, description: "Before");
        FakeRepairRequestDraftStore.ForceStatus(draft, status);

        var result = await _service.UpdateAsync(owner, draft.Id, FakeRepairRequestDraftStore.InitialRowVersion, Fields(description: "After"), CancellationToken.None);

        Assert.Equal(CommandFailure.StateConflict, result.Error?.Failure);
        Assert.Equal("Before", draft.Description);
        Assert.Empty(_store.Audits);
    }

    [Fact]
    public async Task Update_ChangesAndClearsFields_AuditsOnlyChangedValues()
    {
        var owner = Requester();
        var siteId = _store.AddSite();
        var draft = _store.SeedDraft(owner, siteId, description: "Before");

        var result = await _service.UpdateAsync(owner, draft.Id, FakeRepairRequestDraftStore.InitialRowVersion, Fields(siteId, description: "After"), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(FakeRepairRequestDraftStore.SavedRowVersion, result.Value!.RowVersion);
        var audit = Assert.Single(_store.Audits);
        Assert.Equal(RepairRequestAudit.DraftUpdatedAction, audit.ActionCode);
        Assert.Null(audit.FromState);
        Assert.Null(audit.ToState);
        Assert.Equal("{\"description\":\"Before\"}", audit.OldValueJson);
        Assert.Equal("{\"description\":\"After\"}", audit.NewValueJson);

        // An omitted field is saved as empty (the edit carries the complete Draft field set).
        var cleared = await _service.UpdateAsync(owner, draft.Id, FakeRepairRequestDraftStore.SavedRowVersion, Fields(), CancellationToken.None);

        Assert.True(cleared.Succeeded);
        Assert.Null(draft.SiteId);
        Assert.Null(draft.Description);
    }

    [Fact]
    public async Task Update_WithoutChanges_WritesNothingAndAuditsNothing()
    {
        var owner = Requester();
        var draft = _store.SeedDraft(owner, description: "Same");

        var result = await _service.UpdateAsync(owner, draft.Id, FakeRepairRequestDraftStore.InitialRowVersion, Fields(description: " Same "), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(0, _store.SaveCount);
        Assert.Empty(_store.Audits);
    }

    [Fact]
    public async Task Update_UnchangedSiteThatBecameInactive_Returns422_Decision_E2()
    {
        var owner = Requester();
        var siteId = _store.AddSite();
        var draft = _store.SeedDraft(owner, siteId);
        _store.SitesInScope[siteId] = MasterDataStatus.Inactive;

        var result = await _service.UpdateAsync(owner, draft.Id, FakeRepairRequestDraftStore.InitialRowVersion, Fields(siteId, description: "Only text changed"), CancellationToken.None);

        Assert.True(result.Error!.Errors.ContainsKey(RepairRequestFields.SiteId));
        Assert.Null(draft.Description);
    }

    [Fact]
    public async Task Update_ChangingSiteButKeepingEquipmentOfOldSite_Returns422()
    {
        var owner = Requester();
        var siteA = _store.AddSite();
        var siteB = _store.AddSite();
        var equipmentOfA = _store.AddEquipment(siteA);
        var draft = _store.SeedDraft(owner, siteA, equipmentOfA);

        var result = await _service.UpdateAsync(owner, draft.Id, FakeRepairRequestDraftStore.InitialRowVersion, Fields(siteB, equipmentOfA), CancellationToken.None);

        Assert.True(result.Error!.Errors.ContainsKey(RepairRequestFields.EquipmentId));
        Assert.Equal(siteA, draft.SiteId);
    }

    [Fact]
    public async Task Update_SaveConflict_Returns409()
    {
        var owner = Requester();
        var draft = _store.SeedDraft(owner);
        _store.SaveOutcome = RepairRequestSaveOutcome.ConcurrencyConflict;

        var result = await _service.UpdateAsync(owner, draft.Id, FakeRepairRequestDraftStore.InitialRowVersion, Fields(description: "Changed"), CancellationToken.None);

        Assert.Equal(CommandFailure.ConcurrencyConflict, result.Error?.Failure);
        Assert.Empty(_store.Audits);
    }
}
