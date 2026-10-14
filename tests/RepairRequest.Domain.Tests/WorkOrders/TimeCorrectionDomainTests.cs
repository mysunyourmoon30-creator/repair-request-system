using System.Reflection;
using RepairRequest.Domain.Common;
using RepairRequest.Domain.WorkOrders;

namespace RepairRequest.Domain.Tests.WorkOrders;

/// <summary>
/// Time Correction on the Work Session side (ST-TC-001..003; UC-TIME-001; `docs/17`): the chronology rules, exact pause targeting,
/// what an approved correction changes and what it leaves alone, and the correction aggregate's own lifecycle.
/// </summary>
public class TimeCorrectionDomainTests
{
    private static readonly DateTime T0 = new(2026, 10, 12, 8, 0, 0, DateTimeKind.Utc);

    private static DateTime At(int minutes) => T0.AddMinutes(minutes);

    private sealed record Scenario(WorkSession Session, List<WorkSessionPause> Pauses);

    /// <summary>A session checked in at T0 with the given closed pauses (start, end in minutes), optionally one open pause and a check-out.</summary>
    private static Scenario Build((int Start, int End)[] closed, int? openAt = null, int? checkOutAt = null)
    {
        var session = WorkSession.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), T0);
        typeof(WorkSession).GetProperty(nameof(WorkSession.Id))!.SetValue(session, Guid.NewGuid());
        var pauses = new List<WorkSessionPause>();

        WorkSessionPause Open(int at)
        {
            var pause = session.Pause("Waiting for a part", At(at));
            typeof(WorkSessionPause).GetProperty(nameof(WorkSessionPause.Id))!.SetValue(pause, Guid.NewGuid());
            pauses.Add(pause);
            return pause;
        }

        foreach (var (start, end) in closed)
        {
            session.Resume(Open(start), At(end));
        }

        if (openAt is { } open)
        {
            Open(open);
        }

        if (checkOutAt is { } checkOut)
        {
            session.CheckOut(At(checkOut));
        }

        return new Scenario(session, pauses);
    }

    // ---------------- WorkSessionChronology ----------------

    [Fact]
    public void Chronology_AcceptsAnOrderedSession_AndEqualInstants()
    {
        Assert.Null(WorkSessionChronology.Violation(At(0), At(100), [new PausePeriod(Guid.NewGuid(), At(10), At(20)), new PausePeriod(Guid.NewGuid(), At(20), At(30))]));
        Assert.Null(WorkSessionChronology.Violation(At(0), At(0), []));
        Assert.Null(WorkSessionChronology.Violation(At(0), null, [new PausePeriod(Guid.NewGuid(), At(0), null)]));
    }

    [Fact]
    public void Chronology_RejectsEveryBrokenInterval()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        Assert.NotNull(WorkSessionChronology.Violation(At(10), At(5), []));                                              // check-out before check-in
        Assert.NotNull(WorkSessionChronology.Violation(At(10), null, [new PausePeriod(a, At(5), At(20))]));              // pause before check-in
        Assert.NotNull(WorkSessionChronology.Violation(At(0), null, [new PausePeriod(a, At(20), At(10))]));              // pause ends before it starts
        Assert.NotNull(WorkSessionChronology.Violation(At(0), At(50), [new PausePeriod(a, At(10), At(60))]));            // resume after check-out
        Assert.NotNull(WorkSessionChronology.Violation(At(0), At(50), [new PausePeriod(a, At(60), At(70))]));            // pause after check-out
        Assert.NotNull(WorkSessionChronology.Violation(At(0), At(50), [new PausePeriod(a, At(10), null)]));              // open pause on a checked-out session
        Assert.NotNull(WorkSessionChronology.Violation(At(0), null, [new PausePeriod(a, At(10), At(30)), new PausePeriod(b, At(20), At(40))])); // overlap
        Assert.NotNull(WorkSessionChronology.Violation(At(0), null, [new PausePeriod(a, At(10), null), new PausePeriod(b, At(20), At(30))]));   // next pause while one is open
    }

    // ---------------- targets and current values ----------------

    [Fact]
    public void HasTimestamp_FollowsTheExactTarget()
    {
        var open = Build([(10, 20)], openAt: 30);
        var closedPause = open.Pauses[0];
        var openPause = open.Pauses[1];
        var otherSession = Build([(10, 20)]);

        Assert.True(open.Session.HasTimestamp(TimeCorrectionTimestampType.CheckIn, null));
        Assert.False(open.Session.HasTimestamp(TimeCorrectionTimestampType.CheckOut, null));
        Assert.True(open.Session.HasTimestamp(TimeCorrectionTimestampType.PauseStart, closedPause));
        Assert.True(open.Session.HasTimestamp(TimeCorrectionTimestampType.PauseStart, openPause));
        Assert.True(open.Session.HasTimestamp(TimeCorrectionTimestampType.Resume, closedPause));
        Assert.False(open.Session.HasTimestamp(TimeCorrectionTimestampType.Resume, openPause));               // an open pause has no resume yet
        Assert.False(open.Session.HasTimestamp(TimeCorrectionTimestampType.PauseStart, null));
        Assert.False(open.Session.HasTimestamp(TimeCorrectionTimestampType.PauseStart, otherSession.Pauses[0])); // another session's pause

        Assert.Equal(At(10), open.Session.CurrentTimestamp(TimeCorrectionTimestampType.PauseStart, closedPause));
        Assert.Equal(At(20), open.Session.CurrentTimestamp(TimeCorrectionTimestampType.Resume, closedPause));
        Assert.Throws<InvalidOperationException>(() => open.Session.CurrentTimestamp(TimeCorrectionTimestampType.Resume, openPause));
    }

    // ---------------- applying a correction ----------------

    [Fact]
    public void ApplyCheckIn_ChangesOnlyTheCheckInColumn_AndNeverTheStatus()
    {
        var s = Build([(10, 20)], checkOutAt: 60);
        var status = s.Session.Status;

        s.Session.ApplyTimeCorrection(TimeCorrectionTimestampType.CheckIn, At(-5), null, s.Pauses);

        Assert.Equal(At(-5), s.Session.CheckInAt);
        Assert.Equal(At(60), s.Session.CheckOutAt);
        Assert.Equal(status, s.Session.Status);
        Assert.Equal((At(10), At(20)), (s.Pauses[0].PausedAt, s.Pauses[0].ResumedAt));
    }

    [Fact]
    public void ApplyCheckIn_ThatWouldPassTheFirstPause_IsRefused_AndChangesNothing()
    {
        var s = Build([(10, 20)]);

        Assert.Throws<DomainRuleViolationException>(() => s.Session.ApplyTimeCorrection(TimeCorrectionTimestampType.CheckIn, At(15), null, s.Pauses));

        Assert.Equal(T0, s.Session.CheckInAt);
    }

    [Fact]
    public void ApplyCheckOut_ChangesOnlyTheCheckOutColumn_AndRefusesAValueBeforeTheLastResume()
    {
        var s = Build([(10, 20)], checkOutAt: 60);

        s.Session.ApplyTimeCorrection(TimeCorrectionTimestampType.CheckOut, At(45), null, s.Pauses);
        Assert.Equal(At(45), s.Session.CheckOutAt);

        Assert.Throws<DomainRuleViolationException>(() => s.Session.ApplyTimeCorrection(TimeCorrectionTimestampType.CheckOut, At(15), null, s.Pauses));
        Assert.Equal(At(45), s.Session.CheckOutAt);

        var notCheckedOut = Build([(10, 20)]);
        Assert.Throws<DomainRuleViolationException>(() => notCheckedOut.Session.ApplyTimeCorrection(TimeCorrectionTimestampType.CheckOut, At(45), null, notCheckedOut.Pauses));
    }

    [Fact]
    public void ApplyPauseStart_OfAnOlderClosedPause_CorrectsThatRowOnly_AndLeavesTheLatestEventColumnsAlone()
    {
        var s = Build([(10, 20), (30, 40)]);
        var older = s.Pauses[0];
        var latestResume = s.Session.ResumeAt;

        s.Session.ApplyTimeCorrection(TimeCorrectionTimestampType.PauseStart, At(12), older, s.Pauses);

        Assert.Equal(At(12), older.PausedAt);
        Assert.Equal(At(20), older.ResumedAt);
        Assert.Equal((At(30), At(40)), (s.Pauses[1].PausedAt, s.Pauses[1].ResumedAt));
        Assert.Null(s.Session.PauseStartAt);                  // no pause is open: nothing to mirror
        Assert.Equal(latestResume, s.Session.ResumeAt);
    }

    [Fact]
    public void ApplyPauseStart_OfTheOpenPause_AlsoMirrorsPauseStartAt()
    {
        var s = Build([(10, 20)], openAt: 30);
        var open = s.Pauses[1];

        s.Session.ApplyTimeCorrection(TimeCorrectionTimestampType.PauseStart, At(25), open, s.Pauses);

        Assert.Equal(At(25), open.PausedAt);
        Assert.Equal(At(25), s.Session.PauseStartAt);
        Assert.Equal(WorkSessionStatus.Paused, s.Session.Status);
    }

    [Fact]
    public void ApplyResume_OfTheLatestClosedPause_MirrorsResumeAt_ButAnOlderOneDoesNot()
    {
        var s = Build([(10, 20), (30, 40)]);

        s.Session.ApplyTimeCorrection(TimeCorrectionTimestampType.Resume, At(22), s.Pauses[0], s.Pauses);
        Assert.Equal(At(22), s.Pauses[0].ResumedAt);
        Assert.Equal(At(40), s.Session.ResumeAt);             // the older pause is not the latest event

        s.Session.ApplyTimeCorrection(TimeCorrectionTimestampType.Resume, At(45), s.Pauses[1], s.Pauses);
        Assert.Equal(At(45), s.Pauses[1].ResumedAt);
        Assert.Equal(At(45), s.Session.ResumeAt);
    }

    [Fact]
    public void ApplyPauseCorrections_ThatOverlapTheNeighbouringPause_AreRefused_AndChangeNothing()
    {
        var s = Build([(10, 20), (30, 40)]);

        Assert.Throws<DomainRuleViolationException>(() => s.Session.ApplyTimeCorrection(TimeCorrectionTimestampType.PauseStart, At(15), s.Pauses[1], s.Pauses)); // starts before the earlier pause ended? 15 < 20
        Assert.Throws<DomainRuleViolationException>(() => s.Session.ApplyTimeCorrection(TimeCorrectionTimestampType.Resume, At(35), s.Pauses[0], s.Pauses));    // ends after the next one started
        Assert.Throws<DomainRuleViolationException>(() => s.Session.ApplyTimeCorrection(TimeCorrectionTimestampType.Resume, At(5), s.Pauses[0], s.Pauses));     // ends before it started

        Assert.Equal((At(10), At(20)), (s.Pauses[0].PausedAt, s.Pauses[0].ResumedAt));
        Assert.Equal((At(30), At(40)), (s.Pauses[1].PausedAt, s.Pauses[1].ResumedAt));
    }

    [Fact]
    public void ApplyingToAnUnknownTarget_IsRefused()
    {
        var s = Build([(10, 20)], openAt: 30);

        Assert.Throws<DomainRuleViolationException>(() => s.Session.ApplyTimeCorrection(TimeCorrectionTimestampType.Resume, At(35), s.Pauses[1], s.Pauses));    // open pause: no resume yet
        Assert.Throws<DomainRuleViolationException>(() => s.Session.ApplyTimeCorrection(TimeCorrectionTimestampType.PauseStart, At(5), null, s.Pauses));          // no pause named
    }

    [Fact]
    public void TheCorrectionPathExposesNoWayToChangeTheStatusOrTheReason()
    {
        Assert.False(typeof(WorkSession).GetProperty(nameof(WorkSession.Status))!.SetMethod?.IsPublic ?? false);
        Assert.False(typeof(WorkSessionPause).GetProperty(nameof(WorkSessionPause.PauseReason))!.SetMethod?.IsPublic ?? false);
        Assert.All(
            typeof(WorkSessionPause).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly).Where(method => !method.IsSpecialName),
            method => Assert.Fail($"WorkSessionPause exposes a public mutator: {method.Name}"));
    }

    // ---------------- the correction aggregate ----------------

    private static TimeCorrection Request(TimeCorrectionTimestampType type, Guid? pauseId, string reason = "Clock was wrong.") =>
        TimeCorrection.Request(Guid.NewGuid(), Guid.NewGuid(), pauseId, type, At(10), At(12), reason, Guid.NewGuid());

    [Theory]
    [InlineData(TimeCorrectionTimestampType.PauseStart)]
    [InlineData(TimeCorrectionTimestampType.Resume)]
    public void Request_ForAPauseType_RequiresTheExactPause(TimeCorrectionTimestampType type)
    {
        Assert.Throws<ArgumentException>(() => Request(type, null));
        Assert.Equal(type, Request(type, Guid.NewGuid()).TimestampType);
    }

    [Theory]
    [InlineData(TimeCorrectionTimestampType.CheckIn)]
    [InlineData(TimeCorrectionTimestampType.CheckOut)]
    public void Request_ForCheckInOrCheckOut_ForbidsAPause(TimeCorrectionTimestampType type)
    {
        Assert.Throws<ArgumentException>(() => Request(type, Guid.NewGuid()));
        Assert.Null(Request(type, null).WorkSessionPauseId);
    }

    [Fact]
    public void Request_StartsPending_NeverRecalculatesSla_AndRejectsABlankReasonOrANonUtcValue()
    {
        var correction = Request(TimeCorrectionTimestampType.CheckIn, null);

        Assert.Equal(TimeCorrectionStatus.Pending, correction.Status);
        Assert.False(correction.SlaRecalculated);
        Assert.Null(correction.DecidedBy);
        Assert.Throws<ArgumentException>(() => Request(TimeCorrectionTimestampType.CheckIn, null, "   "));
        Assert.Throws<ArgumentException>(() => TimeCorrection.Request(
            Guid.NewGuid(), Guid.NewGuid(), null, TimeCorrectionTimestampType.CheckIn, At(1), DateTime.SpecifyKind(At(2), DateTimeKind.Local), "x", Guid.NewGuid()));
        Assert.DoesNotContain(
            typeof(TimeCorrection).GetProperties(),
            property => property.Name.Contains("Sla", StringComparison.Ordinal) && property.SetMethod?.IsPublic == true);
    }

    [Fact]
    public void ApproveAndReject_DecideOnlyAPendingRequest_Once()
    {
        var approver = Guid.NewGuid();
        var approved = Request(TimeCorrectionTimestampType.CheckIn, null);
        approved.Approve(At(30), approver);
        Assert.Equal((TimeCorrectionStatus.Approved, approver, At(30)), (approved.Status, approved.DecidedBy, approved.DecidedAt));
        Assert.Null(approved.DecisionReason);
        Assert.Throws<DomainRuleViolationException>(() => approved.Approve(At(31), approver));
        Assert.Throws<DomainRuleViolationException>(() => approved.Reject("no", At(31), approver));

        var rejected = Request(TimeCorrectionTimestampType.CheckIn, null);
        Assert.Throws<ArgumentException>(() => rejected.Reject("  ", At(30), approver));
        rejected.Reject("Not supported by the visit log.", At(30), approver);
        Assert.Equal((TimeCorrectionStatus.Rejected, "Not supported by the visit log."), (rejected.Status, rejected.DecisionReason));
        Assert.Throws<DomainRuleViolationException>(() => rejected.Approve(At(31), approver));
    }
}
