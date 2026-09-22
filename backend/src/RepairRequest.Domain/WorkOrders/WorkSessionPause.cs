using RepairRequest.Domain.Common;

namespace RepairRequest.Domain.WorkOrders;

/// <summary>
/// One pause period of a Work Session (S3-002; UC-WO-012). RR-DD-001 keeps a single <c>pause_start_at</c> /
/// <c>resume_at</c> pair on <c>work_session</c> (WS-007/008), which a second pause would overwrite; this table
/// is a Project Owner-directed addition (S3-002) that keeps every pause as its own row so history is never
/// replaced. <see cref="PauseReason"/> is likewise not a baseline field (BR-04 does not list Pause). Rows have
/// no mutators except the later Resume ticket's <c>ResumedAt</c>: reason and start time never change.
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

    /// <summary>Set by Resume (a later ticket); null while this pause is the session's open pause.</summary>
    public DateTime? ResumedAt { get; private set; }
}
