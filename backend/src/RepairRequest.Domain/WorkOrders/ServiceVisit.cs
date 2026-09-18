using RepairRequest.Domain.Common;

namespace RepairRequest.Domain.WorkOrders;

/// <summary>
/// Service Visit aggregate (RR-DD-001 SV-001..018; ST-SV-001/004..009; S2-003). A Work Order has 0..N Service
/// Visits (SV-003 is a plain, non-unique FK). "Team" has no master-data entity anywhere in the codebase
/// (S2-001/S2-002 scaffolding left <see cref="WorkOrder.OwnerTeamId"/> a bare, unvalidated Guid) — Portfolio
/// Project Owner directive, S2-003 pre-implementation: <see cref="AssignedTeamId"/> stays an unvalidated
/// caller-supplied identifier; only <see cref="AssignedTechnicianId"/> is checked against a real user
/// (TECHNICIAN role, Site-scoped). Check-in/Check-out (ST-SV-002/003, IN_PROGRESS/COMPLETED) are out of scope
/// for S2-003 (FR-06 Work Session, a separate ticket); those statuses exist on <see cref="ServiceVisitStatus"/>
/// for schema fidelity only and are unreachable by any method here.
/// </summary>
public sealed class ServiceVisit
{
    public const int ReasonMaxLength = 1000;

    /// <summary>EF Core materialization.</summary>
    private ServiceVisit()
    {
    }

    private ServiceVisit(
        Guid tenantId,
        Guid workOrderId,
        ServiceVisitType visitType,
        Guid assignedTeamId,
        Guid assignedTechnicianId,
        DateTime scheduledStartAt,
        DateTime scheduledEndAt,
        Guid? sourceMissedVisitId)
    {
        TenantId = DomainGuard.NotEmpty(tenantId, nameof(tenantId));
        WorkOrderId = DomainGuard.NotEmpty(workOrderId, nameof(workOrderId));
        VisitType = visitType;
        AssignedTeamId = DomainGuard.NotEmpty(assignedTeamId, nameof(assignedTeamId));
        AssignedTechnicianId = DomainGuard.NotEmpty(assignedTechnicianId, nameof(assignedTechnicianId));
        SetSchedule(scheduledStartAt, scheduledEndAt);
        SourceMissedVisitId = sourceMissedVisitId;
        Status = ServiceVisitStatus.Scheduled;
    }

    /// <summary>
    /// ST-SV-001 Create Scheduled Visit. Team and technician are both required at creation (UC-WO-003 main flow:
    /// "Create Visit; assign team/technician; set schedule"; WO-006 "required before SCHEDULED"). Used both by
    /// Schedule (the Work Order's first Visit) and by Decide Missed's follow-up Visit (<paramref name="sourceMissedVisitId"/>
    /// set, ST-SV-009/D-15).
    /// </summary>
    public static ServiceVisit Create(
        Guid tenantId,
        Guid workOrderId,
        ServiceVisitType visitType,
        Guid assignedTeamId,
        Guid assignedTechnicianId,
        DateTime scheduledStartAt,
        DateTime scheduledEndAt,
        Guid? sourceMissedVisitId = null) =>
        new(tenantId, workOrderId, visitType, assignedTeamId, assignedTechnicianId, scheduledStartAt, scheduledEndAt, sourceMissedVisitId);

    /// <summary>SV-001. Assigned on insert (sequential GUID).</summary>
    public Guid Id { get; private set; }

    /// <summary>SV-002. Customer/Company scope; server-derived; immutable.</summary>
    public Guid TenantId { get; private set; }

    /// <summary>SV-003. Plain, non-unique FK — a Work Order has 0..N Service Visits.</summary>
    public Guid WorkOrderId { get; private set; }

    /// <summary>SV-004.</summary>
    public ServiceVisitType VisitType { get; private set; }

    /// <summary>SV-005. Canonical Service Visit states only (ST-SV-001..009).</summary>
    public ServiceVisitStatus Status { get; private set; }

    /// <summary>SV-006. Caller-supplied, not validated (no Team master data exists).</summary>
    public Guid? AssignedTeamId { get; private set; }

    /// <summary>SV-007. Validated: an ApplicationUser of the tenant holding TECHNICIAN, Site-scoped.</summary>
    public Guid? AssignedTechnicianId { get; private set; }

    /// <summary>SV-008. Required while SCHEDULED.</summary>
    public DateTime? ScheduledStartAt { get; private set; }

    /// <summary>SV-009. Required while SCHEDULED; must not be earlier than <see cref="ScheduledStartAt"/>.</summary>
    public DateTime? ScheduledEndAt { get; private set; }

    /// <summary>SV-010. Required on Reschedule.</summary>
    public string? RescheduleReason { get; private set; }

    /// <summary>SV-011. Required on Reassign.</summary>
    public string? ReassignReason { get; private set; }

    /// <summary>SV-012. Required on Cancel.</summary>
    public string? CancelReason { get; private set; }

    /// <summary>SV-013. Required on Mark Missed.</summary>
    public string? MissedReason { get; private set; }

