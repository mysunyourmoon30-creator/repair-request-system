using RepairRequest.Domain.WorkOrders;

namespace RepairRequest.Domain.Tests.WorkOrders;

/// <summary>Verifies the encoded matrix against RR-STS-001 (ST-WO-001..011). S2-003 introduces only ST-WO-001.</summary>
public class WorkOrderStatusTransitionsTests
{
    [Fact]
    public void CanonicalStatuses_MatchRrSts001()
    {
        Assert.Equal(
            [
                "Open", "Scheduled", "InProgress", "AwaitingSupervisorReview", "AwaitingCustomerAcceptance",
                "Completed", "CorrectiveActionRequired", "CorrectivePlanPending", "CorrectivePlanApproved", "Closed", "Cancelled"
            ],
            Enum.GetNames<WorkOrderStatus>());
    }

    [Fact]
    public void Schedule_IsAllowed_FromOpenOnly()
    {
        Assert.True(WorkOrderStatusTransitions.IsAllowed(WorkOrderStatus.Open, WorkOrderStatus.Scheduled));
        Assert.Contains(
            WorkOrderStatusTransitions.All,
            transition => transition.TransitionId == "ST-WO-001" && transition.From == WorkOrderStatus.Open && transition.To == WorkOrderStatus.Scheduled);
    }

    [Fact]
    public void Matrix_ContainsExactlyTheS2003Transition()
    {
        Assert.Single(WorkOrderStatusTransitions.All);
    }

    [Theory]
    [InlineData(WorkOrderStatus.Scheduled, WorkOrderStatus.Open)]
    [InlineData(WorkOrderStatus.Scheduled, WorkOrderStatus.InProgress)]
    [InlineData(WorkOrderStatus.InProgress, WorkOrderStatus.Scheduled)]
    [InlineData(WorkOrderStatus.Open, WorkOrderStatus.Cancelled)]
    [InlineData(WorkOrderStatus.Open, WorkOrderStatus.Open)]
    public void TransitionOutsideMatrix_IsNotAllowed(WorkOrderStatus from, WorkOrderStatus to)
    {
        Assert.False(WorkOrderStatusTransitions.IsAllowed(from, to));
    }
}
