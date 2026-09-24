using RepairRequest.Domain.Common;

namespace RepairRequest.Domain.WorkOrders;

/// <summary>
/// Work Order aggregate root (RR-DD-001 WO-001..013; RR-DBD-001 work_order).
/// S2-001 establishes the persisted shape for the List/Detail read endpoints; S2-002 adds Convert
/// (ST-RR-008/UC-WO-001). Schedule, Reassign and every later transition remain out of scope.
/// <see cref="CreatedAt"/> is a technical ordering column, not one of the documented WO-001..013
/// business fields (DEC-S2-001-04).
/// </summary>
public sealed class WorkOrder
{
    public const int WorkOrderNoMaxLength = 30;
    public const int ReasonMaxLength = 1000;

    /// <summary>EF Core materialization.</summary>
    private WorkOrder()
    {
    }

    private WorkOrder(Guid tenantId, Guid repairRequestId, string workOrderNo, DateTime createdAt)
    {
        TenantId = DomainGuard.NotEmpty(tenantId, nameof(tenantId));
        RepairRequestId = DomainGuard.NotEmpty(repairRequestId, nameof(repairRequestId));
        WorkOrderNo = DomainGuard.RequiredText(workOrderNo, WorkOrderNoMaxLength, nameof(workOrderNo));
        CreatedAt = DomainGuard.Utc(createdAt, nameof(createdAt));
        Status = WorkOrderStatus.Open;
    }

    /// <summary>
    /// Creates a new Work Order in OPEN state (ST-RR-008/UC-WO-001: "Atomically create WO OPEN"). Tenant, the
    /// originating Repair Request and the generated Work Order No. are server-derived, never client input. Used
    /// by the Convert command (S2-002) and by test seeding.
    /// </summary>
    public static WorkOrder Create(Guid tenantId, Guid repairRequestId, string workOrderNo, DateTime createdAt) =>
        new(tenantId, repairRequestId, workOrderNo, createdAt);

    /// <summary>WO-001. Assigned on insert (sequential GUID).</summary>
    public Guid Id { get; private set; }

    /// <summary>WO-002. Customer/Company scope; server-derived; immutable.</summary>
    public Guid TenantId { get; private set; }

    /// <summary>WO-003. Unique within the tenant; generation format is not yet defined (DEC-S2-001-04).</summary>
    public string WorkOrderNo { get; private set; } = null!;

    /// <summary>WO-004. Unique FK; exactly one Work Order per Repair Request (BR-03).</summary>
    public Guid RepairRequestId { get; private set; }

    /// <summary>WO-005. Canonical Work Order states only (ST-WO-001..011).</summary>
    public WorkOrderStatus Status { get; private set; }

    /// <summary>WO-006. Required before SCHEDULED. No Team master-data entity exists yet (DEC-S2-001-03); persisted only.</summary>
    public Guid? OwnerTeamId { get; private set; }

    /// <summary>WO-007. Active Team Lead. Not displayed in S2-001 (DEC-S2-001-03); persisted only.</summary>
    public Guid? TeamLeadId { get; private set; }

    /// <summary>WO-008. Exactly one designated contact; required before Acceptance.</summary>
    public Guid? AcceptanceContactId { get; private set; }

    /// <summary>WO-009. Immutable once set.</summary>
    public string? AcceptanceContactSnapshot { get; private set; }

    /// <summary>WO-010. Required on Cancel.</summary>
    public string? CancelReason { get; private set; }

    /// <summary>WO-011. Supervisor, on CLOSED.</summary>
    public Guid? ClosedBy { get; private set; }

    /// <summary>WO-012. On CLOSED.</summary>
    public DateTime? ClosedAt { get; private set; }

    /// <summary>WO-013. Optimistic concurrency token.</summary>
    public byte[] RowVersion { get; private set; } = [];

    /// <summary>
    /// Technical ordering column for the S2-001 list's fixed newest-first sort (DEC-S2-001-04). Not part of
    /// WO-001..013; set once at construction and never changed.
    /// </summary>
    public DateTime CreatedAt { get; private set; }

    /// <summary>
    /// ST-WO-001 Schedule (OPEN -&gt; SCHEDULED) by a Coordinator (S2-003; UC-WO-003). Aggregate-local guard only:
    /// current state OPEN. Sets the required owner team (WO-006); the first Service Visit itself is created by the
    /// Application layer in the same transaction, mirroring how Convert creates its Work Order alongside (not
    /// inside) the Repair Request's own transition.
    /// </summary>
    public void Schedule(Guid ownerTeamId)
    {
        if (!WorkOrderStatusTransitions.IsAllowed(Status, WorkOrderStatus.Scheduled))
        {
            throw new DomainRuleViolationException("Only an OPEN Work Order can be scheduled.");
        }

        OwnerTeamId = DomainGuard.NotEmpty(ownerTeamId, nameof(ownerTeamId));
        Status = WorkOrderStatus.Scheduled;
    }

    /// <summary>
    /// ST-WO-002 Check-in begins work (S3-001; UC-WO-016; BR-05): SCHEDULED -&gt; IN_PROGRESS, triggered by the
    /// assigned Technician's Check-in on one of this Work Order's Service Visits. Aggregate-local guard only:
    /// current state SCHEDULED.
    /// </summary>
    public void BeginWork()
    {
        if (!WorkOrderStatusTransitions.IsAllowed(Status, WorkOrderStatus.InProgress))
        {
            throw new DomainRuleViolationException("Only a SCHEDULED Work Order can begin work.");
        }

        Status = WorkOrderStatus.InProgress;
    }

    /// <summary>
    /// ST-WO-003 Submit Work Summary. Only from IN_PROGRESS. Aggregate-local guard only: eligibility (the caller
    /// is the assigned Technician of a checked-out Visit on this Work Order) and the Work Summary's own field
    /// validation are both the Application service's responsibility before this is called. Per `docs/13` §4.15
    /// Decision 2 (Portfolio Project Owner directive), the actor is the Technician, not the baseline's "Team
    /// Lead" — enforced by the API authorization policy, not here.
    /// </summary>
    public void SubmitWorkSummary()
    {
        if (!WorkOrderStatusTransitions.IsAllowed(Status, WorkOrderStatus.AwaitingSupervisorReview))
        {
            throw new DomainRuleViolationException("Only an IN_PROGRESS Work Order can have its Work Summary submitted.");
        }

        Status = WorkOrderStatus.AwaitingSupervisorReview;
    }

    /// <summary>
    /// ST-WO-004 Submit for Acceptance. Only from AWAITING_SUPERVISOR_REVIEW. Per `docs/13` §4.15 Decision 2
    /// (Portfolio Project Owner directive), either the Team Lead or the Supervisor may perform this single review
    /// step, not the baseline's "Supervisor" alone — enforced by the API authorization policy, not here.
    /// </summary>
    public void SubmitForAcceptance()
    {
        if (!WorkOrderStatusTransitions.IsAllowed(Status, WorkOrderStatus.AwaitingCustomerAcceptance))
        {
            throw new DomainRuleViolationException("Only an AWAITING_SUPERVISOR_REVIEW Work Order can be submitted for acceptance.");
        }

        Status = WorkOrderStatus.AwaitingCustomerAcceptance;
    }
}
