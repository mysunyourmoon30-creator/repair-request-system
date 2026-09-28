using RepairRequest.Domain.Common;
using RepairRequest.Domain.WorkOrders;

namespace RepairRequest.Domain.Tests.WorkOrders;

/// <summary>
/// <see cref="WorkOrder.Close"/> (ST-WO-006; BR-08; `docs/13` §4.20) aggregate-local guard only. The data
/// prerequisites (Work Summary reviewed, customer ACCEPT on the current submission, reviewed Cost Summary) and
/// the Supervisor role/scope are the Application service's responsibility — these tests cover the state guard and
/// the mutation of WO-011/WO-012 (<see cref="WorkOrder.ClosedBy"/>/<see cref="WorkOrder.ClosedAt"/>) itself.
/// </summary>
public class WorkOrderCloseDomainTests
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

        if (status == WorkOrderStatus.CorrectiveActionRequired)
        {
            workOrder.Reject();
            return workOrder;
        }

        workOrder.Accept();
        if (status == WorkOrderStatus.Completed)
        {
            return workOrder;
        }

        workOrder.Close(Guid.NewGuid(), DateTime.UtcNow);
        if (status == WorkOrderStatus.Closed)
        {
            return workOrder;
        }

        throw new ArgumentOutOfRangeException(nameof(status), status, "This test helper does not construct that state.");
    }

    [Fact]
    public void ANewWorkOrder_HasNoClosedByOrClosedAt()
    {
        var workOrder = At(WorkOrderStatus.Completed);

        Assert.Null(workOrder.ClosedBy);
        Assert.Null(workOrder.ClosedAt);
    }

    [Fact]
    public void Close_FromCompleted_MovesToClosed_AndRecordsWhoAndWhen()
    {
        var workOrder = At(WorkOrderStatus.Completed);
        var supervisorId = Guid.NewGuid();
        var closedAt = new DateTime(2026, 9, 28, 10, 30, 0, DateTimeKind.Utc);

        workOrder.Close(supervisorId, closedAt);

        Assert.Equal(WorkOrderStatus.Closed, workOrder.Status);
        Assert.Equal(supervisorId, workOrder.ClosedBy);
        Assert.Equal(closedAt, workOrder.ClosedAt);
    }

    [Fact]
    public void Close_LeavesTheAcceptanceContactUntouched()
    {
        var workOrder = At(WorkOrderStatus.Completed);

        workOrder.Close(Guid.NewGuid(), DateTime.UtcNow);

        Assert.Equal(ContactId, workOrder.AcceptanceContactId);
        Assert.Equal(ContactSnapshot, workOrder.AcceptanceContactSnapshot);
    }

    [Theory]
    [InlineData(WorkOrderStatus.Open)]
    [InlineData(WorkOrderStatus.Scheduled)]
    [InlineData(WorkOrderStatus.InProgress)]
    [InlineData(WorkOrderStatus.AwaitingSupervisorReview)]
    [InlineData(WorkOrderStatus.AwaitingCustomerAcceptance)]
    [InlineData(WorkOrderStatus.CorrectiveActionRequired)]
    public void Close_FromAnyNonCompletedState_ThrowsWithoutMutating(WorkOrderStatus notCompleted)
    {
        var workOrder = At(notCompleted);

        Assert.Throws<DomainRuleViolationException>(() => workOrder.Close(Guid.NewGuid(), DateTime.UtcNow));

        Assert.Equal(notCompleted, workOrder.Status);
        Assert.Null(workOrder.ClosedBy);
        Assert.Null(workOrder.ClosedAt);
    }

    [Fact]
    public void Close_WhenAlreadyClosed_Throws_AndKeepsTheOriginalClosedByAndAt()
    {
        var workOrder = At(WorkOrderStatus.Completed);
        var firstSupervisor = Guid.NewGuid();
        var firstAt = new DateTime(2026, 9, 28, 10, 0, 0, DateTimeKind.Utc);
        workOrder.Close(firstSupervisor, firstAt);

        Assert.Throws<DomainRuleViolationException>(() => workOrder.Close(Guid.NewGuid(), firstAt.AddHours(1)));

        Assert.Equal(WorkOrderStatus.Closed, workOrder.Status);
        Assert.Equal(firstSupervisor, workOrder.ClosedBy);
        Assert.Equal(firstAt, workOrder.ClosedAt);
    }

    [Fact]
    public void Close_RequiresANonEmptyClosedBy_AndDoesNotMutate()
    {
        var workOrder = At(WorkOrderStatus.Completed);

        Assert.Throws<ArgumentException>(() => workOrder.Close(Guid.Empty, DateTime.UtcNow));

        Assert.Equal(WorkOrderStatus.Completed, workOrder.Status);
        Assert.Null(workOrder.ClosedBy);
        Assert.Null(workOrder.ClosedAt);
    }

    [Fact]
    public void Close_RequiresAUtcTimestamp_AndDoesNotMutate()
    {
        var workOrder = At(WorkOrderStatus.Completed);

        Assert.Throws<ArgumentException>(() => workOrder.Close(Guid.NewGuid(), DateTime.SpecifyKind(DateTime.Now, DateTimeKind.Local)));
        Assert.Throws<ArgumentException>(() => workOrder.Close(Guid.NewGuid(), DateTime.SpecifyKind(DateTime.Now, DateTimeKind.Unspecified)));

        Assert.Equal(WorkOrderStatus.Completed, workOrder.Status);
        Assert.Null(workOrder.ClosedAt);
    }

    [Fact]
    public void AClosedWorkOrder_RejectsEveryEarlierTransition()
    {
        // The "lock operations" half of ST-WO-006 at the aggregate level: once CLOSED, every earlier transition throws.
        var workOrder = At(WorkOrderStatus.Closed);

        Assert.Throws<DomainRuleViolationException>(() => workOrder.Accept());
        Assert.Throws<DomainRuleViolationException>(() => workOrder.Reject());
        Assert.Throws<DomainRuleViolationException>(() => workOrder.SubmitWorkSummary());
        Assert.Throws<DomainRuleViolationException>(() => workOrder.SubmitForAcceptance(Guid.NewGuid(), ContactSnapshot));
        Assert.Throws<DomainRuleViolationException>(() => workOrder.BeginWork());
        Assert.Throws<DomainRuleViolationException>(() => workOrder.Schedule(Guid.NewGuid()));
        Assert.Equal(WorkOrderStatus.Closed, workOrder.Status);
    }
}
