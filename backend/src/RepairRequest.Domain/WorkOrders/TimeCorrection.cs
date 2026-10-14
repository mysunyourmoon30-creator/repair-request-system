using RepairRequest.Domain.Common;

namespace RepairRequest.Domain.WorkOrders;

/// <summary>
/// Time Correction aggregate (RR-DD-001 TC-001..012; ST-TC-001..003; UC-TIME-001; `docs/17`). Requested by the
/// Technician who owns the Work Session or a Team Lead within their Site scope, decided by a Supervisor. A
/// correction changes work-duration facts only (the Work Session's check-in/out, or one exact pause's start or
/// end) and never recalculates SLA (<see cref="SlaRecalculated"/> is always false; BR-13/BR-18 text of the baseline).
/// <para>
/// PAUSE_START and RESUME name the exact pause they correct (<see cref="WorkSessionPauseId"/>; `docs/17` P1, owner
/// approval of 13 October 2026) because a session has one row per pause (`docs/13` §4.12); CHECK_IN and CHECK_OUT never
/// carry one. The logical target of a correction is (<see cref="WorkSessionId"/>, <see cref="TimestampType"/>,
/// <see cref="WorkSessionPauseId"/>); at most one PENDING correction exists per target (`docs/17` P8, enforced by
/// filtered unique indexes). <see cref="TenantId"/> is not one of the documented TC-* fields but is carried here like
/// every other transactional aggregate.
/// </para>
/// The aggregate only owns its own lifecycle; applying the corrected value to the Work Session (and its pause rows) is
/// <see cref="WorkSession.ApplyTimeCorrection"/>, called by the Application service in the same transaction.
/// </summary>
public sealed class TimeCorrection
{
    public const int ReasonMaxLength = 1000;

    /// <summary>EF Core materialization.</summary>
    private TimeCorrection()
    {
    }

    private TimeCorrection(
        Guid tenantId,
        Guid workSessionId,
        Guid? workSessionPauseId,
        TimeCorrectionTimestampType timestampType,
        DateTime oldValue,
        DateTime newValue,
        string reason,
        Guid requestedBy)
    {
        var needsPause = RequiresPause(timestampType);
        if (needsPause && workSessionPauseId is null)
        {
            throw new ArgumentException("PAUSE_START and RESUME corrections must name the pause they correct.", nameof(workSessionPauseId));
        }

        if (!needsPause && workSessionPauseId is not null)
        {
            throw new ArgumentException("CHECK_IN and CHECK_OUT corrections cannot name a pause.", nameof(workSessionPauseId));
        }

        TenantId = DomainGuard.NotEmpty(tenantId, nameof(tenantId));
        WorkSessionId = DomainGuard.NotEmpty(workSessionId, nameof(workSessionId));
        WorkSessionPauseId = workSessionPauseId is { } pauseId ? DomainGuard.NotEmpty(pauseId, nameof(workSessionPauseId)) : null;
        TimestampType = timestampType;
        OldValue = DomainGuard.Utc(oldValue, nameof(oldValue));
        NewValue = DomainGuard.Utc(newValue, nameof(newValue));
        Reason = DomainGuard.RequiredText(reason, ReasonMaxLength, nameof(reason));
        RequestedBy = DomainGuard.NotEmpty(requestedBy, nameof(requestedBy));
        Status = TimeCorrectionStatus.Pending;

        // BR-13: Time Correction never recalculates SLA; there is no method that ever sets this true.
        SlaRecalculated = false;
    }

    /// <summary>True for the two timestamp types that live on a pause row (PAUSE_START, RESUME).</summary>
    public static bool RequiresPause(TimeCorrectionTimestampType type) =>
        type is TimeCorrectionTimestampType.PauseStart or TimeCorrectionTimestampType.Resume;

    /// <summary>
    /// ST-TC-001 Request. <paramref name="oldValue"/> is the current value of the targeted timestamp at request time
    /// (an immutable snapshot, TC-004); the caller (the Application service) owns the "supported timestamp, not in
    /// the future, valid sequence" guards, since this aggregate does not know the session's other timestamps. The
    /// reason is required and non-blank; the caller trims it first.
    /// </summary>
    public static TimeCorrection Request(
        Guid tenantId,
        Guid workSessionId,
        Guid? workSessionPauseId,
        TimeCorrectionTimestampType timestampType,
        DateTime oldValue,
        DateTime newValue,
        string reason,
        Guid requestedBy) =>
        new(tenantId, workSessionId, workSessionPauseId, timestampType, oldValue, newValue, reason, requestedBy);

