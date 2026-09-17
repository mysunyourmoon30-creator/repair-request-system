using RepairRequest.Domain.WorkOrders;

namespace RepairRequest.Domain.Tests.WorkOrders;

/// <summary>
/// S2-001 persisted shape (RR-DD-001 WO-001..013). Only Create is implemented; Convert, Schedule, Reassign and
/// every later transition are out of scope (DEC-S2-001-05).
/// </summary>
public class WorkOrderTests
{
    private static readonly DateTime CreatedAt = new(2026, 9, 17, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Create_SetsIdentityAndStartsOpen_WithEveryOtherFieldEmpty()
    {
        var tenantId = Guid.NewGuid();
        var repairRequestId = Guid.NewGuid();

        var workOrder = WorkOrder.Create(tenantId, repairRequestId, "WO-2026-000001", CreatedAt);

        Assert.Equal(tenantId, workOrder.TenantId);
        Assert.Equal(repairRequestId, workOrder.RepairRequestId);
        Assert.Equal("WO-2026-000001", workOrder.WorkOrderNo);
        Assert.Equal(WorkOrderStatus.Open, workOrder.Status);
        Assert.Equal(CreatedAt, workOrder.CreatedAt);
        Assert.Null(workOrder.OwnerTeamId);
        Assert.Null(workOrder.TeamLeadId);
        Assert.Null(workOrder.AcceptanceContactId);
        Assert.Null(workOrder.AcceptanceContactSnapshot);
        Assert.Null(workOrder.CancelReason);
        Assert.Null(workOrder.ClosedBy);
        Assert.Null(workOrder.ClosedAt);
    }

    [Fact]
    public void Create_RequiresATenant() =>
        Assert.Throws<ArgumentException>(() => WorkOrder.Create(Guid.Empty, Guid.NewGuid(), "WO-1", CreatedAt));

    [Fact]
    public void Create_RequiresTheOriginatingRepairRequest_BecauseWO004IsAUniqueFK() =>
        Assert.Throws<ArgumentException>(() => WorkOrder.Create(Guid.NewGuid(), Guid.Empty, "WO-1", CreatedAt));

    [Fact]
    public void Create_RequiresAWorkOrderNo() =>
        Assert.Throws<ArgumentException>(() => WorkOrder.Create(Guid.NewGuid(), Guid.NewGuid(), " ", CreatedAt));

    [Fact]
    public void Create_RejectsANonUtcCreatedAt() =>
        Assert.Throws<ArgumentException>(() =>
            WorkOrder.Create(Guid.NewGuid(), Guid.NewGuid(), "WO-1", DateTime.SpecifyKind(CreatedAt, DateTimeKind.Local)));

    [Fact]
    public void Create_RejectsAWorkOrderNoLongerThanTheMaxLength() =>
        Assert.Throws<ArgumentException>(() =>
            WorkOrder.Create(Guid.NewGuid(), Guid.NewGuid(), new string('A', WorkOrder.WorkOrderNoMaxLength + 1), CreatedAt));
}
