using RepairRequest.Domain.WorkOrders;

namespace RepairRequest.Domain.Tests.WorkOrders;

/// <summary>WSM-001/003..007 persisted shape (UC-WO-020; `docs/13` §4.15). Create only — no edit exists in this ticket's scope.</summary>
public class WorkSummaryDomainTests
{
    [Fact]
    public void Create_SetsIdentityAndFields_WithRevisionOne()
    {
        var tenantId = Guid.NewGuid();
        var workOrderId = Guid.NewGuid();
        var visitId = Guid.NewGuid();

        var summary = WorkSummary.Create(tenantId, workOrderId, visitId, "Replaced the pump seal.", RepairOutcomeCode.Repaired);

        Assert.Equal(tenantId, summary.TenantId);
        Assert.Equal(workOrderId, summary.WorkOrderId);
        Assert.Equal(visitId, summary.ServiceVisitId);
        Assert.Equal(1, summary.RevisionNo);
        Assert.Equal("Replaced the pump seal.", summary.SummaryText);
        Assert.Equal(RepairOutcomeCode.Repaired, summary.RepairOutcomeCode);
    }

    [Theory]
    [InlineData(RepairOutcomeCode.Repaired)]
    [InlineData(RepairOutcomeCode.TemporaryFix)]
    [InlineData(RepairOutcomeCode.PartsRequired)]
    [InlineData(RepairOutcomeCode.NoFaultFound)]
    [InlineData(RepairOutcomeCode.NotRepairable)]
    [InlineData(RepairOutcomeCode.FollowUpRequired)]
    public void Create_AcceptsEveryAllowedOutcomeCode(RepairOutcomeCode code)
    {
        var summary = WorkSummary.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "text", code);

        Assert.Equal(code, summary.RepairOutcomeCode);
    }

    [Fact]
    public void Create_RequiresATenant() =>
        Assert.Throws<ArgumentException>(() => WorkSummary.Create(Guid.Empty, Guid.NewGuid(), Guid.NewGuid(), "text", RepairOutcomeCode.Repaired));

    [Fact]
    public void Create_RequiresAWorkOrder() =>
        Assert.Throws<ArgumentException>(() => WorkSummary.Create(Guid.NewGuid(), Guid.Empty, Guid.NewGuid(), "text", RepairOutcomeCode.Repaired));

    [Fact]
    public void Create_RequiresAServiceVisit() =>
        Assert.Throws<ArgumentException>(() => WorkSummary.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.Empty, "text", RepairOutcomeCode.Repaired));

    [Fact]
    public void Create_RequiresNonBlankSummaryText() =>
        Assert.Throws<ArgumentException>(() => WorkSummary.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), " ", RepairOutcomeCode.Repaired));

    [Fact]
    public void Create_RejectsSummaryTextLongerThanTheMaxLength() =>
        Assert.Throws<ArgumentException>(() =>
            WorkSummary.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new string('A', WorkSummary.SummaryTextMaxLength + 1), RepairOutcomeCode.Repaired));
}
