using RepairRequest.Domain.WorkOrders;

namespace RepairRequest.Domain.Tests.WorkOrders;

/// <summary>Verifies the encoded matrix against RR-STS-001 (ST-CA-002/003; `docs/13` §4.21; Ticket 6).</summary>
public class CorrectiveActionStatusTransitionsTests
{
    [Fact]
    public void SubmitPlan_IsAllowed_FromDraftOnly()
    {
        Assert.True(CorrectiveActionStatusTransitions.IsAllowed(CorrectiveActionStatus.Draft, CorrectiveActionStatus.PendingPlanApproval));
        Assert.Contains(
            CorrectiveActionStatusTransitions.All,
            transition => transition.TransitionId == "ST-CA-002"
                && transition.From == CorrectiveActionStatus.Draft
                && transition.To == CorrectiveActionStatus.PendingPlanApproval);
    }

    [Fact]
    public void ApprovePlan_IsAllowed_FromPendingPlanApprovalOnly()
    {
        Assert.True(CorrectiveActionStatusTransitions.IsAllowed(CorrectiveActionStatus.PendingPlanApproval, CorrectiveActionStatus.Approved));
        Assert.Contains(
            CorrectiveActionStatusTransitions.All,
            transition => transition.TransitionId == "ST-CA-003"
                && transition.From == CorrectiveActionStatus.PendingPlanApproval
                && transition.To == CorrectiveActionStatus.Approved);
    }

    [Fact]
    public void Matrix_ContainsExactlyTheImplementedTransitions()
    {
        Assert.Equal(2, CorrectiveActionStatusTransitions.All.Count);
    }

    [Theory]
    [InlineData(CorrectiveActionStatus.PendingPlanApproval, CorrectiveActionStatus.Draft)]
    [InlineData(CorrectiveActionStatus.Approved, CorrectiveActionStatus.PendingPlanApproval)]
    [InlineData(CorrectiveActionStatus.Draft, CorrectiveActionStatus.Approved)]
    [InlineData(CorrectiveActionStatus.Draft, CorrectiveActionStatus.Draft)]
    public void TransitionOutsideMatrix_IsNotAllowed(CorrectiveActionStatus from, CorrectiveActionStatus to)
    {
        Assert.False(CorrectiveActionStatusTransitions.IsAllowed(from, to));
    }

    [Fact]
    public void Approved_IsTerminal_NoTransitionLeavesIt()
    {
        Assert.DoesNotContain(CorrectiveActionStatusTransitions.All, transition => transition.From == CorrectiveActionStatus.Approved);
    }
}
