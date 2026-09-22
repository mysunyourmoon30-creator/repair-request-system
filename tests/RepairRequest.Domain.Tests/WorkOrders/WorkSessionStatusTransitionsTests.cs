using RepairRequest.Domain.WorkOrders;

namespace RepairRequest.Domain.Tests.WorkOrders;

/// <summary>Verifies the encoded matrix against RR-STS-001 (ST-WS-001..004). S3-002/S3-003 register Pause (ST-WS-002) and Resume (ST-WS-003).</summary>
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
    public void Resume_IsAllowed_FromPausedOnly_WithItsCanonicalId()
    {
        Assert.True(WorkSessionStatusTransitions.IsAllowed(WorkSessionStatus.Paused, WorkSessionStatus.CheckedIn));
        Assert.Contains(
            WorkSessionStatusTransitions.All,
            transition => transition.TransitionId == "ST-WS-003" && transition.From == WorkSessionStatus.Paused && transition.To == WorkSessionStatus.CheckedIn);
    }

    [Fact]
    public void Matrix_ContainsExactlyTheImplementedTransitions()
    {
        Assert.Equal(2, WorkSessionStatusTransitions.All.Count);
    }

    [Theory]
    [InlineData(WorkSessionStatus.Paused, WorkSessionStatus.Paused)]
    [InlineData(WorkSessionStatus.CheckedIn, WorkSessionStatus.CheckedOut)]
    [InlineData(WorkSessionStatus.CheckedIn, WorkSessionStatus.CheckedIn)]
    [InlineData(WorkSessionStatus.CheckedOut, WorkSessionStatus.Paused)]
    [InlineData(WorkSessionStatus.CheckedOut, WorkSessionStatus.CheckedIn)]
    public void TransitionOutsideMatrix_IsNotAllowed(WorkSessionStatus from, WorkSessionStatus to)
    {
        Assert.False(WorkSessionStatusTransitions.IsAllowed(from, to));
    }
}
