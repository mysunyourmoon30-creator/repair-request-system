using RepairRequest.Domain.WorkOrders;

namespace RepairRequest.Domain.Tests.WorkOrders;

/// <summary>
/// Verifies the encoded matrix against RR-STS-001 (ST-TC-001..003; S3-005). Request (ST-TC-001) creates the
/// record and is not a from-state transition, same convention as Check-in (ST-WS-001).
/// </summary>
public class TimeCorrectionStatusTransitionsTests
{
    [Fact]
    public void CanonicalStatuses_MatchRrSts001()
    {
        Assert.Equal(["Pending", "Approved", "Rejected"], Enum.GetNames<TimeCorrectionStatus>());
    }

    [Theory]
    [InlineData("ST-TC-002", TimeCorrectionStatus.Pending, TimeCorrectionStatus.Approved)]
    [InlineData("ST-TC-003", TimeCorrectionStatus.Pending, TimeCorrectionStatus.Rejected)]
    public void AllowedTransition_IsPresentWithCanonicalId(string transitionId, TimeCorrectionStatus from, TimeCorrectionStatus to)
    {
        Assert.True(TimeCorrectionStatusTransitions.IsAllowed(from, to));
        Assert.Contains(
            TimeCorrectionStatusTransitions.All,
            transition => transition.TransitionId == transitionId && transition.From == from && transition.To == to);
    }

    [Fact]
    public void Matrix_ContainsExactlyTheImplementedTransitions()
    {
        Assert.Equal(2, TimeCorrectionStatusTransitions.All.Count);
    }

    [Theory]
    [InlineData(TimeCorrectionStatus.Pending, TimeCorrectionStatus.Pending)]
    [InlineData(TimeCorrectionStatus.Approved, TimeCorrectionStatus.Pending)]
    [InlineData(TimeCorrectionStatus.Approved, TimeCorrectionStatus.Rejected)]
    [InlineData(TimeCorrectionStatus.Approved, TimeCorrectionStatus.Approved)]
    [InlineData(TimeCorrectionStatus.Rejected, TimeCorrectionStatus.Pending)]
    [InlineData(TimeCorrectionStatus.Rejected, TimeCorrectionStatus.Approved)]
    [InlineData(TimeCorrectionStatus.Rejected, TimeCorrectionStatus.Rejected)]
    public void TransitionOutsideMatrix_IsNotAllowed(TimeCorrectionStatus from, TimeCorrectionStatus to)
    {
        Assert.False(TimeCorrectionStatusTransitions.IsAllowed(from, to));
    }
}