    /// <summary>
    /// ST-TC-002 Approve: only from PENDING. The caller has already applied <see cref="NewValue"/> to the Work Session in
    /// the same transaction. <paramref name="decidedAt"/> is always the server clock.
    /// </summary>
    public void Approve(DateTime decidedAt, Guid decidedBy)
    {
        if (!TimeCorrectionStatusTransitions.IsAllowed(Status, TimeCorrectionStatus.Approved))
        {
            throw new DomainRuleViolationException("Only a PENDING Time Correction can be approved.");
        }

        var utcDecidedAt = DomainGuard.Utc(decidedAt, nameof(decidedAt));
        var validDecidedBy = DomainGuard.NotEmpty(decidedBy, nameof(decidedBy));

        Status = TimeCorrectionStatus.Approved;
        DecidedBy = validDecidedBy;
        DecidedAt = utcDecidedAt;
    }

    /// <summary>
    /// ST-TC-003 Reject: only from PENDING. The Work Session is never touched. The decision reason is required and
    /// non-blank; the caller trims it first. <paramref name="decidedAt"/> is always the server clock.
    /// </summary>
    public void Reject(string decisionReason, DateTime decidedAt, Guid decidedBy)
    {
        if (!TimeCorrectionStatusTransitions.IsAllowed(Status, TimeCorrectionStatus.Rejected))
        {
            throw new DomainRuleViolationException("Only a PENDING Time Correction can be rejected.");
        }

        var trimmedReason = DomainGuard.RequiredText(decisionReason, ReasonMaxLength, nameof(decisionReason));
        var utcDecidedAt = DomainGuard.Utc(decidedAt, nameof(decidedAt));
        var validDecidedBy = DomainGuard.NotEmpty(decidedBy, nameof(decidedBy));

        DecisionReason = trimmedReason;
        Status = TimeCorrectionStatus.Rejected;
        DecidedBy = validDecidedBy;
        DecidedAt = utcDecidedAt;
    }

    /// <summary>TC-001. Assigned on insert (sequential GUID) — also the list-ordering key.</summary>
    public Guid Id { get; private set; }

    /// <summary>Not a documented TC-* field; see class remarks.</summary>
    public Guid TenantId { get; private set; }

    /// <summary>TC-002. Plain FK — a Work Session may have many correction requests over its lifetime.</summary>
    public Guid WorkSessionId { get; private set; }

    /// <summary>Technical addition (`docs/17` P1): the exact pause a PAUSE_START/RESUME correction targets; null for CHECK_IN/CHECK_OUT.</summary>
    public Guid? WorkSessionPauseId { get; private set; }

    /// <summary>TC-003. One of the four supported Work Session timestamps.</summary>
    public TimeCorrectionTimestampType TimestampType { get; private set; }

    /// <summary>TC-004. Immutable snapshot of the targeted value at request time; never changes after.</summary>
    public DateTime OldValue { get; private set; }

    /// <summary>TC-005. The requested replacement value; must preserve chronology and not exceed the server UTC now (checked at request and at approval).</summary>
    public DateTime NewValue { get; private set; }

    /// <summary>TC-006. Required, non-blank.</summary>
    public string Reason { get; private set; } = null!;

    /// <summary>TC-007. The Technician or Team Lead who requested the correction; server-derived, never client input.</summary>
    public Guid RequestedBy { get; private set; }

    /// <summary>TC-008. PENDING/APPROVED/REJECTED.</summary>
    public TimeCorrectionStatus Status { get; private set; }

    /// <summary>TC-009. Set by <see cref="Approve"/>/<see cref="Reject"/>; the deciding Supervisor.</summary>
    public Guid? DecidedBy { get; private set; }

    /// <summary>TC-010. Required on Reject; always null on Approve.</summary>
    public string? DecisionReason { get; private set; }

    /// <summary>TC-011. Set by <see cref="Approve"/>/<see cref="Reject"/>; server-derived.</summary>
    public DateTime? DecidedAt { get; private set; }

    /// <summary>TC-012. Always false (BR-13): Time Correction never recalculates SLA.</summary>
    public bool SlaRecalculated { get; private set; }

    /// <summary>Optimistic concurrency token: the If-Match of Approve/Reject (TIME-API-002/003); TIME-API-001 Request carries none.</summary>
    public byte[] RowVersion { get; private set; } = [];
}
