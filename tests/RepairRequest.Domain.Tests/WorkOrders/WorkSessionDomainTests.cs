using RepairRequest.Domain.WorkOrders;

namespace RepairRequest.Domain.Tests.WorkOrders;

/// <summary>S3-001 Work Session invariant: ST-WS-001 Create always starts CHECKED_IN.</summary>
public class WorkSessionDomainTests
{
    private static readonly DateTime CheckInAt = new(2026, 9, 20, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Create_SetsFieldsAndStartsCheckedIn()
    {
        var tenantId = Guid.NewGuid();
        var serviceVisitId = Guid.NewGuid();
        var technicianId = Guid.NewGuid();

        var session = WorkSession.Create(tenantId, serviceVisitId, technicianId, CheckInAt);

        Assert.Equal(tenantId, session.TenantId);
        Assert.Equal(serviceVisitId, session.ServiceVisitId);
        Assert.Equal(technicianId, session.TechnicianId);
        Assert.Equal(WorkSessionStatus.CheckedIn, session.Status);
        Assert.Equal(CheckInAt, session.CheckInAt);
        Assert.Null(session.PauseStartAt);
        Assert.Null(session.ResumeAt);
        Assert.Null(session.CheckOutAt);
    }

    [Fact]
    public void Create_RequiresANonEmptyServiceVisitId() =>
        Assert.Throws<ArgumentException>(() => WorkSession.Create(Guid.NewGuid(), Guid.Empty, Guid.NewGuid(), CheckInAt));

    [Fact]
    public void Create_RequiresANonEmptyTechnicianId() =>
        Assert.Throws<ArgumentException>(() => WorkSession.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.Empty, CheckInAt));

    [Fact]
    public void Create_RequiresUtcCheckInTime() =>
        Assert.Throws<ArgumentException>(() => WorkSession.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTime.SpecifyKind(CheckInAt, DateTimeKind.Unspecified)));
}
