using RepairRequest.Domain.Common;
using RepairRequest.Domain.WorkOrders;

namespace RepairRequest.Domain.Tests.WorkOrders;

/// <summary>S3-001 Check-in invariant: ST-WO-002 SCHEDULED -&gt; IN_PROGRESS only.</summary>
public class WorkOrderCheckInDomainTests
{
    private static readonly DateTime CreatedAt = new(2026, 9, 18, 8, 0, 0, DateTimeKind.Utc);

    private static WorkOrder WorkOrderIn(WorkOrderStatus status)
    {
        var workOrder = WorkOrder.Create(Guid.NewGuid(), Guid.NewGuid(), "WO-2026-000001", CreatedAt);
        if (status != WorkOrderStatus.Open)
        {
            typeof(WorkOrder).GetProperty(nameof(WorkOrder.Status))!.SetValue(workOrder, status);
        }

        return workOrder;
    }

    [Fact]
    public void BeginWork_FromScheduled_MovesToInProgress()
    {
        var workOrder = WorkOrderIn(WorkOrderStatus.Scheduled);

        workOrder.BeginWork();

        Assert.Equal(WorkOrderStatus.InProgress, workOrder.Status);
    }

    [Theory]
    [InlineData(WorkOrderStatus.Open)]
    [InlineData(WorkOrderStatus.InProgress)]
    [InlineData(WorkOrderStatus.AwaitingSupervisorReview)]
    [InlineData(WorkOrderStatus.AwaitingCustomerAcceptance)]
    [InlineData(WorkOrderStatus.Completed)]
    [InlineData(WorkOrderStatus.CorrectiveActionRequired)]
    [InlineData(WorkOrderStatus.CorrectivePlanPending)]
    [InlineData(WorkOrderStatus.CorrectivePlanApproved)]
    [InlineData(WorkOrderStatus.Closed)]
    [InlineData(WorkOrderStatus.Cancelled)]
    public void BeginWork_FromAnyStateOtherThanScheduled_IsDenied_AndChangesNothing(WorkOrderStatus status)
    {
        var workOrder = WorkOrderIn(status);

        Assert.Throws<DomainRuleViolationException>(() => workOrder.BeginWork());

        Assert.Equal(status, workOrder.Status);
    }
}
