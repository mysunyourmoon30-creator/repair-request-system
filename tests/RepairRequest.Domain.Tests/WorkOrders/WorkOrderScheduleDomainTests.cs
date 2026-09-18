using RepairRequest.Domain.Common;
using RepairRequest.Domain.WorkOrders;

namespace RepairRequest.Domain.Tests.WorkOrders;

/// <summary>S2-003 Schedule invariant: ST-WO-001 OPEN -&gt; SCHEDULED only, setting the required owner team (WO-006).</summary>
public class WorkOrderScheduleDomainTests
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
    public void Schedule_FromOpen_MovesToScheduled_AndSetsTheOwnerTeam()
    {
        var workOrder = WorkOrderIn(WorkOrderStatus.Open);
        var teamId = Guid.NewGuid();

        workOrder.Schedule(teamId);

        Assert.Equal(WorkOrderStatus.Scheduled, workOrder.Status);
        Assert.Equal(teamId, workOrder.OwnerTeamId);
    }

    [Fact]
    public void Schedule_RequiresANonEmptyTeam() =>
        Assert.Throws<ArgumentException>(() => WorkOrderIn(WorkOrderStatus.Open).Schedule(Guid.Empty));

    [Theory]
    [InlineData(WorkOrderStatus.Scheduled)]
    [InlineData(WorkOrderStatus.InProgress)]
    [InlineData(WorkOrderStatus.AwaitingSupervisorReview)]
    [InlineData(WorkOrderStatus.AwaitingCustomerAcceptance)]
    [InlineData(WorkOrderStatus.Completed)]
    [InlineData(WorkOrderStatus.CorrectiveActionRequired)]
    [InlineData(WorkOrderStatus.CorrectivePlanPending)]
    [InlineData(WorkOrderStatus.CorrectivePlanApproved)]
    [InlineData(WorkOrderStatus.Closed)]
    [InlineData(WorkOrderStatus.Cancelled)]
    public void Schedule_FromAnyStateOtherThanOpen_IsDenied_AndChangesNothing(WorkOrderStatus status)
    {
        var workOrder = WorkOrderIn(status);

        Assert.Throws<DomainRuleViolationException>(() => workOrder.Schedule(Guid.NewGuid()));

        Assert.Equal(status, workOrder.Status);
        Assert.Null(workOrder.OwnerTeamId);
    }
}
