using RepairRequest.Domain.WorkOrders;

namespace RepairRequest.Domain.Tests.WorkOrders;

/// <summary>Verifies the encoded matrix against RR-STS-001 (ST-WS-001..004). S3-002 registers only Pause (ST-WS-002).</summary>
public class WorkSessionStatusTransitionsTests
{
    [Fact]
    public void CanonicalStatuses_MatchRrSts001()
    {
        Assert.Equal(["CheckedIn", "Paused", "CheckedOut"], Enum.GetNames<WorkSessionStatus>());
    }

    [Fact]
    public void Pause_IsAllowed_FromCheckedInOnly_WithItsCanonicalId()
    {
        Assert.True(WorkSessionStatusTransitions.IsAllowed(WorkSessionStatus.CheckedIn, WorkSessionStatus.Paused));
        Assert.Contains(
            WorkSessionStatusTransitions.All,
            transition => transition.TransitionId == "ST-WS-002" && transition.From == WorkSessionStatus.CheckedIn && transition.To == WorkSessionStatus.Paused);
    }

    [Fact]
    public void Matrix_ContainsExactlyTheImplementedTransition()
    {
        Assert.Single(WorkSessionStatusTransitions.All);
    }

    [Theory]
    [InlineData(WorkSessionStatus.Paused, WorkSessionStatus.Paused)]
    [InlineData(WorkSessionStatus.Paused, WorkSessionStatus.CheckedIn)]
    [InlineData(WorkSessionStatus.CheckedIn, WorkSessionStatus.CheckedOut)]
    [InlineData(WorkSessionStatus.CheckedIn, WorkSessionStatus.CheckedIn)]
    [InlineData(WorkSessionStatus.CheckedOut, WorkSessionStatus.Paused)]
    public void TransitionOutsideMatrix_IsNotAllowed(WorkSessionStatus from, WorkSessionStatus to)
    {
        Assert.False(WorkSessionStatusTransitions.IsAllowed(from, to));
    }
}
