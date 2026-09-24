using RepairRequest.Domain.Common;
using RepairRequest.Domain.WorkOrders;

namespace RepairRequest.Domain.Tests.WorkOrders;

/// <summary>
/// <see cref="WorkOrder.SubmitWorkSummary"/> (ST-WO-003), <see cref="WorkOrder.SubmitForAcceptance"/> (ST-WO-004)
/// and <see cref="WorkOrder.Accept"/> (ST-WO-005) aggregate-local guards. Eligibility (Technician owns a
/// checked-out Visit; Team Lead/Supervisor site scope; the exact designated Acceptance Contact) and field
/// validation (Work Summary; Acceptance Contact eligibility) are the Application service's responsibility —
/// these tests cover only the state guard and the mutation of WO-008/WO-009 itself. `docs/13` §4.15/§4.16.
/// </summary>
public class WorkOrderWorkSummaryDomainTests
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

        workOrder.Accept();
        if (status == WorkOrderStatus.Completed)
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
    public void SubmitForAcceptance_FromAwaitingSupervisorReview_MovesToAwaitingCustomerAcceptance_AndSetsTheContact()
    {
        var workOrder = At(WorkOrderStatus.AwaitingSupervisorReview);

        workOrder.SubmitForAcceptance(ContactId, ContactSnapshot);

        Assert.Equal(WorkOrderStatus.AwaitingCustomerAcceptance, workOrder.Status);
        Assert.Equal(ContactId, workOrder.AcceptanceContactId);
        Assert.Equal(ContactSnapshot, workOrder.AcceptanceContactSnapshot);
    }

    [Theory]
    [InlineData(WorkOrderStatus.Open)]
    [InlineData(WorkOrderStatus.Scheduled)]
    [InlineData(WorkOrderStatus.InProgress)]
    [InlineData(WorkOrderStatus.AwaitingCustomerAcceptance)]
    [InlineData(WorkOrderStatus.Completed)]
    public void SubmitForAcceptance_FromAnyOtherState_ThrowsWithoutMutating(WorkOrderStatus notAwaitingReview)
    {
        var workOrder = At(notAwaitingReview);
        var previousContactId = workOrder.AcceptanceContactId;

        Assert.Throws<DomainRuleViolationException>(() => workOrder.SubmitForAcceptance(Guid.NewGuid(), "should not be applied"));
        Assert.Equal(notAwaitingReview, workOrder.Status);
        Assert.Equal(previousContactId, workOrder.AcceptanceContactId);
    }

    [Fact]
    public void SubmitForAcceptance_RequiresANonEmptyContactId() =>
        Assert.Throws<ArgumentException>(() => At(WorkOrderStatus.AwaitingSupervisorReview).SubmitForAcceptance(Guid.Empty, ContactSnapshot));

    [Fact]
    public void SubmitForAcceptance_RequiresANonBlankSnapshot() =>
        Assert.Throws<ArgumentException>(() => At(WorkOrderStatus.AwaitingSupervisorReview).SubmitForAcceptance(ContactId, " "));

    [Fact]
    public void Accept_FromAwaitingCustomerAcceptance_MovesToCompleted()
    {
        var workOrder = At(WorkOrderStatus.AwaitingCustomerAcceptance);

        workOrder.Accept();

        Assert.Equal(WorkOrderStatus.Completed, workOrder.Status);
    }

    [Theory]
    [InlineData(WorkOrderStatus.Open)]
    [InlineData(WorkOrderStatus.Scheduled)]
    [InlineData(WorkOrderStatus.InProgress)]
    [InlineData(WorkOrderStatus.AwaitingSupervisorReview)]
    [InlineData(WorkOrderStatus.Completed)]
    public void Accept_FromAnyOtherState_ThrowsWithoutMutating(WorkOrderStatus notAwaitingAcceptance)
    {
        var workOrder = At(notAwaitingAcceptance);

        Assert.Throws<DomainRuleViolationException>(() => workOrder.Accept());
        Assert.Equal(notAwaitingAcceptance, workOrder.Status);
    }
}
