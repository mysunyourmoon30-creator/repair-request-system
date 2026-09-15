using RepairRequest.Application.Common;
using RepairRequest.Application.RepairRequests;
using RepairRequest.Application.Tests.MasterData;
using RepairRequest.Domain.MasterData;
using RepairRequest.Domain.RepairRequests;
using RepairRequestAggregate = RepairRequest.Domain.RepairRequests.RepairRequest;

namespace RepairRequest.Application.Tests.RepairRequests;

/// <summary>
/// UC-RR-002 / ST-RR-002 Submit rules: deterministic validation order, Category/Priority lookups, contact eligibility,
/// CLEAN-photo evidence, BR-14 duplicate warning with continuation reason, Request No per tenant-year, SLA start marker
/// and success-only audit (DEC-PRE-S1-007-01..12).
/// </summary>
public class RepairRequestSubmitServiceTests
{
    private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 9, 14, 10, 0, 0, 456, TimeSpan.Zero).AddTicks(789);
    private static readonly DateTime NowUtc = new(2026, 9, 14, 10, 0, 0, 456, DateTimeKind.Utc);
    private static readonly DateTime Start = new(2026, 9, 20, 8, 0, 0, DateTimeKind.Utc);

    private readonly FakeRepairRequestDraftStore _drafts = new();
    private readonly FakeRepairRequestSubmitStore _submits = new();
    private readonly RepairRequestSubmitService _service;
    private readonly Guid _tenantId = Guid.NewGuid();

    public RepairRequestSubmitServiceTests()
    {
        _service = new RepairRequestSubmitService(_drafts, _submits, new FixedClock(Now));
        _drafts.Categories["ELECTRICAL"] = MasterDataStatus.Active;
        _drafts.Priorities["HIGH"] = MasterDataStatus.Active;
    }

    private CommandContext Requester(Guid? userId = null) => FakeRepairRequestDraftStore.RequesterContext(_tenantId, userId);

    private RepairRequestAggregate CompleteDraft(CommandContext owner, Guid? equipmentId = null, bool cleanPhoto = true)
    {
        var siteId = _drafts.AddSite();
        if (equipmentId is not null)
        {
            _drafts.Equipment[equipmentId.Value] = (siteId, MasterDataStatus.Active);
        }

        var contactId = _drafts.AddEligibleContact(siteId);
        var draft = _drafts.SeedDraft(owner, siteId, equipmentId, "Pump leaking", "ELECTRICAL", "HIGH", contactId, Start, Start.AddHours(2));
        if (cleanPhoto)
        {
            _submits.RequestsWithCleanPhoto.Add(draft.Id);
        }

        return draft;
    }

    private Task<CommandResult<RepairRequestDraftDto>> SubmitAsync(CommandContext caller, RepairRequestAggregate draft, string? reason = null, byte[]? rowVersion = null) =>
        _service.SubmitAsync(caller, draft.Id, rowVersion ?? FakeRepairRequestDraftStore.InitialRowVersion, reason, CancellationToken.None);

    private void AssertNotSubmitted(RepairRequestAggregate draft)
    {
        Assert.Equal(RepairRequestStatus.Draft, draft.Status);
        Assert.Null(draft.RequestNo);
        Assert.Null(draft.SubmittedAt);
        Assert.Null(draft.SubmittedBy);
        Assert.Empty(_submits.Audits);
        Assert.Empty(_submits.Counters);
    }

    // ---------------- Success ----------------

    [Fact]
    public async Task Submit_ValidDraft_GeneratesRequestNo_SetsSubmittedAndSlaStartMarker_AndAuditsOnce()
    {
        var owner = Requester();
        var draft = CompleteDraft(owner);

        var result = await SubmitAsync(owner, draft);

        Assert.True(result.Succeeded);
        var dto = result.Value!;
        Assert.Equal(RepairRequestStatus.Submitted, dto.Status);
        Assert.Equal("RR-2026-000001", dto.RequestNo);
        Assert.Equal(NowUtc, dto.SubmittedAt);
        Assert.Equal(owner.User.UserId, draft.SubmittedBy);
        Assert.Null(draft.DuplicateContinuationReason);
        Assert.Equal(FakeRepairRequestDraftStore.SavedRowVersion, dto.RowVersion);

        var audit = Assert.Single(_submits.Audits);
        Assert.Equal(RepairRequestAudit.SubmittedAction, audit.ActionCode);
        Assert.Equal("DRAFT", audit.FromState);
        Assert.Equal("SUBMITTED", audit.ToState);
        Assert.Null(audit.Reason);
        Assert.Contains("\"requestNo\":\"RR-2026-000001\"", audit.NewValueJson);
        Assert.DoesNotContain("duplicateCount", audit.NewValueJson);
        Assert.Equal(owner.CorrelationId, audit.CorrelationId);

        var duplicateQuery = Assert.Single(_submits.DuplicateQueries);
        Assert.Equal(draft.SiteId, duplicateQuery.SiteId);
        Assert.Equal("ELECTRICAL", duplicateQuery.RequestCategoryCode);
        Assert.Null(duplicateQuery.EquipmentId);
        Assert.Equal(draft.Id, duplicateQuery.RepairRequestId);
        Assert.Equal(NowUtc.AddHours(-24), duplicateQuery.WindowStart);
    }

    [Fact]
    public async Task Submit_SequencesArePerTenantAndYear()
    {
        var owner = Requester();
        var first = CompleteDraft(owner);
        var second = CompleteDraft(owner);
        var otherTenantService = new RepairRequestSubmitService(_drafts, _submits, new FixedClock(Now));
        var otherTenantOwner = FakeRepairRequestDraftStore.RequesterContext(Guid.NewGuid());
        var otherTenantDraft = CompleteDraft(otherTenantOwner);

        await SubmitAsync(owner, first);
        await SubmitAsync(owner, second);
        await otherTenantService.SubmitAsync(otherTenantOwner, otherTenantDraft.Id, FakeRepairRequestDraftStore.InitialRowVersion, null, CancellationToken.None);

        Assert.Equal("RR-2026-000001", first.RequestNo);
        Assert.Equal("RR-2026-000002", second.RequestNo);
        Assert.Equal("RR-2026-000001", otherTenantDraft.RequestNo);
    }

    [Fact]
    public async Task Submit_PassesSelectedEquipmentToTheDuplicateQuery()
    {
        var owner = Requester();
        var equipmentId = Guid.NewGuid();
        var draft = CompleteDraft(owner, equipmentId);

        Assert.True((await SubmitAsync(owner, draft)).Succeeded);

        Assert.Equal(equipmentId, Assert.Single(_submits.DuplicateQueries).EquipmentId);
    }

    // ---------------- Ownership / concurrency / state ----------------

    [Fact]
    public async Task Submit_ByAnotherUser_ReturnsNotFound_WithoutAnyFurtherQuery()
    {
        var draft = CompleteDraft(Requester());

        var result = await SubmitAsync(Requester(), draft);

        Assert.Equal(CommandFailure.NotFound, result.Error?.Failure);
        Assert.Equal(0, _drafts.SelectionQueries);
        Assert.Equal(0, _submits.PhotoQueries);
        Assert.Empty(_submits.DuplicateQueries);
        AssertNotSubmitted(draft);
    }

    [Fact]
    public async Task Submit_WithStaleRowVersion_Returns409_WithoutAnyFurtherQuery()
    {
        var owner = Requester();
        var draft = CompleteDraft(owner);

        var result = await SubmitAsync(owner, draft, rowVersion: [9, 9, 9, 9, 9, 9, 9, 9]);

        Assert.Equal(CommandFailure.ConcurrencyConflict, result.Error?.Failure);
        Assert.Equal(0, _drafts.SelectionQueries);
        Assert.Empty(_submits.DuplicateQueries);
        AssertNotSubmitted(draft);
    }

    [Theory]
    [InlineData(RepairRequestStatus.Submitted)]
    [InlineData(RepairRequestStatus.UnderReview)]
    [InlineData(RepairRequestStatus.Approved)]
    [InlineData(RepairRequestStatus.Rejected)]
    [InlineData(RepairRequestStatus.Cancelled)]
    [InlineData(RepairRequestStatus.Converted)]
    public async Task Submit_WhenNotDraft_Returns409StateConflict(RepairRequestStatus status)
    {
        var owner = Requester();
        var draft = CompleteDraft(owner);
        FakeRepairRequestDraftStore.ForceStatus(draft, status);

        var result = await SubmitAsync(owner, draft);

        Assert.Equal(CommandFailure.StateConflict, result.Error?.Failure);
        Assert.Empty(_submits.DuplicateQueries);
        Assert.Equal(0, _submits.SubmitCalls);
    }

    [Fact]
    public async Task Submit_SaveConflict_Returns409_AndCommitsNothing()
    {
        var owner = Requester();
        var draft = CompleteDraft(owner);
        _submits.SaveOutcome = RepairRequestSaveOutcome.ConcurrencyConflict;

        var result = await SubmitAsync(owner, draft);

        Assert.Equal(CommandFailure.ConcurrencyConflict, result.Error?.Failure);
        Assert.Empty(_submits.Audits);
        Assert.Empty(_submits.Counters);
    }

    // ---------------- Validation ----------------

    [Fact]
    public async Task Submit_EmptyDraft_ReportsEveryMissingFieldAndPhotoInOne422_WithoutDuplicateQuery()
    {
        var owner = Requester();
        var draft = _drafts.SeedDraft(owner);

        var result = await SubmitAsync(owner, draft);

        Assert.Equal(CommandFailure.ValidationFailed, result.Error?.Failure);
        Assert.Equal(
            new[]
            {
                RepairRequestFields.Attachments, RepairRequestFields.Description, RepairRequestFields.PreferredEndAt,
                RepairRequestFields.PreferredStartAt, RepairRequestFields.PriorityCode, RepairRequestFields.RequestCategoryCode,
                RepairRequestFields.RequestContactId, RepairRequestFields.SiteId
            },
            result.Error!.Errors.Keys.Order());
        Assert.Null(result.Error.DuplicateCount);
        Assert.Equal(0, _drafts.SelectionQueries);
        Assert.Empty(_submits.DuplicateQueries);
        AssertNotSubmitted(draft);
    }

    [Fact]
    public async Task Submit_InactiveOrUnknownLookups_IneligibleContact_AndInactiveMasters_Return422()
    {
        var owner = Requester();
        var draft = CompleteDraft(owner, Guid.NewGuid());
        _drafts.Categories["ELECTRICAL"] = MasterDataStatus.Inactive;
        _drafts.Priorities.Remove("HIGH");
        _drafts.EligibleContacts.Clear();
        _drafts.SitesInScope[draft.SiteId!.Value] = MasterDataStatus.Inactive;
        _drafts.Equipment[draft.EquipmentId!.Value] = (draft.SiteId.Value, MasterDataStatus.Inactive);

        var result = await SubmitAsync(owner, draft);

        var errors = result.Error!.Errors;
        Assert.Equal(new[] { "The Category is inactive." }, errors[RepairRequestFields.RequestCategoryCode]);
        Assert.Equal(new[] { "The Priority is not valid." }, errors[RepairRequestFields.PriorityCode]);
        Assert.True(errors.ContainsKey(RepairRequestFields.RequestContactId));
        Assert.Equal(new[] { "The Site is inactive." }, errors[RepairRequestFields.SiteId]);
        Assert.Equal(new[] { "The Equipment is inactive." }, errors[RepairRequestFields.EquipmentId]);
        Assert.False(errors.ContainsKey(RepairRequestFields.Attachments));
        Assert.Empty(_submits.DuplicateQueries);
        AssertNotSubmitted(draft);
    }

    [Fact]
    public async Task Submit_WhenSiteLeftTheCallersBusinessScope_ReturnsNotFound_BeforePhotoAndDuplicateQueries()
    {
        var owner = Requester();
        var draft = CompleteDraft(owner);
        _drafts.SitesInScope.Remove(draft.SiteId!.Value);

        var result = await SubmitAsync(owner, draft);

        Assert.Equal(CommandFailure.NotFound, result.Error?.Failure);
        Assert.Equal(0, _submits.PhotoQueries);
        Assert.Empty(_submits.DuplicateQueries);
        AssertNotSubmitted(draft);
    }

    [Fact]
    public async Task Submit_WithoutCleanPhoto_Returns422OnAttachments_WithoutDuplicateQuery()
    {
        var owner = Requester();
        var draft = CompleteDraft(owner, cleanPhoto: false);

        var result = await SubmitAsync(owner, draft);

        Assert.Equal(new[] { RepairRequestFields.Attachments }, result.Error!.Errors.Keys);
        Assert.Empty(_submits.DuplicateQueries);
        AssertNotSubmitted(draft);
    }

    [Fact]
    public async Task Submit_ContinuationReasonLongerThanLimit_Returns422_WithoutDuplicateQuery()
    {
        var owner = Requester();
        var draft = CompleteDraft(owner);

        var result = await SubmitAsync(owner, draft, new string('r', RepairRequestAggregate.ReasonMaxLength + 1));

        Assert.True(result.Error!.Errors.ContainsKey(RepairRequestFields.DuplicateContinuationReason));
        Assert.Empty(_submits.DuplicateQueries);
        AssertNotSubmitted(draft);
    }

    // ---------------- Duplicate warning ----------------

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task Submit_DuplicateWithoutReason_Returns422WithCountOnly_AndStaysDraftWithoutNumberOrSlaStart(string? reason)
    {
        var owner = Requester();
        var draft = CompleteDraft(owner);
        _submits.DuplicateCount = 2;

        var result = await SubmitAsync(owner, draft, reason);

        Assert.Equal(CommandFailure.ValidationFailed, result.Error?.Failure);
        Assert.Equal(new[] { RepairRequestFields.DuplicateContinuationReason }, result.Error!.Errors.Keys);
        Assert.Equal(2, result.Error.DuplicateCount);

        // The count is taken inside the single atomic store call, which commits nothing on a warning.
        Assert.Equal(1, _submits.SubmitCalls);
        Assert.Single(_submits.DuplicateQueries);
        AssertNotSubmitted(draft);
    }

    [Fact]
    public async Task Submit_DuplicateWithReason_Succeeds_StoresAndAuditsTheTrimmedReasonAndCount()
    {
        var owner = Requester();
        var draft = CompleteDraft(owner);
        _submits.DuplicateCount = 1;

        var result = await SubmitAsync(owner, draft, "  Second pump, different fault  ");

        Assert.True(result.Succeeded);
        Assert.Equal("Second pump, different fault", draft.DuplicateContinuationReason);
        var audit = Assert.Single(_submits.Audits);
        Assert.Equal("Second pump, different fault", audit.Reason);
        Assert.Contains("\"duplicateCount\":1", audit.NewValueJson);
    }

    [Fact]
    public async Task Submit_ReasonWithoutDuplicate_Succeeds_AndTheReasonIsIgnored()
    {
        var owner = Requester();
        var draft = CompleteDraft(owner);

        var result = await SubmitAsync(owner, draft, "Not needed");

        Assert.True(result.Succeeded);
        Assert.Null(draft.DuplicateContinuationReason);
        Assert.Null(Assert.Single(_submits.Audits).Reason);
    }
}
