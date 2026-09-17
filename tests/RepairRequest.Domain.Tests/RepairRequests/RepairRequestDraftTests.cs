using RepairRequest.Domain.Common;
using RepairRequest.Domain.RepairRequests;
using RepairRequestAggregate = RepairRequest.Domain.RepairRequests.RepairRequest;

namespace RepairRequest.Domain.Tests.RepairRequests;

/// <summary>ST-RR-001 Draft editing: aggregate-local rules only (UC-RR-001; RR-DD-001 RR-005..RR-020).</summary>
public class RepairRequestDraftTests
{
    private static readonly DateTime Start = new(2026, 9, 20, 8, 0, 0, DateTimeKind.Utc);

    private static RepairRequestAggregate NewDraft() => RepairRequestAggregate.CreateDraft(Guid.NewGuid(), Guid.NewGuid());

    private static void Edit(
        RepairRequestAggregate draft,
        Guid? siteId = null,
        Guid? equipmentId = null,
        string? category = null,
        string? priority = null,
        Guid? contactId = null,
        string? description = null,
        DateTime? start = null,
        DateTime? end = null) =>
        draft.EditDraft(siteId, equipmentId, category, priority, contactId, description, start, end);

    [Fact]
    public void EditDraft_SetsDraftFields_AndLeavesSubmitDataEmpty()
    {
        var draft = NewDraft();
        var siteId = Guid.NewGuid();
        var equipmentId = Guid.NewGuid();
        var contactId = Guid.NewGuid();

        Edit(draft, siteId, equipmentId, "ELECTRICAL", "HIGH", contactId, "Pump leaking", Start, Start.AddHours(2));

        Assert.Equal(RepairRequestStatus.Draft, draft.Status);
        Assert.Equal(siteId, draft.SiteId);
        Assert.Equal(equipmentId, draft.EquipmentId);
        Assert.Equal("ELECTRICAL", draft.RequestCategoryCode);
        Assert.Equal("HIGH", draft.PriorityCode);
        Assert.Equal(contactId, draft.RequestContactId);
        Assert.Equal("Pump leaking", draft.Description);
        Assert.Equal(Start, draft.PreferredStartAt);
        Assert.Equal(Start.AddHours(2), draft.PreferredEndAt);
        Assert.Null(draft.RequestNo);
        Assert.Null(draft.LocationId);
        Assert.Null(draft.SubmittedAt);
    }

    [Fact]
    public void EditDraft_WithEverythingEmpty_IsAValidWorkInProgressDraft()
    {
        var draft = NewDraft();
        Edit(draft, Guid.NewGuid(), category: "IT", priority: "LOW", contactId: Guid.NewGuid(), description: "Text", start: Start);

        Edit(draft);

        Assert.Null(draft.SiteId);
        Assert.Null(draft.RequestCategoryCode);
        Assert.Null(draft.PriorityCode);
        Assert.Null(draft.RequestContactId);
        Assert.Null(draft.Description);
        Assert.Null(draft.PreferredStartAt);
        Assert.Equal(RepairRequestStatus.Draft, draft.Status);
    }

    [Fact]
    public void EditDraft_EquipmentWithoutSite_IsRejected()
    {
        var draft = NewDraft();

        Assert.Throws<ArgumentException>(() => Edit(draft, equipmentId: Guid.NewGuid()));
        Assert.Null(draft.EquipmentId);
    }

    [Fact]
    public void EditDraft_ContactWithoutSite_IsRejected()
    {
        var draft = NewDraft();

        Assert.Throws<ArgumentException>(() => Edit(draft, contactId: Guid.NewGuid(), description: "Text"));
        Assert.Null(draft.RequestContactId);
        Assert.Null(draft.Description);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EditDraft_BlankCodes_AreRejected(string blank)
    {
        var draft = NewDraft();

        Assert.Throws<ArgumentException>(() => Edit(draft, category: blank));
        Assert.Throws<ArgumentException>(() => Edit(draft, priority: blank));
    }

    [Fact]
    public void EditDraft_CodesLongerThanLimit_AreRejected()
    {
        var draft = NewDraft();

        Assert.Throws<ArgumentException>(() => Edit(draft, category: new string('C', RepairRequestAggregate.RequestCategoryCodeMaxLength + 1)));
        Assert.Throws<ArgumentException>(() => Edit(draft, priority: new string('P', RepairRequestAggregate.PriorityCodeMaxLength + 1)));
    }

    [Fact]
    public void EditDraft_PreferredEndBeforeStart_IsRejected()
    {
        var draft = NewDraft();

        Assert.Throws<ArgumentException>(() => Edit(draft, start: Start, end: Start.AddMinutes(-1)));
        Assert.Null(draft.PreferredStartAt);
    }

    [Fact]
    public void EditDraft_PreferredEndEqualToStart_IsAllowed()
    {
        var draft = NewDraft();

        Edit(draft, start: Start, end: Start);

        Assert.Equal(draft.PreferredStartAt, draft.PreferredEndAt);
    }

    [Fact]
    public void EditDraft_NonUtcTimestamp_IsRejected()
    {
        var draft = NewDraft();

        Assert.Throws<ArgumentException>(() => Edit(draft, start: DateTime.SpecifyKind(Start, DateTimeKind.Local)));
        Assert.Throws<ArgumentException>(() => Edit(draft, end: DateTime.SpecifyKind(Start, DateTimeKind.Unspecified)));
    }

    [Fact]
    public void EditDraft_DescriptionLongerThanLimit_IsRejected()
    {
        var draft = NewDraft();

        Assert.Throws<ArgumentException>(() => Edit(draft, description: new string('d', RepairRequestAggregate.DescriptionMaxLength + 1)));
    }

    [Fact]
    public void EditDraft_EmptyIdentifiers_AreRejected()
    {
        var draft = NewDraft();

        Assert.Throws<ArgumentException>(() => Edit(draft, Guid.Empty));
        Assert.Throws<ArgumentException>(() => Edit(draft, Guid.NewGuid(), Guid.Empty));
        Assert.Throws<ArgumentException>(() => Edit(draft, Guid.NewGuid(), contactId: Guid.Empty));
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

        Assert.Throws<DomainRuleViolationException>(() => Edit(draft, description: "Changed"));
        Assert.Null(draft.Description);
    }
}
