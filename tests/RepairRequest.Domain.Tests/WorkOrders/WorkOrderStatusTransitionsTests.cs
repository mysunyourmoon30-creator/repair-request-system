using RepairRequest.Domain.WorkOrders;

namespace RepairRequest.Domain.Tests.WorkOrders;

/// <summary>Verifies the encoded matrix against RR-STS-001 (ST-WO-001..011). S2-003 introduces ST-WO-001; S3-001 adds ST-WO-002.</summary>
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
    public void CheckIn_IsAllowed_FromScheduledOnly()
    {
        Assert.True(WorkOrderStatusTransitions.IsAllowed(WorkOrderStatus.Scheduled, WorkOrderStatus.InProgress));
        Assert.Contains(
            WorkOrderStatusTransitions.All,
            transition => transition.TransitionId == "ST-WO-002" && transition.From == WorkOrderStatus.Scheduled && transition.To == WorkOrderStatus.InProgress);
    }

    [Fact]
    public void Matrix_ContainsExactlyTheImplementedTransitions()
    {
        Assert.Equal(2, WorkOrderStatusTransitions.All.Count);
    }

    [Theory]
    [InlineData(WorkOrderStatus.Scheduled, WorkOrderStatus.Open)]
    [InlineData(WorkOrderStatus.InProgress, WorkOrderStatus.Scheduled)]
    [InlineData(WorkOrderStatus.Open, WorkOrderStatus.Cancelled)]
    [InlineData(WorkOrderStatus.Open, WorkOrderStatus.Open)]
    [InlineData(WorkOrderStatus.Open, WorkOrderStatus.InProgress)]
    public void TransitionOutsideMatrix_IsNotAllowed(WorkOrderStatus from, WorkOrderStatus to)
    {
        Assert.False(WorkOrderStatusTransitions.IsAllowed(from, to));
    }
}
