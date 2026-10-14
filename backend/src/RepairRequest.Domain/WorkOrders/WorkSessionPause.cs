using RepairRequest.Domain.Common;

namespace RepairRequest.Domain.WorkOrders;

/// <summary>
/// One pause period of a Work Session (S3-002/S3-003; UC-WO-012). RR-DD-001 keeps a single <c>pause_start_at</c> /
/// <c>resume_at</c> pair on <c>work_session</c> (WS-007/008), which a second pause would overwrite; this table
/// is a Project Owner-directed addition (S3-002) that keeps every pause as its own row so history is never
/// replaced. <see cref="PauseReason"/> is likewise not a baseline field (BR-04 does not list Pause). The mutators are
/// <see cref="Resume"/> (S3-003) and the two Time Correction mutators (S6; `docs/17` P1), all called exclusively by
/// <see cref="WorkSession"/>, which owns the chronology guards: the reason never changes, and the start time changes only
/// through an approved PAUSE_START correction.
/// </summary>
public sealed class WorkSessionPause
{
    public const int ReasonMaxLength = 1000;

    /// <summary>EF Core materialization.</summary>
    private WorkSessionPause()
    {
    }

    private WorkSessionPause(Guid tenantId, Guid workSessionId, DateTime pausedAt, string pauseReason)
    {
        TenantId = DomainGuard.NotEmpty(tenantId, nameof(tenantId));
        WorkSessionId = DomainGuard.NotEmpty(workSessionId, nameof(workSessionId));
        PausedAt = DomainGuard.Utc(pausedAt, nameof(pausedAt));
        PauseReason = DomainGuard.RequiredText(pauseReason, ReasonMaxLength, nameof(pauseReason));
    }

    /// <summary>Created only by <see cref="WorkSession.Pause"/>, which owns the state guard.</summary>
    internal static WorkSessionPause Create(Guid tenantId, Guid workSessionId, DateTime pausedAt, string pauseReason) =>
        new(tenantId, workSessionId, pausedAt, pauseReason);

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid WorkSessionId { get; private set; }

    /// <summary>Server-derived; never client input.</summary>
    public DateTime PausedAt { get; private set; }

    public string PauseReason { get; private set; } = null!;

    /// <summary>Set by <see cref="Resume"/>; null while this pause is the session's open pause.</summary>
    public DateTime? ResumedAt { get; private set; }

    /// <summary>Approved PAUSE_START correction, called only by <see cref="WorkSession.ApplyTimeCorrection"/>, which has already validated the whole session chronology.</summary>
    internal void CorrectPausedAt(DateTime pausedAt)
    {
        var utcPausedAt = DomainGuard.Utc(pausedAt, nameof(pausedAt));
        if (ResumedAt is { } resumedAt && utcPausedAt > resumedAt)
        {
            throw new ArgumentException("A pause cannot start after it ended.", nameof(pausedAt));
        }

        PausedAt = utcPausedAt;
    }

    /// <summary>Approved RESUME correction, called only by <see cref="WorkSession.ApplyTimeCorrection"/>; only a closed pause has a resume time to correct.</summary>
    internal void CorrectResumedAt(DateTime resumedAt)
    {
        if (ResumedAt is null)
        {
            throw new DomainRuleViolationException("An open pause has no resume time to correct.");
        }

        var utcResumedAt = DomainGuard.Utc(resumedAt, nameof(resumedAt));
        if (utcResumedAt < PausedAt)
        {
            throw new ArgumentException("A pause cannot end before it started.", nameof(resumedAt));
        }

        ResumedAt = utcResumedAt;
    }

    /// <summary>
    /// ST-WS-003 success (S3-003), called only by <see cref="WorkSession.Resume"/>, which owns the state guard.
    /// <paramref name="resumedAt"/> is always the server clock and cannot be before <see cref="PausedAt"/>
    /// (CK_work_session_pause_period).
    /// </summary>
    internal void Resume(DateTime resumedAt)
    {
        if (ResumedAt is not null)
        {
            throw new DomainRuleViolationException("This pause period has already been resumed.");
        }

        var utcResumedAt = DomainGuard.Utc(resumedAt, nameof(resumedAt));
        if (utcResumedAt < PausedAt)
        {
            throw new ArgumentException("Resume time cannot be before the pause started.", nameof(resumedAt));
        }

        ResumedAt = utcResumedAt;
    }
}
