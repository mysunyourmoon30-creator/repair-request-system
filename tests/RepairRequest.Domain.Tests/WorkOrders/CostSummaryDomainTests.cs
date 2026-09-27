using RepairRequest.Domain.Common;
using RepairRequest.Domain.WorkOrders;

namespace RepairRequest.Domain.Tests.WorkOrders;

/// <summary>CST-001/003..011 persisted shape (CST-API-001 Prepare only; BR-08; `docs/13` §4.18).</summary>
public class CostSummaryDomainTests
{
    private static readonly DateTime Now = DateTime.UtcNow;

    [Fact]
    public void Create_SetsIdentityAndFields()
    {
        var tenantId = Guid.NewGuid();
        var workOrderId = Guid.NewGuid();
        var preparedBy = Guid.NewGuid();

        var summary = CostSummary.Create(tenantId, workOrderId, 1234.56m, "USD", "Parts and labor.", preparedBy, Now);

        Assert.Equal(tenantId, summary.TenantId);
        Assert.Equal(workOrderId, summary.WorkOrderId);
        Assert.Equal(1234.56m, summary.TotalAmount);
        Assert.Equal("USD", summary.CurrencyCode);
        Assert.Equal("Parts and labor.", summary.Note);
        Assert.Equal(preparedBy, summary.PreparedBy);
        Assert.Equal(Now, summary.PreparedAt);
        Assert.Null(summary.ReviewedBy);
        Assert.Null(summary.ReviewedAt);
    }

    [Fact]
    public void Create_AllowsANullNote()
    {
        var summary = CostSummary.Create(Guid.NewGuid(), Guid.NewGuid(), 0m, "USD", null, Guid.NewGuid(), Now);
        Assert.Null(summary.Note);
    }

    [Fact]
    public void Create_RequiresATenant() =>
        Assert.Throws<ArgumentException>(() => CostSummary.Create(Guid.Empty, Guid.NewGuid(), 1m, "USD", null, Guid.NewGuid(), Now));

    [Fact]
    public void Create_RequiresAWorkOrder() =>
        Assert.Throws<ArgumentException>(() => CostSummary.Create(Guid.NewGuid(), Guid.Empty, 1m, "USD", null, Guid.NewGuid(), Now));

    [Fact]
    public void Create_RequiresAPreparer() =>
        Assert.Throws<ArgumentException>(() => CostSummary.Create(Guid.NewGuid(), Guid.NewGuid(), 1m, "USD", null, Guid.Empty, Now));

    [Fact]
    public void Create_RequiresNonBlankCurrencyCode() =>
        Assert.Throws<ArgumentException>(() => CostSummary.Create(Guid.NewGuid(), Guid.NewGuid(), 1m, " ", null, Guid.NewGuid(), Now));

    [Fact]
    public void Create_RejectsACurrencyCodeLongerThanThreeCharacters() =>
        Assert.Throws<ArgumentException>(() => CostSummary.Create(Guid.NewGuid(), Guid.NewGuid(), 1m, "USDD", null, Guid.NewGuid(), Now));

    [Fact]
    public void Create_RejectsANoteLongerThanTheMaxLength() =>
        Assert.Throws<ArgumentException>(() =>
            CostSummary.Create(Guid.NewGuid(), Guid.NewGuid(), 1m, "USD", new string('A', CostSummary.NoteMaxLength + 1), Guid.NewGuid(), Now));

    [Fact]
    public void UpdatePreparation_OverwritesFieldsInPlace_WhenNotYetReviewed()
    {
        var summary = CostSummary.Create(Guid.NewGuid(), Guid.NewGuid(), 100m, "USD", "First pass.", Guid.NewGuid(), Now);
        var secondPreparer = Guid.NewGuid();
        var later = Now.AddMinutes(5);

        summary.UpdatePreparation(250.75m, "THB", "Revised after parts arrived.", secondPreparer, later);

        Assert.Equal(250.75m, summary.TotalAmount);
        Assert.Equal("THB", summary.CurrencyCode);
        Assert.Equal("Revised after parts arrived.", summary.Note);
        Assert.Equal(secondPreparer, summary.PreparedBy);
        Assert.Equal(later, summary.PreparedAt);
    }

    [Fact]
    public void UpdatePreparation_ThrowsDomainRuleViolation_WhenAlreadyReviewed()
    {
        // Nothing in this ticket's own scope can ever set ReviewedAt (Review is a future ticket) — this test
        // exercises the domain guard directly via reflection on the private setter, simulating the state the
        // future Review ticket will eventually be able to produce, so the guard is proven correct now rather
        // than left untested until that ticket exists.
        var summary = CostSummary.Create(Guid.NewGuid(), Guid.NewGuid(), 100m, "USD", null, Guid.NewGuid(), Now);
        typeof(CostSummary).GetProperty(nameof(CostSummary.ReviewedAt))!.SetValue(summary, (DateTime?)Now);

        var exception = Assert.Throws<DomainRuleViolationException>(() =>
            summary.UpdatePreparation(999m, "EUR", null, Guid.NewGuid(), Now.AddMinutes(1)));
        Assert.Equal("This Work Order's Cost Summary has already been reviewed.", exception.Message);
    }
}
