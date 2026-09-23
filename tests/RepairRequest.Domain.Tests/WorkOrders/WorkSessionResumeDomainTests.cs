using RepairRequest.Domain.Common;
using RepairRequest.Domain.WorkOrders;

namespace RepairRequest.Domain.Tests.WorkOrders;

/// <summary>
/// S3-003 Resume invariants: ST-WS-003 PAUSED -&gt; CHECKED_IN only; the open pause period closes
/// (<see cref="WorkSessionPause.ResumedAt"/>) without ever rewriting its <see cref="WorkSessionPause.PausedAt"/>
/// or <see cref="WorkSessionPause.PauseReason"/>; a UTC (server) time no earlier than the pause started; a second
/// Pause after Resume is its own new period row.
/// </summary>
public class WorkSessionResumeDomainTests
{
    private static readonly DateTime CheckInAt = new(2026, 9, 20, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime PausedAt = new(2026, 9, 20, 9, 30, 0, DateTimeKind.Utc);
    private static readonly DateTime ResumedAt = new(2026, 9, 20, 10, 0, 0, DateTimeKind.Utc);

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

    private static (WorkSession Session, WorkSessionPause Pause) PausedSession()
    {
        var session = SessionIn(WorkSessionStatus.CheckedIn);
        var pause = session.Pause("Waiting for spare part", PausedAt);
        return (session, pause);
    }

    [Fact]
    public void Resume_FromPaused_MovesToCheckedIn_ClosesThePause_AndClearsPauseStart()
    {
        var (session, pause) = PausedSession();

        session.Resume(pause, ResumedAt);

        Assert.Equal(WorkSessionStatus.CheckedIn, session.Status);
        Assert.Null(session.PauseStartAt);
        Assert.Equal(ResumedAt, session.ResumeAt);
        Assert.Equal(CheckInAt, session.CheckInAt);
        Assert.Null(session.CheckOutAt);

        Assert.Equal(ResumedAt, pause.ResumedAt);
        Assert.Equal(PausedAt, pause.PausedAt);
        Assert.Equal("Waiting for spare part", pause.PauseReason);
    }

    [Fact]
    public void Resume_RequiresAUtcTime_AndChangesNothing()
    {
        var (session, pause) = PausedSession();

        Assert.Throws<ArgumentException>(() => session.Resume(pause, DateTime.SpecifyKind(ResumedAt, DateTimeKind.Unspecified)));

        Assert.Equal(WorkSessionStatus.Paused, session.Status);
        Assert.Equal(PausedAt, session.PauseStartAt);
        Assert.Null(session.ResumeAt);
        Assert.Null(pause.ResumedAt);
    }

    [Fact]
    public void Resume_RejectsATimeBeforeThePauseStarted_AndChangesNothing()
    {
        var (session, pause) = PausedSession();

        Assert.Throws<ArgumentException>(() => session.Resume(pause, PausedAt.AddMinutes(-1)));

        Assert.Equal(WorkSessionStatus.Paused, session.Status);
        Assert.Null(pause.ResumedAt);
    }

    [Fact]
    public void Resume_RejectsANullPause()
    {
        var session = SessionIn(WorkSessionStatus.Paused);

        Assert.Throws<ArgumentNullException>(() => session.Resume(null!, ResumedAt));
    }

    [Theory]
    [InlineData(WorkSessionStatus.CheckedIn)]
    [InlineData(WorkSessionStatus.CheckedOut)]
    public void Resume_FromAnyStateOtherThanPaused_IsDenied_AndChangesNothing(WorkSessionStatus status)
    {
        var session = SessionIn(status);
        var pause = SessionIn(WorkSessionStatus.CheckedIn).Pause("reason", PausedAt);

        Assert.Throws<DomainRuleViolationException>(() => session.Resume(pause, ResumedAt));

        Assert.Equal(status, session.Status);
        Assert.Null(session.ResumeAt);
        Assert.Null(pause.ResumedAt);
    }

    [Fact]
    public void ASecondResume_OfAnAlreadyResumedSession_IsDenied_AndKeepsTheResumeFacts()
    {
        var (session, pause) = PausedSession();
        session.Resume(pause, ResumedAt);

        Assert.Throws<DomainRuleViolationException>(() => session.Resume(pause, ResumedAt.AddMinutes(5)));

        Assert.Equal(WorkSessionStatus.CheckedIn, session.Status);
        Assert.Equal(ResumedAt, session.ResumeAt);
        Assert.Equal(ResumedAt, pause.ResumedAt);
    }

    [Fact]
    public void PauseResumePause_EachCycleIsItsOwnPeriod_AndEarlierHistoryIsNeverRewritten()
    {
        var (session, firstPause) = PausedSession();
        session.Resume(firstPause, ResumedAt);

        var secondPause = session.Pause("Second interruption", ResumedAt.AddMinutes(30));

        Assert.Equal(WorkSessionStatus.Paused, session.Status);
        Assert.NotSame(firstPause, secondPause);
        Assert.Equal(PausedAt, firstPause.PausedAt);
        Assert.Equal(ResumedAt, firstPause.ResumedAt);
        Assert.Equal("Waiting for spare part", firstPause.PauseReason);
        Assert.Equal(ResumedAt.AddMinutes(30), secondPause.PausedAt);
        Assert.Equal("Second interruption", secondPause.PauseReason);
        Assert.Null(secondPause.ResumedAt);
    }
}
