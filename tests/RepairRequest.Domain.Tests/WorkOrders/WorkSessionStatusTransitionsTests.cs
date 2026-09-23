using RepairRequest.Domain.WorkOrders;

namespace RepairRequest.Domain.Tests.WorkOrders;

/// <summary>Verifies the encoded matrix against RR-STS-001 (ST-WS-001..004). S3-002/S3-003/S3-004 register Pause (ST-WS-002), Resume (ST-WS-003) and Check-out (ST-WS-004).</summary>
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
    public void CheckOut_IsAllowed_FromCheckedInOnly_WithItsCanonicalId()
    {
        Assert.True(WorkSessionStatusTransitions.IsAllowed(WorkSessionStatus.CheckedIn, WorkSessionStatus.CheckedOut));
        Assert.Contains(
            WorkSessionStatusTransitions.All,
            transition => transition.TransitionId == "ST-WS-004" && transition.From == WorkSessionStatus.CheckedIn && transition.To == WorkSessionStatus.CheckedOut);
    }

    [Fact]
    public void Matrix_ContainsExactlyTheImplementedTransitions()
    {
        Assert.Equal(3, WorkSessionStatusTransitions.All.Count);
    }

    [Theory]
    [InlineData(WorkSessionStatus.Paused, WorkSessionStatus.Paused)]
    [InlineData(WorkSessionStatus.Paused, WorkSessionStatus.CheckedOut)]
    [InlineData(WorkSessionStatus.CheckedIn, WorkSessionStatus.CheckedIn)]
    [InlineData(WorkSessionStatus.CheckedOut, WorkSessionStatus.Paused)]
    [InlineData(WorkSessionStatus.CheckedOut, WorkSessionStatus.CheckedIn)]
    [InlineData(WorkSessionStatus.CheckedOut, WorkSessionStatus.CheckedOut)]
    public void TransitionOutsideMatrix_IsNotAllowed(WorkSessionStatus from, WorkSessionStatus to)
    {
        Assert.False(WorkSessionStatusTransitions.IsAllowed(from, to));
    }
}
