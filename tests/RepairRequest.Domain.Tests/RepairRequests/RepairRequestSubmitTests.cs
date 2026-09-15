using RepairRequest.Domain.Common;
using RepairRequest.Domain.RepairRequests;
using RepairRequestAggregate = RepairRequest.Domain.RepairRequests.RepairRequest;

namespace RepairRequest.Domain.Tests.RepairRequests;

/// <summary>ST-RR-002 Submit: aggregate-local guards (DRAFT only, owner, required fields, immutable Request No, UTC).</summary>
public class RepairRequestSubmitTests
{
    private static readonly DateTime Start = new(2026, 9, 20, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime SubmittedAt = new(2026, 9, 14, 10, 0, 0, 123, DateTimeKind.Utc);

    private static RepairRequestAggregate CompleteDraft(Guid? owner = null)
    {
        var draft = RepairRequestAggregate.CreateDraft(Guid.NewGuid(), owner ?? Guid.NewGuid());
        draft.EditDraft(Guid.NewGuid(), null, "ELECTRICAL", "HIGH", Guid.NewGuid(), "Pump leaking", Start, Start.AddHours(2));
        return draft;
    }

    private static void Set(RepairRequestAggregate request, string property, object? value) =>
        typeof(RepairRequestAggregate).GetProperty(property)!.SetValue(request, value);

    [Fact]
    public void Submit_CompleteDraft_BecomesSubmittedWithNumberActorAndSlaStartMarker()
    {
        var draft = CompleteDraft();

        draft.Submit("RR-2026-000001", draft.CreatedBy, SubmittedAt, null);

        Assert.Equal(RepairRequestStatus.Submitted, draft.Status);
        Assert.Equal("RR-2026-000001", draft.RequestNo);
        Assert.Equal(draft.CreatedBy, draft.SubmittedBy);
        Assert.Equal(SubmittedAt, draft.SubmittedAt);
        Assert.Null(draft.DuplicateContinuationReason);
    }

    [Fact]
    public void Submit_WithContinuationReason_StoresIt()
    {
        var draft = CompleteDraft();

        draft.Submit("RR-2026-000002", draft.CreatedBy, SubmittedAt, "Different failure on the same pump");

        Assert.Equal("Different failure on the same pump", draft.DuplicateContinuationReason);
    }

    [Theory]
    [InlineData(RepairRequestStatus.Submitted)]
    [InlineData(RepairRequestStatus.UnderReview)]
    [InlineData(RepairRequestStatus.Approved)]
    [InlineData(RepairRequestStatus.Rejected)]
    [InlineData(RepairRequestStatus.Cancelled)]
    [InlineData(RepairRequestStatus.Converted)]
    public void Submit_WhenNotDraft_IsRejectedWithoutChange(RepairRequestStatus status)
    {
        var draft = CompleteDraft();
        Set(draft, nameof(RepairRequestAggregate.Status), status);

        Assert.Throws<DomainRuleViolationException>(() => draft.Submit("RR-2026-000001", draft.CreatedBy, SubmittedAt, null));
        Assert.Null(draft.RequestNo);
        Assert.Null(draft.SubmittedAt);
    }

    [Fact]
    public void Submit_WhenRequestNoAlreadyAssigned_IsRejected_RequestNoIsImmutable()
    {
        var draft = CompleteDraft();
        Set(draft, nameof(RepairRequestAggregate.RequestNo), "RR-2026-000007");

        Assert.Throws<DomainRuleViolationException>(() => draft.Submit("RR-2026-000008", draft.CreatedBy, SubmittedAt, null));
        Assert.Equal("RR-2026-000007", draft.RequestNo);
        Assert.Equal(RepairRequestStatus.Draft, draft.Status);
    }

    [Fact]
    public void Submit_ByAnotherUser_IsRejected()
    {
        var draft = CompleteDraft();

        Assert.Throws<DomainRuleViolationException>(() => draft.Submit("RR-2026-000001", Guid.NewGuid(), SubmittedAt, null));
        Assert.Equal(RepairRequestStatus.Draft, draft.Status);
    }

    public static TheoryData<string> RequiredFields =>
    [
        nameof(RepairRequestAggregate.SiteId),
        nameof(RepairRequestAggregate.RequestCategoryCode),
        nameof(RepairRequestAggregate.PriorityCode),
        nameof(RepairRequestAggregate.RequestContactId),
        nameof(RepairRequestAggregate.Description),
        nameof(RepairRequestAggregate.PreferredStartAt),
        nameof(RepairRequestAggregate.PreferredEndAt)
    ];

    [Theory]
    [MemberData(nameof(RequiredFields))]
    public void Submit_WithMissingRequiredField_IsRejected(string field)
    {
        var draft = CompleteDraft();
        Set(draft, field, null);

        Assert.Throws<DomainRuleViolationException>(() => draft.Submit("RR-2026-000001", draft.CreatedBy, SubmittedAt, null));
        Assert.Equal(RepairRequestStatus.Draft, draft.Status);
        Assert.Null(draft.RequestNo);
    }

    [Fact]
    public void Submit_WithInvalidArguments_IsRejected()
    {
        var draft = CompleteDraft();

        Assert.Throws<ArgumentException>(() => draft.Submit(" ", draft.CreatedBy, SubmittedAt, null));
        Assert.Throws<ArgumentException>(() => draft.Submit(new string('R', RepairRequestAggregate.RequestNoMaxLength + 1), draft.CreatedBy, SubmittedAt, null));
        Assert.Throws<ArgumentException>(() => draft.Submit("RR-2026-000001", Guid.Empty, SubmittedAt, null));
        Assert.Throws<ArgumentException>(() => draft.Submit("RR-2026-000001", draft.CreatedBy, DateTime.SpecifyKind(SubmittedAt, DateTimeKind.Local), null));
        Assert.Throws<ArgumentException>(() => draft.Submit("RR-2026-000001", draft.CreatedBy, SubmittedAt, new string('r', RepairRequestAggregate.ReasonMaxLength + 1)));
        Assert.Equal(RepairRequestStatus.Draft, draft.Status);
        Assert.Null(draft.RequestNo);
    }

    [Fact]
    public void Submit_Twice_SecondAttemptIsRejected()
    {
        var draft = CompleteDraft();
        draft.Submit("RR-2026-000001", draft.CreatedBy, SubmittedAt, null);

        Assert.Throws<DomainRuleViolationException>(() => draft.Submit("RR-2026-000002", draft.CreatedBy, SubmittedAt, null));
        Assert.Equal("RR-2026-000001", draft.RequestNo);
    }
}
