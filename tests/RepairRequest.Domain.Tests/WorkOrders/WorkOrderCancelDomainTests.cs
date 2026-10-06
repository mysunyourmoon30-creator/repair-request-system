using RepairRequest.Domain.Common;
using RepairRequest.Domain.WorkOrders;

namespace RepairRequest.Domain.Tests.WorkOrders;

/// <summary>
/// <see cref="WorkOrder.Cancel"/> (ST-WO-011; UC-WO-026; BR-09/BR-12; `docs/13` §4.25) aggregate-local guard only.
/// The Supervisor role/scope and the "no Service Visit currently IN_PROGRESS" guard are the Application service's
/// responsibility — these tests cover the state guard (every non-terminal, pre-Accept source state) and the
/// mutation of WO-010 (<see cref="WorkOrder.CancelReason"/>) itself.
/// </summary>
public class WorkOrderCancelDomainTests
{
    private static readonly Guid ContactId = Guid.NewGuid();
    private const string ContactSnapshot = "{\"displayName\":\"req\",\"email\":\"req@example.test\"}";

    private static WorkOrder At(WorkOrderStatus status)
    {
        var workOrder = WorkOrder.Create(Guid.NewGuid(), Guid.NewGuid(), "WO-1", DateTime.UtcNow);
        if (status == WorkOrderStatus.Open)
        {
            return workOrder;
        }

        workOrder.Schedule(Guid.NewGuid());
        if (status == WorkOrderStatus.Scheduled)
        {
            return workOrder;
        }

        workOrder.BeginWork();
        if (status == WorkOrderStatus.InProgress)
        {
            return workOrder;
        }

        workOrder.SubmitWorkSummary();
        if (status == WorkOrderStatus.AwaitingSupervisorReview)
        {
            return workOrder;
        }

        workOrder.SubmitForAcceptance(ContactId, ContactSnapshot);
        if (status == WorkOrderStatus.AwaitingCustomerAcceptance)
        {
            return workOrder;
        }

        if (status == WorkOrderStatus.Completed)
        {
            workOrder.Accept();
            return workOrder;
        }

        workOrder.Reject();
        if (status == WorkOrderStatus.CorrectiveActionRequired)
        {
            return workOrder;
        }

        workOrder.SubmitCorrectivePlan();
        if (status == WorkOrderStatus.CorrectivePlanPending)
        {
            return workOrder;
        }

        workOrder.ApproveCorrectivePlan();
        if (status == WorkOrderStatus.CorrectivePlanApproved)
        {
            return workOrder;
        }

        throw new ArgumentOutOfRangeException(nameof(status), status, "This test helper does not construct that state.");
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
    public void Cancel_FromEveryNonTerminalPreAcceptState_MovesToCancelled_AndRecordsTheReason(WorkOrderStatus from)
    {
        var workOrder = At(from);

        workOrder.Cancel("No longer needed.");

        Assert.Equal(WorkOrderStatus.Cancelled, workOrder.Status);
        Assert.Equal("No longer needed.", workOrder.CancelReason);
    }

    [Fact]
    public void Cancel_FromCompleted_Throws_AndDoesNotMutate()
    {
        var workOrder = At(WorkOrderStatus.Completed);

        Assert.Throws<DomainRuleViolationException>(() => workOrder.Cancel("Too late."));

        Assert.Equal(WorkOrderStatus.Completed, workOrder.Status);
        Assert.Null(workOrder.CancelReason);
    }

    [Fact]
    public void Cancel_WhenAlreadyClosed_Throws_AndDoesNotMutate()
    {
        var workOrder = At(WorkOrderStatus.Completed);
        workOrder.Close(Guid.NewGuid(), DateTime.UtcNow);

        Assert.Throws<DomainRuleViolationException>(() => workOrder.Cancel("Too late."));

        Assert.Equal(WorkOrderStatus.Closed, workOrder.Status);
        Assert.Null(workOrder.CancelReason);
    }

    [Fact]
    public void Cancel_WhenAlreadyCancelled_Throws_AndKeepsTheOriginalReason()
    {
        var workOrder = At(WorkOrderStatus.Open);
        workOrder.Cancel("First reason.");

        Assert.Throws<DomainRuleViolationException>(() => workOrder.Cancel("Second attempt."));

        Assert.Equal(WorkOrderStatus.Cancelled, workOrder.Status);
        Assert.Equal("First reason.", workOrder.CancelReason);
    }

    [Fact]
    public void Cancel_RequiresANonEmptyReason_AndDoesNotMutate()
    {
        var workOrder = At(WorkOrderStatus.Open);

        Assert.Throws<ArgumentException>(() => workOrder.Cancel(string.Empty));
        Assert.Throws<ArgumentException>(() => workOrder.Cancel("   "));

        Assert.Equal(WorkOrderStatus.Open, workOrder.Status);
        Assert.Null(workOrder.CancelReason);
    }

    [Fact]
    public void Cancel_LeavesTheAcceptanceContactUntouched()
    {
        var workOrder = At(WorkOrderStatus.AwaitingCustomerAcceptance);

        workOrder.Cancel("No longer needed.");

        Assert.Equal(ContactId, workOrder.AcceptanceContactId);
        Assert.Equal(ContactSnapshot, workOrder.AcceptanceContactSnapshot);
    }

    [Fact]
    public void ACancelledWorkOrder_RejectsEveryEarlierTransition()
    {
        var workOrder = At(WorkOrderStatus.Open);
        workOrder.Cancel("No longer needed.");

        Assert.Throws<DomainRuleViolationException>(() => workOrder.Schedule(Guid.NewGuid()));
        Assert.Throws<DomainRuleViolationException>(() => workOrder.BeginWork());
        Assert.Throws<DomainRuleViolationException>(() => workOrder.SubmitWorkSummary());
        Assert.Throws<DomainRuleViolationException>(() => workOrder.SubmitForAcceptance(Guid.NewGuid(), ContactSnapshot));
        Assert.Throws<DomainRuleViolationException>(() => workOrder.Accept());
        Assert.Throws<DomainRuleViolationException>(() => workOrder.Reject());
        Assert.Throws<DomainRuleViolationException>(() => workOrder.Close(Guid.NewGuid(), DateTime.UtcNow));
        Assert.Equal(WorkOrderStatus.Cancelled, workOrder.Status);
    }
}
