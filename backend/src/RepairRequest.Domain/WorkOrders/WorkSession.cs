using RepairRequest.Domain.Common;

namespace RepairRequest.Domain.WorkOrders;

/// <summary>
/// Work Session aggregate (RR-DD-001 WS-001, WS-003..010; ST-WS-001; S3-001). One Work Session is opened per
/// Check-in. <see cref="TenantId"/> is not one of the documented WS-* fields (the Data Dictionary's numbering
/// jumps from WS-001 to WS-003) but is carried here anyway, matching every other transactional aggregate in
/// this codebase and `docs/05` section 2's own "tenant_id on transactional/control data" convention.
/// S3-002 adds Pause (ST-WS-002); S3-003 adds Resume (ST-WS-003); S3-004 adds Check-out (ST-WS-004) as a pure
/// status transition — RR-DD-001 BR-06 ties Check-out's guard to Summary/Outcome/CLEAN evidence, but that data
/// lives in the separate <c>work_summary</c> table and is captured by a later use case (UC-WO-020); Portfolio
/// Project Owner directive, S3-004 pre-implementation: <see cref="CheckOut"/> enforces only the state guard.
/// </summary>
public sealed class WorkSession
{
    /// <summary>EF Core materialization.</summary>
    private WorkSession()
    {
    }

    private WorkSession(Guid tenantId, Guid serviceVisitId, Guid technicianId, DateTime checkInAt)
    {
        TenantId = DomainGuard.NotEmpty(tenantId, nameof(tenantId));
        ServiceVisitId = DomainGuard.NotEmpty(serviceVisitId, nameof(serviceVisitId));
        TechnicianId = DomainGuard.NotEmpty(technicianId, nameof(technicianId));
        CheckInAt = DomainGuard.Utc(checkInAt, nameof(checkInAt));
        Status = WorkSessionStatus.CheckedIn;
    }

    /// <summary>
    /// ST-WS-001 Check-in / create session. <paramref name="checkInAt"/> is always the server clock (RR-API-001
    /// section 1: "Time: ISO-8601 UTC"; no generic status/time field the client can set) — the caller (the
    /// Application service) is the only source of this timestamp, never request input.
    /// </summary>
    public static WorkSession Create(Guid tenantId, Guid serviceVisitId, Guid technicianId, DateTime checkInAt) =>
        new(tenantId, serviceVisitId, technicianId, checkInAt);

    /// <summary>
    /// ST-WS-002 Pause (S3-002; UC-WO-012). Only from CHECKED_IN — which is also the "no active pause" guard, since
    /// a paused session is PAUSED. <paramref name="pausedAt"/> is always the server clock. Sets WS-007
    /// <see cref="PauseStartAt"/> and returns the new pause-period row, so history lives in
    /// <see cref="WorkSessionPause"/> and is never replaced by a later pause. The reason is required and must
    /// be non-blank; the caller trims it first.
    /// </summary>
    public WorkSessionPause Pause(string reason, DateTime pausedAt)
    {
        if (!WorkSessionStatusTransitions.IsAllowed(Status, WorkSessionStatus.Paused))
        {
            throw new DomainRuleViolationException("Only a CHECKED_IN Work Session can be paused.");
        }

        var pause = WorkSessionPause.Create(TenantId, Id, pausedAt, reason);

        Status = WorkSessionStatus.Paused;
        PauseStartAt = pausedAt;
        return pause;
    }

    /// <summary>
    /// ST-WS-003 Resume (S3-003; UC-WO-012). Only from PAUSED — the guard "Active pause exists" is the caller's
    /// (the Application service's) responsibility to have already loaded <paramref name="openPause"/> with, since
    /// a session invariantly has an open pause whenever it is PAUSED. Closes that period
    /// (<see cref="WorkSessionPause.Resume"/>) and clears WS-007 <see cref="PauseStartAt"/> (no pause is open any
    /// more); WS-008 <see cref="ResumeAt"/> is overwritten with this Resume's own time — the same "latest event"
    /// column shape as <see cref="CheckInAt"/>/<see cref="CheckOutAt"/>, while full pause history stays in
    /// <see cref="WorkSessionPause"/> and is never rewritten. <paramref name="resumedAt"/> is always the server
    /// clock.
    /// </summary>
    public void Resume(WorkSessionPause openPause, DateTime resumedAt)
    {
        ArgumentNullException.ThrowIfNull(openPause);

        if (!WorkSessionStatusTransitions.IsAllowed(Status, WorkSessionStatus.CheckedIn))
        {
            throw new DomainRuleViolationException("Only a PAUSED Work Session can be resumed.");
        }

        openPause.Resume(resumedAt);

        Status = WorkSessionStatus.CheckedIn;
        ResumeAt = openPause.ResumedAt!.Value;
        PauseStartAt = null;
    }

