using RepairRequest.Domain.Common;
using RepairRequest.Domain.WorkOrders;

namespace RepairRequest.Domain.Tests.WorkOrders;

/// <summary>
/// S3-002 Pause invariants: ST-WS-002 CHECKED_IN -&gt; PAUSED only; a non-blank reason; a UTC (server) time; the
/// pause is a new period row and never replaces earlier history.
/// </summary>
public class WorkSessionPauseDomainTests
{
    private static readonly DateTime CheckInAt = new(2026, 9, 20, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime PausedAt = new(2026, 9, 20, 9, 30, 0, DateTimeKind.Utc);

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
    public void Pause_FromCheckedIn_MovesToPaused_SetsPauseStart_AndReturnsANewPeriod()
    {
        var session = SessionIn(WorkSessionStatus.CheckedIn);

        var pause = session.Pause("Waiting for spare part", PausedAt);

        Assert.Equal(WorkSessionStatus.Paused, session.Status);
        Assert.Equal(PausedAt, session.PauseStartAt);
        Assert.Equal(CheckInAt, session.CheckInAt);
        Assert.Null(session.ResumeAt);
        Assert.Null(session.CheckOutAt);

        Assert.Equal(session.Id, pause.WorkSessionId);
        Assert.Equal(session.TenantId, pause.TenantId);
        Assert.Equal(PausedAt, pause.PausedAt);
        Assert.Equal("Waiting for spare part", pause.PauseReason);
        Assert.Null(pause.ResumedAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t \n")]
    public void Pause_RequiresANonBlankReason_AndChangesNothing(string? reason)
    {
        var session = SessionIn(WorkSessionStatus.CheckedIn);

        Assert.Throws<ArgumentException>(() => session.Pause(reason!, PausedAt));

        Assert.Equal(WorkSessionStatus.CheckedIn, session.Status);
        Assert.Null(session.PauseStartAt);
    }

    [Fact]
    public void Pause_RejectsAReasonLongerThanTheMaximum_AndChangesNothing()
    {
        var session = SessionIn(WorkSessionStatus.CheckedIn);

        Assert.Throws<ArgumentException>(() => session.Pause(new string('x', WorkSessionPause.ReasonMaxLength + 1), PausedAt));
        Assert.Equal(WorkSessionStatus.CheckedIn, session.Status);

        session.Pause(new string('x', WorkSessionPause.ReasonMaxLength), PausedAt);
        Assert.Equal(WorkSessionStatus.Paused, session.Status);
    }

    [Fact]
    public void Pause_RequiresAUtcTime_AndChangesNothing()
    {
        var session = SessionIn(WorkSessionStatus.CheckedIn);

        Assert.Throws<ArgumentException>(() => session.Pause("reason", DateTime.SpecifyKind(PausedAt, DateTimeKind.Unspecified)));

        Assert.Equal(WorkSessionStatus.CheckedIn, session.Status);
        Assert.Null(session.PauseStartAt);
    }

    [Theory]
    [InlineData(WorkSessionStatus.Paused)]
    [InlineData(WorkSessionStatus.CheckedOut)]
    public void Pause_FromAnyStateOtherThanCheckedIn_IsDenied_AndChangesNothing(WorkSessionStatus status)
    {
        var session = SessionIn(status);

        Assert.Throws<DomainRuleViolationException>(() => session.Pause("reason", PausedAt));

        Assert.Equal(status, session.Status);
        Assert.Null(session.PauseStartAt);
    }

    [Fact]
    public void ASecondPause_OfAnAlreadyPausedSession_IsDenied_AndKeepsTheFirstPausesFacts()
    {
        var session = SessionIn(WorkSessionStatus.CheckedIn);
        var first = session.Pause("first reason", PausedAt);

        Assert.Throws<DomainRuleViolationException>(() => session.Pause("second reason", PausedAt.AddMinutes(5)));

        Assert.Equal(PausedAt, session.PauseStartAt);
        Assert.Equal("first reason", first.PauseReason);
        Assert.Equal(PausedAt, first.PausedAt);
    }
}
