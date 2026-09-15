using RepairRequest.Domain.MasterData;
using RepairRequest.Domain.RepairRequests;
using RepairRequestAggregate = RepairRequest.Domain.RepairRequests.RepairRequest;

namespace RepairRequest.Domain.Tests.RepairRequests;

/// <summary>Request No format (DEC-PRE-S1-007-11) and tenant-scoped Category/Priority lookup construction.</summary>
public class RequestNumberAndLookupTests
{
    [Theory]
    [InlineData(2026, 1, "RR-2026-000001")]
    [InlineData(2026, 42, "RR-2026-000042")]
    [InlineData(2027, 999_999, "RR-2027-999999")]
    public void Format_UsesYearAndSixDigitSequence(int year, int sequence, string expected)
    {
        var number = RequestNumber.Format(year, sequence);

        Assert.Equal(expected, number);
        Assert.True(number.Length <= RepairRequestAggregate.RequestNoMaxLength);
    }

    [Fact]
    public void Format_RejectsOutOfRangeInput_AndFailsClosedWhenSequenceIsExhausted()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RequestNumber.Format(2026, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => RequestNumber.Format(0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => RequestNumber.Format(10_000, 1));
        Assert.Throws<InvalidOperationException>(() => RequestNumber.Format(2026, RequestNumber.MaxSequence + 1));
    }

    [Fact]
    public void Lookups_AreTenantScopedAndActiveByDefault()
    {
        var tenantId = Guid.NewGuid();

        var category = new RequestCategory(tenantId, "ELECTRICAL", "Electrical");
        var priority = new RequestPriority(tenantId, "LOW", "Low", MasterDataStatus.Inactive);

        Assert.Equal(tenantId, category.TenantId);
        Assert.Equal("ELECTRICAL", category.Code);
        Assert.Equal(MasterDataStatus.Active, category.Status);
        Assert.Equal(MasterDataStatus.Inactive, priority.Status);
    }

    [Fact]
    public void Lookups_RejectInvalidValues()
    {
        Assert.Throws<ArgumentException>(() => new RequestCategory(Guid.Empty, "IT", "IT"));
        Assert.Throws<ArgumentException>(() => new RequestCategory(Guid.NewGuid(), " ", "IT"));
        Assert.Throws<ArgumentException>(() => new RequestCategory(Guid.NewGuid(), new string('C', RequestCategory.CodeMaxLength + 1), "IT"));
        Assert.Throws<ArgumentException>(() => new RequestPriority(Guid.NewGuid(), "LOW", " "));
        Assert.Throws<ArgumentException>(() => new RequestPriority(Guid.NewGuid(), new string('P', RequestPriority.CodeMaxLength + 1), "Low"));
        Assert.Throws<ArgumentException>(() => new RequestPriority(Guid.NewGuid(), "LOW", new string('n', RequestPriority.NameMaxLength + 1)));
    }
}
