using RepairRequest.Domain.Common;

namespace RepairRequest.Domain.WorkOrders;

/// <summary>
/// Work Session aggregate (RR-DD-001 WS-001, WS-003..010; ST-WS-001; S3-001). One Work Session is opened per
/// Check-in. <see cref="TenantId"/> is not one of the documented WS-* fields (the Data Dictionary's numbering
/// jumps from WS-001 to WS-003) but is carried here anyway, matching every other transactional aggregate in
/// this codebase and `docs/05` section 2's own "tenant_id on transactional/control data" convention.
/// S3-002 adds Pause (ST-WS-002); S3-003 adds Resume (ST-WS-003). Check-out (ST-WS-004) is a later ticket;
/// <see cref="WorkSessionStatus.CheckedOut"/> exists for schema fidelity only, and this aggregate exposes no
/// method that reaches it yet — same "unreachable by any method here" convention S2-003's
/// <see cref="ServiceVisit"/> used for its own then-out-of-scope states.
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

    /// <summary>WS-009. Set on Check-out; out of scope for S3-001, always null here.</summary>
    public DateTime? CheckOutAt { get; private set; }

    /// <summary>WS-010. Optimistic concurrency token.</summary>
    public byte[] RowVersion { get; private set; } = [];
}
