using RepairRequest.Domain.Common;
using RepairRequest.Domain.MasterData;

namespace RepairRequest.Domain.Tests.MasterData;

/// <summary>DEC-PS1-001/002/003 construction rules and DEC-PS1-014 deactivate reason.</summary>
public class MasterDataEntityTests
{
    public static TheoryData<string> EntityKinds => ["Customer", "Site", "Equipment"];

    private static MasterDataEntity Create(string kind, string code = "CODE-001") => kind switch
    {
        "Customer" => new Customer(Guid.NewGuid(), code),
        "Site" => new Site(Guid.NewGuid(), Guid.NewGuid(), code),
        "Equipment" => new Equipment(Guid.NewGuid(), Guid.NewGuid(), code),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    [Theory]
    [MemberData(nameof(EntityKinds))]
    public void NewEntity_IsActiveWithoutReason(string kind)
    {
        var entity = Create(kind);

        Assert.Equal(MasterDataStatus.Active, entity.Status);
        Assert.Null(entity.DeactivateReason);
    }

    [Theory]
    [MemberData(nameof(EntityKinds))]
    public void Deactivate_WithReason_BecomesInactive(string kind)
    {
        var entity = Create(kind);

        entity.Deactivate("Contract ended");

        Assert.Equal(MasterDataStatus.Inactive, entity.Status);
        Assert.Equal("Contract ended", entity.DeactivateReason);
    }

    [Theory]
    [InlineData("Customer", null)]
    [InlineData("Site", "")]
    [InlineData("Equipment", "   ")]
    public void Deactivate_WithoutReason_IsRejectedAndStaysActive(string kind, string? reason)
    {
        var entity = Create(kind);

        Assert.Throws<ArgumentException>(() => entity.Deactivate(reason!));
        Assert.Equal(MasterDataStatus.Active, entity.Status);
    }

    [Theory]
    [MemberData(nameof(EntityKinds))]
    public void Deactivate_ReasonLongerThanBaselineLimit_IsRejected(string kind)
    {
        var entity = Create(kind);

        Assert.Throws<ArgumentException>(() => entity.Deactivate(new string('x', MasterDataEntity.DeactivateReasonMaxLength + 1)));
    }

    [Theory]
    [MemberData(nameof(EntityKinds))]
    public void Deactivate_WhenAlreadyInactive_IsRejected(string kind)
    {
        var entity = Create(kind);
        entity.Deactivate("First");

        Assert.Throws<DomainRuleViolationException>(() => entity.Deactivate("Second"));
        Assert.Equal("First", entity.DeactivateReason);
    }

    [Theory]
    [InlineData("Customer", "")]
    [InlineData("Site", "  ")]
    [InlineData("Equipment", null)]
    public void Create_WithBlankCode_IsRejected(string kind, string? code)
    {
        Assert.Throws<ArgumentException>(() => Create(kind, code!));
    }

    [Theory]
    [MemberData(nameof(EntityKinds))]
    public void Create_WithCodeLongerThanLimit_IsRejected(string kind)
    {
        Assert.Throws<ArgumentException>(() => Create(kind, new string('C', MasterDataEntity.CodeMaxLength + 1)));
    }

    [Fact]
    public void Create_WithoutTenant_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => new Customer(Guid.Empty, "C-1"));
    }

    [Fact]
    public void Site_WithoutCustomer_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => new Site(Guid.NewGuid(), Guid.Empty, "S-1"));
    }

    [Fact]
    public void Equipment_WithoutSite_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => new Equipment(Guid.NewGuid(), Guid.Empty, "E-1"));
    }
}
