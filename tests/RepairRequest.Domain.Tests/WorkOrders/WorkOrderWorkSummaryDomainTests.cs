using RepairRequest.Domain.Common;
using RepairRequest.Domain.WorkOrders;

namespace RepairRequest.Domain.Tests.WorkOrders;

/// <summary>
/// <see cref="WorkOrder.SubmitWorkSummary"/> (ST-WO-003) and <see cref="WorkOrder.SubmitForAcceptance"/>
/// (ST-WO-004) aggregate-local guards. Eligibility (Technician owns a checked-out Visit; Team Lead/Supervisor
/// site scope) and Work Summary field validation are the Application service's responsibility — these tests
/// cover only the state guard itself. `docs/13` §4.15. No public transition into COMPLETED exists yet (a later
/// ticket), so that state is not exercised here.
/// </summary>
public class WorkOrderWorkSummaryDomainTests
{
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

        workOrder.SubmitForAcceptance();
        if (status == WorkOrderStatus.AwaitingCustomerAcceptance)
        {
            return workOrder;
        }

        throw new ArgumentOutOfRangeException(nameof(status), status, "No public transition reaches this state yet.");
    }

    [Fact]
    public void SubmitWorkSummary_FromInProgress_MovesToAwaitingSupervisorReview()
    {
        var workOrder = At(WorkOrderStatus.InProgress);

        workOrder.SubmitWorkSummary();

        Assert.Equal(WorkOrderStatus.AwaitingSupervisorReview, workOrder.Status);
    }

    [Theory]
    [InlineData(WorkOrderStatus.Open)]
    [InlineData(WorkOrderStatus.Scheduled)]
    [InlineData(WorkOrderStatus.AwaitingSupervisorReview)]
    [InlineData(WorkOrderStatus.AwaitingCustomerAcceptance)]
    public void SubmitWorkSummary_FromAnyOtherState_ThrowsWithoutMutating(WorkOrderStatus notInProgress)
    {
        var workOrder = At(notInProgress);

        Assert.Throws<DomainRuleViolationException>(() => workOrder.SubmitWorkSummary());
        Assert.Equal(notInProgress, workOrder.Status);
    }

    [Fact]
    public void SubmitForAcceptance_FromAwaitingSupervisorReview_MovesToAwaitingCustomerAcceptance()
    {
        var workOrder = At(WorkOrderStatus.AwaitingSupervisorReview);

        workOrder.SubmitForAcceptance();

        Assert.Equal(WorkOrderStatus.AwaitingCustomerAcceptance, workOrder.Status);
    }

    [Theory]
    [InlineData(WorkOrderStatus.Open)]
    [InlineData(WorkOrderStatus.Scheduled)]
    [InlineData(WorkOrderStatus.InProgress)]
    [InlineData(WorkOrderStatus.AwaitingCustomerAcceptance)]
    public void SubmitForAcceptance_FromAnyOtherState_ThrowsWithoutMutating(WorkOrderStatus notAwaitingReview)
    {
        var workOrder = At(notAwaitingReview);

        Assert.Throws<DomainRuleViolationException>(() => workOrder.SubmitForAcceptance());
        Assert.Equal(notAwaitingReview, workOrder.Status);
    }
}
