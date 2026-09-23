using RepairRequest.Domain.Common;
using RepairRequest.Domain.WorkOrders;

namespace RepairRequest.Domain.Tests.WorkOrders;

/// <summary>
/// S3-004 Check-out invariants: ST-WS-004 CHECKED_IN -&gt; CHECKED_OUT only, and ST-SV-003 IN_PROGRESS -&gt;
/// COMPLETED only; a UTC (server) time on both; per the resolved Portfolio Project Owner scope decision, neither
/// guard enforces BR-06's summary/outcome/evidence half.
/// </summary>
public class WorkSessionCheckOutDomainTests
{
    private static readonly DateTime CheckInAt = new(2026, 9, 20, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime CheckOutAt = new(2026, 9, 20, 17, 0, 0, DateTimeKind.Utc);

    /// <summary>A persisted-looking session: <c>Id</c> is assigned on insert, so tests set it the way EF would.</summary>
    private static WorkSession SessionIn(WorkSessionStatus status)
    {
        var session = WorkSession.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), CheckInAt);
        typeof(WorkSession).GetProperty(nameof(WorkSession.Id))!.SetValue(session, Guid.NewGuid());
        if (status != WorkSessionStatus.CheckedIn)
        {
            typeof(WorkSession).GetProperty(nameof(WorkSession.Status))!.SetValue(session, status);
        }

        return session;
    }

    [Fact]
    public void CheckOut_FromCheckedIn_MovesToCheckedOut_AndSetsCheckOutAt()
    {
        var session = SessionIn(WorkSessionStatus.CheckedIn);

        session.CheckOut(CheckOutAt);

        Assert.Equal(WorkSessionStatus.CheckedOut, session.Status);
        Assert.Equal(CheckOutAt, session.CheckOutAt);
        Assert.Equal(CheckInAt, session.CheckInAt);
        Assert.Null(session.PauseStartAt);
        Assert.Null(session.ResumeAt);
    }

    [Fact]
    public void CheckOut_RequiresAUtcTime_AndChangesNothing()
    {
        var session = SessionIn(WorkSessionStatus.CheckedIn);

        Assert.Throws<ArgumentException>(() => session.CheckOut(DateTime.SpecifyKind(CheckOutAt, DateTimeKind.Unspecified)));

        Assert.Equal(WorkSessionStatus.CheckedIn, session.Status);
        Assert.Null(session.CheckOutAt);
    }

    [Theory]
    [InlineData(WorkSessionStatus.Paused)]
    [InlineData(WorkSessionStatus.CheckedOut)]
    public void CheckOut_FromAnyStateOtherThanCheckedIn_IsDenied_AndChangesNothing(WorkSessionStatus status)
    {
        var session = SessionIn(status);

        Assert.Throws<DomainRuleViolationException>(() => session.CheckOut(CheckOutAt));

        Assert.Equal(status, session.Status);
        Assert.Null(session.CheckOutAt);
    }
}

/// <summary>
/// S3-004 Check-out invariants on the Service Visit side: ST-SV-003 IN_PROGRESS -&gt; COMPLETED only, server time.
/// </summary>
public class ServiceVisitCheckOutDomainTests
{
    private static readonly DateTime ScheduledStartAt = new(2026, 9, 20, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime ScheduledEndAt = new(2026, 9, 20, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime CompletedAt = new(2026, 9, 20, 17, 0, 0, DateTimeKind.Utc);

    private static ServiceVisit VisitIn(ServiceVisitStatus status)
    {
        var visit = ServiceVisit.Create(
            Guid.NewGuid(), Guid.NewGuid(), ServiceVisitType.Initial, Guid.NewGuid(), Guid.NewGuid(), ScheduledStartAt, ScheduledEndAt);
        typeof(ServiceVisit).GetProperty(nameof(ServiceVisit.Id))!.SetValue(visit, Guid.NewGuid());
        if (status != ServiceVisitStatus.Scheduled)
        {
            typeof(ServiceVisit).GetProperty(nameof(ServiceVisit.Status))!.SetValue(visit, status);
        }

        return visit;
    }

    [Fact]
    public void CheckOut_FromInProgress_MovesToCompleted_AndSetsCompletedAt()
    {
        var visit = VisitIn(ServiceVisitStatus.InProgress);

        visit.CheckOut(CompletedAt);

        Assert.Equal(ServiceVisitStatus.Completed, visit.Status);
        Assert.Equal(CompletedAt, visit.CompletedAt);
    }

    [Fact]
    public void CheckOut_RequiresAUtcTime_AndChangesNothing()
    {
        var visit = VisitIn(ServiceVisitStatus.InProgress);

        Assert.Throws<ArgumentException>(() => visit.CheckOut(DateTime.SpecifyKind(CompletedAt, DateTimeKind.Unspecified)));

        Assert.Equal(ServiceVisitStatus.InProgress, visit.Status);
        Assert.Null(visit.CompletedAt);
    }

    [Theory]
    [InlineData(ServiceVisitStatus.Scheduled)]
    [InlineData(ServiceVisitStatus.Completed)]
    [InlineData(ServiceVisitStatus.Cancelled)]
    [InlineData(ServiceVisitStatus.Missed)]
    public void CheckOut_FromAnyStateOtherThanInProgress_IsDenied_AndChangesNothing(ServiceVisitStatus status)
    {
        var visit = VisitIn(status);

        Assert.Throws<DomainRuleViolationException>(() => visit.CheckOut(CompletedAt));

        Assert.Equal(status, visit.Status);
        Assert.Null(visit.CompletedAt);
    }
}
