using RepairRequest.Domain.WorkOrders;

namespace RepairRequest.Domain.Tests.WorkOrders;

/// <summary>
/// Verifies the encoded matrix against RR-STS-001 (ST-SV-001..009). S2-003 registers the true cross-state
/// transitions Cancel and Mark Missed; S3-001 adds Check-in (ST-SV-002); S3-004 adds Check-out (ST-SV-003).
/// Reschedule/Reassign/Decide Missed are same-state actions guarded directly by <see cref="ServiceVisit"/>'s own
/// methods (see <see cref="ServiceVisitStatusTransitions"/>'s doc comment).
/// </summary>
public class ServiceVisitStatusTransitionsTests
{
    [Fact]
    public void CanonicalStatuses_MatchRrSts001()
    {
        Assert.Equal(
            ["Scheduled", "Rescheduled", "InProgress", "Completed", "Missed", "Cancelled"],
            Enum.GetNames<ServiceVisitStatus>());
    }

    [Theory]
    [InlineData("ST-SV-002", ServiceVisitStatus.Scheduled, ServiceVisitStatus.InProgress)]
    [InlineData("ST-SV-003", ServiceVisitStatus.InProgress, ServiceVisitStatus.Completed)]
    [InlineData("ST-SV-007", ServiceVisitStatus.Scheduled, ServiceVisitStatus.Cancelled)]
    [InlineData("ST-SV-008", ServiceVisitStatus.Scheduled, ServiceVisitStatus.Missed)]
    public void AllowedTransition_IsPresentWithCanonicalId(string transitionId, ServiceVisitStatus from, ServiceVisitStatus to)
    {
        Assert.True(ServiceVisitStatusTransitions.IsAllowed(from, to));
        Assert.Contains(
            ServiceVisitStatusTransitions.All,
            transition => transition.TransitionId == transitionId && transition.From == from && transition.To == to);
    }

    [Fact]
    public void Matrix_ContainsExactlyTheImplementedTransitions()
    {
        Assert.Equal(4, ServiceVisitStatusTransitions.All.Count);
    }

    [Theory]
    [InlineData(ServiceVisitStatus.Scheduled, ServiceVisitStatus.Rescheduled)]
    [InlineData(ServiceVisitStatus.Rescheduled, ServiceVisitStatus.Scheduled)]
    [InlineData(ServiceVisitStatus.Missed, ServiceVisitStatus.Scheduled)]
    [InlineData(ServiceVisitStatus.Cancelled, ServiceVisitStatus.Scheduled)]
    [InlineData(ServiceVisitStatus.Completed, ServiceVisitStatus.InProgress)]
    public void TransitionOutsideMatrix_IsNotAllowed(ServiceVisitStatus from, ServiceVisitStatus to)
    {
        Assert.False(ServiceVisitStatusTransitions.IsAllowed(from, to));
    }

    [Fact]
    public void Matrix_HasNoSelfTransitions()
    {
        Assert.DoesNotContain(ServiceVisitStatusTransitions.All, transition => transition.From == transition.To);
    }
}
