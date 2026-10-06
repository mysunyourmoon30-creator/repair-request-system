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
    public void SubmitWorkSummary_IsAllowed_FromInProgressOnly()
    {
        Assert.True(WorkOrderStatusTransitions.IsAllowed(WorkOrderStatus.InProgress, WorkOrderStatus.AwaitingSupervisorReview));
        Assert.Contains(
            WorkOrderStatusTransitions.All,
            transition => transition.TransitionId == "ST-WO-003"
                && transition.From == WorkOrderStatus.InProgress
                && transition.To == WorkOrderStatus.AwaitingSupervisorReview);
    }

    [Fact]
    public void SubmitForAcceptance_IsAllowed_FromAwaitingSupervisorReviewOnly()
    {
        Assert.True(WorkOrderStatusTransitions.IsAllowed(WorkOrderStatus.AwaitingSupervisorReview, WorkOrderStatus.AwaitingCustomerAcceptance));
        Assert.Contains(
            WorkOrderStatusTransitions.All,
            transition => transition.TransitionId == "ST-WO-004"
                && transition.From == WorkOrderStatus.AwaitingSupervisorReview
                && transition.To == WorkOrderStatus.AwaitingCustomerAcceptance);
    }

    [Fact]
    public void Accept_IsAllowed_FromAwaitingCustomerAcceptanceOnly()
    {
        Assert.True(WorkOrderStatusTransitions.IsAllowed(WorkOrderStatus.AwaitingCustomerAcceptance, WorkOrderStatus.Completed));
        Assert.Contains(
            WorkOrderStatusTransitions.All,
            transition => transition.TransitionId == "ST-WO-005"
                && transition.From == WorkOrderStatus.AwaitingCustomerAcceptance
                && transition.To == WorkOrderStatus.Completed);
    }

    [Fact]
    public void Reject_IsAllowed_FromAwaitingCustomerAcceptanceOnly()
    {
        Assert.True(WorkOrderStatusTransitions.IsAllowed(WorkOrderStatus.AwaitingCustomerAcceptance, WorkOrderStatus.CorrectiveActionRequired));
        Assert.Contains(
            WorkOrderStatusTransitions.All,
            transition => transition.TransitionId == "ST-WO-007"
                && transition.From == WorkOrderStatus.AwaitingCustomerAcceptance
                && transition.To == WorkOrderStatus.CorrectiveActionRequired);
    }

    [Fact]
    public void Close_IsAllowed_FromCompletedOnly()
    {
        Assert.True(WorkOrderStatusTransitions.IsAllowed(WorkOrderStatus.Completed, WorkOrderStatus.Closed));
        Assert.Contains(
            WorkOrderStatusTransitions.All,
            transition => transition.TransitionId == "ST-WO-006"
                && transition.From == WorkOrderStatus.Completed
                && transition.Action == "Close"
                && transition.To == WorkOrderStatus.Closed);
    }

    [Fact]
    public void SubmitCorrectivePlan_IsAllowed_FromCorrectiveActionRequiredOnly()
    {
        Assert.True(WorkOrderStatusTransitions.IsAllowed(WorkOrderStatus.CorrectiveActionRequired, WorkOrderStatus.CorrectivePlanPending));
        Assert.Contains(
            WorkOrderStatusTransitions.All,
            transition => transition.TransitionId == "ST-WO-008"
                && transition.From == WorkOrderStatus.CorrectiveActionRequired
                && transition.To == WorkOrderStatus.CorrectivePlanPending);
    }

    [Fact]
    public void ApproveCorrectivePlan_IsAllowed_FromCorrectivePlanPendingOnly()
    {
        Assert.True(WorkOrderStatusTransitions.IsAllowed(WorkOrderStatus.CorrectivePlanPending, WorkOrderStatus.CorrectivePlanApproved));
        Assert.Contains(
            WorkOrderStatusTransitions.All,
            transition => transition.TransitionId == "ST-WO-009"
                && transition.From == WorkOrderStatus.CorrectivePlanPending
                && transition.To == WorkOrderStatus.CorrectivePlanApproved);
    }

    [Fact]
    public void StartRework_IsAllowed_FromCorrectivePlanApprovedOnly()
    {
        // ST-WO-010 (`docs/13` §4.23): fires through the same Check-in flow as ST-WO-002, on a corrective Visit.
        Assert.True(WorkOrderStatusTransitions.IsAllowed(WorkOrderStatus.CorrectivePlanApproved, WorkOrderStatus.InProgress));
        Assert.Contains(
            WorkOrderStatusTransitions.All,
            transition => transition.TransitionId == "ST-WO-010"
                && transition.From == WorkOrderStatus.CorrectivePlanApproved
                && transition.To == WorkOrderStatus.InProgress);
    }

    [Theory]
    [InlineData(WorkOrderStatus.Open)]
    [InlineData(WorkOrderStatus.Scheduled)]
    [InlineData(WorkOrderStatus.InProgress)]
    [InlineData(WorkOrderStatus.AwaitingSupervisorReview)]
    [InlineData(WorkOrderStatus.AwaitingCustomerAcceptance)]
    [InlineData(WorkOrderStatus.CorrectiveActionRequired)]
    [InlineData(WorkOrderStatus.CorrectivePlanPending)]
    [InlineData(WorkOrderStatus.CorrectivePlanApproved)]
    public void Cancel_IsAllowed_FromEveryNonTerminalPreAcceptState(WorkOrderStatus from)
    {
        // ST-WO-011 (`docs/13` §4.25): RR-STS-001's own "OPEN/SCHEDULED/IN_PROGRESS/AWAITING_*/CORRECTIVE_*" shorthand.
        Assert.True(WorkOrderStatusTransitions.IsAllowed(from, WorkOrderStatus.Cancelled));
        Assert.Contains(
            WorkOrderStatusTransitions.All,
            transition => transition.TransitionId == "ST-WO-011" && transition.From == from && transition.To == WorkOrderStatus.Cancelled);
    }

    [Theory]
    [InlineData(WorkOrderStatus.Completed)]
    [InlineData(WorkOrderStatus.Closed)]
    [InlineData(WorkOrderStatus.Cancelled)]
    public void Cancel_IsNotAllowed_OnceAcceptedOrTerminal(WorkOrderStatus from)
    {
        Assert.False(WorkOrderStatusTransitions.IsAllowed(from, WorkOrderStatus.Cancelled));
    }

    [Fact]
    public void Matrix_ContainsExactlyTheImplementedTransitions()
    {
        Assert.Equal(18, WorkOrderStatusTransitions.All.Count);
    }

    [Theory]
    [InlineData(WorkOrderStatus.Open)]
    [InlineData(WorkOrderStatus.Scheduled)]
    [InlineData(WorkOrderStatus.InProgress)]
    [InlineData(WorkOrderStatus.AwaitingSupervisorReview)]
    [InlineData(WorkOrderStatus.AwaitingCustomerAcceptance)]
    [InlineData(WorkOrderStatus.CorrectiveActionRequired)]
    [InlineData(WorkOrderStatus.Closed)]
    [InlineData(WorkOrderStatus.Cancelled)]
    public void Close_IsNotAllowed_FromAnyOtherState(WorkOrderStatus from)
    {
        Assert.False(WorkOrderStatusTransitions.IsAllowed(from, WorkOrderStatus.Closed));
    }

    [Theory]
    [InlineData(WorkOrderStatus.Open)]
    [InlineData(WorkOrderStatus.Completed)]
    [InlineData(WorkOrderStatus.Closed)]
    [InlineData(WorkOrderStatus.AwaitingCustomerAcceptance)]
    [InlineData(WorkOrderStatus.CorrectiveActionRequired)]
    public void Closed_IsTerminal_NoTransitionLeavesIt(WorkOrderStatus to)
    {
        // No Reopen in MVP (docs/01 §4; docs/03 §7).
        Assert.DoesNotContain(WorkOrderStatusTransitions.All, transition => transition.From == WorkOrderStatus.Closed);
        Assert.False(WorkOrderStatusTransitions.IsAllowed(WorkOrderStatus.Closed, to));
    }

    [Theory]
    [InlineData(WorkOrderStatus.Scheduled, WorkOrderStatus.Open)]
    [InlineData(WorkOrderStatus.InProgress, WorkOrderStatus.Scheduled)]
    [InlineData(WorkOrderStatus.Completed, WorkOrderStatus.Cancelled)]
    [InlineData(WorkOrderStatus.Open, WorkOrderStatus.Open)]
    [InlineData(WorkOrderStatus.Open, WorkOrderStatus.InProgress)]
    public void TransitionOutsideMatrix_IsNotAllowed(WorkOrderStatus from, WorkOrderStatus to)
    {
        Assert.False(WorkOrderStatusTransitions.IsAllowed(from, to));
    }
}
