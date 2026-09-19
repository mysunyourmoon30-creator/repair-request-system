using RepairRequest.Domain.Common;

namespace RepairRequest.Domain.WorkOrders;

/// <summary>
/// Work Session aggregate (RR-DD-001 WS-001, WS-003..010; ST-WS-001; S3-001). One Work Session is opened per
/// Check-in. <see cref="TenantId"/> is not one of the documented WS-* fields (the Data Dictionary's numbering
/// jumps from WS-001 to WS-003) but is carried here anyway, matching every other transactional aggregate in
/// this codebase and `docs/05` section 2's own "tenant_id on transactional/control data" convention. Pause,
/// Resume and Check-out (ST-WS-002..004) are a later ticket; <see cref="WorkSessionStatus"/> carries their
/// values for schema fidelity only, and this aggregate exposes no method that reaches them yet — same
/// "unreachable by any method here" convention S2-003's <see cref="ServiceVisit"/> used for its own
/// then-out-of-scope states.
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

    /// <summary>WS-007. Set on Pause; out of scope for S3-001, always null here.</summary>
    public DateTime? PauseStartAt { get; private set; }

    /// <summary>WS-008. Set on Resume; out of scope for S3-001, always null here.</summary>
    public DateTime? ResumeAt { get; private set; }

    /// <summary>WS-009. Set on Check-out; out of scope for S3-001, always null here.</summary>
    public DateTime? CheckOutAt { get; private set; }

    /// <summary>WS-010. Optimistic concurrency token.</summary>
    public byte[] RowVersion { get; private set; } = [];
}
