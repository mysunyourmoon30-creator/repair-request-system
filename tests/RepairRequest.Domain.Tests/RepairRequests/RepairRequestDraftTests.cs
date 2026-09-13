using RepairRequest.Domain.Common;
using RepairRequest.Domain.RepairRequests;
using RepairRequestAggregate = RepairRequest.Domain.RepairRequests.RepairRequest;

namespace RepairRequest.Domain.Tests.RepairRequests;

/// <summary>ST-RR-001 Draft editing: aggregate-local rules only (UC-RR-001; RR-DD-001 RR-005..RR-020).</summary>
public class RepairRequestDraftTests
{
    private static readonly DateTime Start = new(2026, 9, 20, 8, 0, 0, DateTimeKind.Utc);

    private static RepairRequestAggregate NewDraft() => RepairRequestAggregate.CreateDraft(Guid.NewGuid(), Guid.NewGuid());

    [Fact]
    public void EditDraft_SetsDraftFields_AndLeavesSubmitDataEmpty()
    {
        var draft = NewDraft();
        var siteId = Guid.NewGuid();
        var equipmentId = Guid.NewGuid();

        draft.EditDraft(siteId, equipmentId, "Pump leaking", Start, Start.AddHours(2));

        Assert.Equal(RepairRequestStatus.Draft, draft.Status);
        Assert.Equal(siteId, draft.SiteId);
        Assert.Equal(equipmentId, draft.EquipmentId);
        Assert.Equal("Pump leaking", draft.Description);
        Assert.Equal(Start, draft.PreferredStartAt);
        Assert.Equal(Start.AddHours(2), draft.PreferredEndAt);
        Assert.Null(draft.RequestNo);
        Assert.Null(draft.RequestCategoryCode);
        Assert.Null(draft.PriorityCode);
        Assert.Null(draft.LocationId);
        Assert.Null(draft.RequestContactId);
    }

    [Fact]
    public void EditDraft_WithEverythingEmpty_IsAValidWorkInProgressDraft()
    {
        var draft = NewDraft();
        draft.EditDraft(Guid.NewGuid(), null, "Text", Start, null);

        draft.EditDraft(null, null, null, null, null);

        Assert.Null(draft.SiteId);
        Assert.Null(draft.Description);
        Assert.Null(draft.PreferredStartAt);
        Assert.Equal(RepairRequestStatus.Draft, draft.Status);
    }

    [Fact]
    public void EditDraft_EquipmentWithoutSite_IsRejected()
    {
        var draft = NewDraft();

        Assert.Throws<ArgumentException>(() => draft.EditDraft(null, Guid.NewGuid(), null, null, null));
        Assert.Null(draft.EquipmentId);
    }

    [Fact]
    public void EditDraft_PreferredEndBeforeStart_IsRejected()
    {
        var draft = NewDraft();

        Assert.Throws<ArgumentException>(() => draft.EditDraft(null, null, null, Start, Start.AddMinutes(-1)));
        Assert.Null(draft.PreferredStartAt);
    }

    [Fact]
    public void EditDraft_PreferredEndEqualToStart_IsAllowed()
    {
        var draft = NewDraft();

        draft.EditDraft(null, null, null, Start, Start);

        Assert.Equal(draft.PreferredStartAt, draft.PreferredEndAt);
    }

    [Fact]
    public void EditDraft_NonUtcTimestamp_IsRejected()
    {
        var draft = NewDraft();

        Assert.Throws<ArgumentException>(() => draft.EditDraft(null, null, null, DateTime.SpecifyKind(Start, DateTimeKind.Local), null));
        Assert.Throws<ArgumentException>(() => draft.EditDraft(null, null, null, null, DateTime.SpecifyKind(Start, DateTimeKind.Unspecified)));
    }

    [Fact]
    public void EditDraft_DescriptionLongerThanLimit_IsRejected()
    {
        var draft = NewDraft();

        Assert.Throws<ArgumentException>(() =>
            draft.EditDraft(null, null, new string('d', RepairRequestAggregate.DescriptionMaxLength + 1), null, null));
    }

    [Fact]
    public void EditDraft_EmptyIdentifiers_AreRejected()
    {
        var draft = NewDraft();

        Assert.Throws<ArgumentException>(() => draft.EditDraft(Guid.Empty, null, null, null, null));
        Assert.Throws<ArgumentException>(() => draft.EditDraft(Guid.NewGuid(), Guid.Empty, null, null, null));
    }

    [Theory]
    [InlineData(RepairRequestStatus.Submitted)]
    [InlineData(RepairRequestStatus.UnderReview)]
    [InlineData(RepairRequestStatus.Approved)]
    [InlineData(RepairRequestStatus.Rejected)]
    [InlineData(RepairRequestStatus.Cancelled)]
    [InlineData(RepairRequestStatus.Converted)]
    public void EditDraft_WhenNotDraft_IsRejected(RepairRequestStatus status)
    {
        var draft = NewDraft();
        typeof(RepairRequestAggregate).GetProperty(nameof(RepairRequestAggregate.Status))!.SetValue(draft, status);

        Assert.Throws<DomainRuleViolationException>(() => draft.EditDraft(null, null, "Changed", null, null));
        Assert.Null(draft.Description);
    }
}