    /// <summary>
    /// ST-WS-004 Check-out (S3-004; UC-WO-016). Only from CHECKED_IN — which is also the "no active pause" guard,
    /// same reasoning <see cref="Pause"/> already uses. <paramref name="checkOutAt"/> is always the server clock.
    /// Per the class remarks' Portfolio Project Owner directive, this enforces only the state guard: BR-06's
    /// summary/outcome/evidence half is not part of this ticket.
    /// </summary>
    public void CheckOut(DateTime checkOutAt)
    {
        if (!WorkSessionStatusTransitions.IsAllowed(Status, WorkSessionStatus.CheckedOut))
        {
            throw new DomainRuleViolationException("Only a CHECKED_IN Work Session can be checked out.");
        }

        var utcCheckOutAt = DomainGuard.Utc(checkOutAt, nameof(checkOutAt));

        Status = WorkSessionStatus.CheckedOut;
        CheckOutAt = utcCheckOutAt;
    }

    /// <summary>
    /// Whether the timestamp a Time Correction targets exists (`docs/17` P1). CHECK_IN always exists; CHECK_OUT only once the
    /// session is checked out; PAUSE_START needs a pause row of this session; RESUME needs a *closed* pause row of this session.
    /// </summary>
    public bool HasTimestamp(TimeCorrectionTimestampType type, WorkSessionPause? pause) => type switch
    {
        TimeCorrectionTimestampType.CheckIn => true,
        TimeCorrectionTimestampType.CheckOut => CheckOutAt is not null,
        TimeCorrectionTimestampType.PauseStart => pause is not null && pause.WorkSessionId == Id,
        TimeCorrectionTimestampType.Resume => pause is not null && pause.WorkSessionId == Id && pause.ResumedAt is not null,
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    /// <summary>The current value of the targeted timestamp. Throws if <see cref="HasTimestamp"/> is false — callers check first.</summary>
    public DateTime CurrentTimestamp(TimeCorrectionTimestampType type, WorkSessionPause? pause)
    {
        if (!HasTimestamp(type, pause))
        {
            throw new InvalidOperationException("The targeted timestamp does not exist on this Work Session.");
        }

        return type switch
        {
            TimeCorrectionTimestampType.CheckIn => CheckInAt,
            TimeCorrectionTimestampType.CheckOut => CheckOutAt!.Value,
            TimeCorrectionTimestampType.PauseStart => pause!.PausedAt,
            TimeCorrectionTimestampType.Resume => pause!.ResumedAt!.Value,
            _ => throw new ArgumentOutOfRangeException(nameof(type))
        };
    }

    /// <summary>
    /// Null when replacing the targeted timestamp by <paramref name="newValue"/> keeps the whole session chronological
    /// (`docs/17` P2, <see cref="WorkSessionChronology"/>: check-in, every pause interval and check-out), otherwise a short
    /// description of the first violation. <paramref name="pauses"/> are all pause rows of this session.
    /// </summary>
    public string? ChronologyViolationAfter(
        TimeCorrectionTimestampType type, DateTime newValue, WorkSessionPause? pause, IReadOnlyCollection<WorkSessionPause> pauses)
    {
        ArgumentNullException.ThrowIfNull(pauses);

        var checkIn = type == TimeCorrectionTimestampType.CheckIn ? newValue : CheckInAt;
        var checkOut = type == TimeCorrectionTimestampType.CheckOut ? newValue : CheckOutAt;
        var periods = pauses.Select(item =>
        {
            var isTarget = pause is not null && item.Id == pause.Id;
            return new PausePeriod(
                item.Id,
                isTarget && type == TimeCorrectionTimestampType.PauseStart ? newValue : item.PausedAt,
                isTarget && type == TimeCorrectionTimestampType.Resume ? newValue : item.ResumedAt);
        }).ToList();

        return WorkSessionChronology.Violation(checkIn, checkOut, periods);
    }

    /// <summary>
    /// Applies an approved Time Correction (ST-TC-002; UC-TIME-001 "approved applies timestamp only"; `docs/17` P1/P2). Only the
    /// targeted fact changes — <see cref="Status"/> never does. CHECK_IN/CHECK_OUT change the session column. PAUSE_START/RESUME
    /// change the named pause row and then mirror the latest-event columns only where they describe that pause: PAUSE_START
    /// mirrors <see cref="PauseStartAt"/> when the pause is still open; RESUME mirrors <see cref="ResumeAt"/> when it is the
    /// latest closed pause. Throws (and changes nothing) when the timestamp does not exist or the corrected session would no
    /// longer be chronological.
    /// </summary>
    public void ApplyTimeCorrection(
        TimeCorrectionTimestampType type, DateTime newValue, WorkSessionPause? pause, IReadOnlyCollection<WorkSessionPause> pauses)
    {
        ArgumentNullException.ThrowIfNull(pauses);

        if (!HasTimestamp(type, pause))
        {
            throw new DomainRuleViolationException("The targeted timestamp does not exist on this Work Session.");
        }

        var utcNewValue = DomainGuard.Utc(newValue, nameof(newValue));
        var violation = ChronologyViolationAfter(type, utcNewValue, pause, pauses);
        if (violation is not null)
        {
            throw new DomainRuleViolationException(violation);
        }

        switch (type)
        {
            case TimeCorrectionTimestampType.CheckIn:
                CheckInAt = utcNewValue;
                break;
            case TimeCorrectionTimestampType.CheckOut:
                CheckOutAt = utcNewValue;
                break;
            case TimeCorrectionTimestampType.PauseStart:
                pause!.CorrectPausedAt(utcNewValue);
                if (pause.ResumedAt is null)
                {
                    PauseStartAt = utcNewValue;
                }

                break;
            case TimeCorrectionTimestampType.Resume:
                var latestClosed = pauses
                    .Where(item => item.ResumedAt is not null)
                    .OrderByDescending(item => item.PausedAt)
                    .ThenByDescending(item => item.Id)
                    .First();
                pause!.CorrectResumedAt(utcNewValue);
                if (latestClosed.Id == pause.Id)
                {
                    ResumeAt = utcNewValue;
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(type));
        }
    }

    /// <summary>WS-001. Assigned on insert (sequential GUID).</summary>
    public Guid Id { get; private set; }

    /// <summary>Not a documented WS-* field; see class remarks.</summary>
    public Guid TenantId { get; private set; }

    /// <summary>WS-003. Plain FK — a Service Visit has at most one active Work Session at a time (enforced by the store, not here).</summary>
    public Guid ServiceVisitId { get; private set; }

    /// <summary>WS-004. The assigned Technician who checked in; derived from the authenticated caller, never client input.</summary>
    public Guid TechnicianId { get; private set; }

    /// <summary>WS-005. Canonical Work Session states only (ST-WS-001..004).</summary>
    public WorkSessionStatus Status { get; private set; }

    /// <summary>WS-006. Set once, at Check-in; server-derived.</summary>
    public DateTime CheckInAt { get; private set; }

    /// <summary>
    /// WS-007. Start of the current (open) pause, set by <see cref="Pause"/> and cleared to null by
    /// <see cref="Resume"/> (S3-003). The full pause history is in <see cref="WorkSessionPause"/>.
    /// </summary>
    public DateTime? PauseStartAt { get; private set; }

    /// <summary>WS-008. Set by <see cref="Resume"/> (S3-003) to that Resume's own time; overwritten by a later Resume.</summary>
    public DateTime? ResumeAt { get; private set; }

    /// <summary>WS-009. Set by <see cref="CheckOut"/> (S3-004).</summary>
    public DateTime? CheckOutAt { get; private set; }

    /// <summary>WS-010. Optimistic concurrency token.</summary>
    public byte[] RowVersion { get; private set; } = [];
}