    /// <summary>SV-014. Set on Check-out; out of scope for S2-003, always null here.</summary>
    public DateTime? CompletedAt { get; private set; }

    /// <summary>SV-015. Optimistic concurrency token.</summary>
    public byte[] RowVersion { get; private set; } = [];

    /// <summary>SV-016. Set only on a follow-up Visit created by Decide Missed; the original stays immutable.</summary>
    public Guid? SourceMissedVisitId { get; private set; }

    /// <summary>SV-017. Set once, by Decide Missed.</summary>
    public MissedVisitDecisionCode? MissedDecisionCode { get; private set; }

    /// <summary>SV-018. Set once, by Decide Missed.</summary>
    public DateTime? MissedDecidedAt { get; private set; }

    /// <summary>
    /// ST-SV-004/005 Reschedule. Documented as two matrix rows (SCHEDULED -&gt; RESCHEDULED -&gt; SCHEDULED), but only
    /// one API route and one use case (UC-WO-006) exist for it, so this settles back at SCHEDULED within the same
    /// call rather than leaving the Visit sitting in RESCHEDULED between two separate commands.
    /// </summary>
    public void Reschedule(string reason, DateTime newScheduledStartAt, DateTime newScheduledEndAt)
    {
        if (Status != ServiceVisitStatus.Scheduled)
        {
            throw new DomainRuleViolationException("Only a SCHEDULED Service Visit can be rescheduled.");
        }

        RescheduleReason = DomainGuard.RequiredText(reason, ReasonMaxLength, nameof(reason));
        SetSchedule(newScheduledStartAt, newScheduledEndAt);
    }

    /// <summary>ST-SV-006 Reassign: stays SCHEDULED; reason and both the new team and technician are required.</summary>
    public void Reassign(string reason, Guid assignedTeamId, Guid assignedTechnicianId)
    {
        if (Status != ServiceVisitStatus.Scheduled)
        {
            throw new DomainRuleViolationException("Only a SCHEDULED Service Visit can be reassigned.");
        }

        ReassignReason = DomainGuard.RequiredText(reason, ReasonMaxLength, nameof(reason));
        AssignedTeamId = DomainGuard.NotEmpty(assignedTeamId, nameof(assignedTeamId));
        AssignedTechnicianId = DomainGuard.NotEmpty(assignedTechnicianId, nameof(assignedTechnicianId));
    }

    /// <summary>ST-SV-007 Cancel Visit: only before Check-in (i.e. only while SCHEDULED). Work Order/SLA continue.</summary>
    public void Cancel(string reason)
    {
        if (!ServiceVisitStatusTransitions.IsAllowed(Status, ServiceVisitStatus.Cancelled))
        {
            throw new DomainRuleViolationException("Only a SCHEDULED Service Visit can be cancelled.");
        }

        CancelReason = DomainGuard.RequiredText(reason, ReasonMaxLength, nameof(reason));
        Status = ServiceVisitStatus.Cancelled;
    }

    /// <summary>ST-SV-008 Mark Missed: only before Check-in (i.e. only while SCHEDULED).</summary>
    public void MarkMissed(string reason)
    {
        if (!ServiceVisitStatusTransitions.IsAllowed(Status, ServiceVisitStatus.Missed))
        {
            throw new DomainRuleViolationException("Only a SCHEDULED Service Visit can be marked missed.");
        }

        MissedReason = DomainGuard.RequiredText(reason, ReasonMaxLength, nameof(reason));
        Status = ServiceVisitStatus.Missed;
    }

    /// <summary>
    /// ST-SV-009/D-15 Decide Missed. The original Visit's own record only gains the decision code and timestamp —
    /// it never changes Status (stays MISSED, immutable) and is never decided twice. The decision's reason has no
    /// dedicated SV-* column (unlike Reschedule/Reassign/Cancel/MarkMissed's own reason fields); it is recorded in
    /// the audit trail only by the caller. Creating the follow-up Visit (RESCHEDULE/FOLLOW_UP/REASSIGN) is a
    /// separate <see cref="Create"/> call made by the Application layer, mirroring how Convert creates its Work
    /// Order alongside (not inside) the Repair Request's own transition.
    /// </summary>
    public void DecideMissed(MissedVisitDecisionCode decision, DateTime decidedAt)
    {
        if (Status != ServiceVisitStatus.Missed)
        {
            throw new DomainRuleViolationException("Only a MISSED Service Visit can have a follow-up decision recorded.");
        }

        if (MissedDecisionCode is not null)
        {
            throw new DomainRuleViolationException("This Service Visit's Missed decision was already recorded.");
        }

        MissedDecisionCode = decision;
        MissedDecidedAt = DomainGuard.Utc(decidedAt, nameof(decidedAt));
    }

    private void SetSchedule(DateTime start, DateTime end)
    {
        DomainGuard.Utc(start, nameof(start));
        DomainGuard.Utc(end, nameof(end));

        if (end < start)
        {
            throw new ArgumentException("The scheduled end must not be earlier than the scheduled start.", nameof(end));
        }

        ScheduledStartAt = start;
        ScheduledEndAt = end;
    }
}
