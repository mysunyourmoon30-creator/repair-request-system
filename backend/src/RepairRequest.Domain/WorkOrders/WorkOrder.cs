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
    /// Begins work, triggered by the assigned Technician's Check-in on one of this Work Order's Service Visits.
    /// Two source states are allowed: ST-WO-002 (S3-001; UC-WO-016; BR-05) SCHEDULED -&gt; IN_PROGRESS for the
    /// initial Visit, and ST-WO-010 (`docs/13` §4.23) CORRECTIVE_PLAN_APPROVED -&gt; IN_PROGRESS ("Start Rework")
    /// for a corrective Visit — both fire through this same method, since the underlying guard
    /// (<see cref="WorkOrderStatusTransitions"/>) and Check-in flow are identical either way. Aggregate-local
    /// guard only: current state must allow a transition to IN_PROGRESS.
    /// </summary>
    public void BeginWork()
    {
        if (!WorkOrderStatusTransitions.IsAllowed(Status, WorkOrderStatus.InProgress))
        {
            throw new DomainRuleViolationException("Only a SCHEDULED Work Order, or one with an APPROVED corrective plan, can begin work.");
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
    /// step, not the baseline's "Supervisor" alone — enforced by the API authorization policy, not here. Per
    /// `docs/13` §4.16, the caller also designates the Acceptance Contact (WO-008/WO-009) in this same call —
    /// eligibility (existing REQUESTER user, correct tenant and Site scope) is the Application service's
    /// responsibility before this is called; this method only requires both non-empty. Once set here, neither
    /// field has any other mutator in this ticket's scope, so the contact cannot be changed afterward.
    /// </summary>
    public void SubmitForAcceptance(Guid acceptanceContactId, string acceptanceContactSnapshot)
    {
        if (!WorkOrderStatusTransitions.IsAllowed(Status, WorkOrderStatus.AwaitingCustomerAcceptance))
        {
            throw new DomainRuleViolationException("Only an AWAITING_SUPERVISOR_REVIEW Work Order can be submitted for acceptance.");
        }

        AcceptanceContactId = DomainGuard.NotEmpty(acceptanceContactId, nameof(acceptanceContactId));
        AcceptanceContactSnapshot = DomainGuard.RequiredText(acceptanceContactSnapshot, int.MaxValue, nameof(acceptanceContactSnapshot));
        Status = WorkOrderStatus.AwaitingCustomerAcceptance;
    }

    /// <summary>
    /// ST-WO-005 Accept (UC-WO-021; `docs/13` §4.16). Only from AWAITING_CUSTOMER_ACCEPTANCE. Aggregate-local
    /// guard only: eligibility (the caller is exactly <see cref="AcceptanceContactId"/>, a resource-specific
    /// check, not a role-scoped query) is the Application service's responsibility before this is called.
    /// </summary>
    public void Accept()
    {
        if (!WorkOrderStatusTransitions.IsAllowed(Status, WorkOrderStatus.Completed))
        {
            throw new DomainRuleViolationException("Only a Work Order awaiting customer acceptance can be accepted.");
        }

        Status = WorkOrderStatus.Completed;
    }

    /// <summary>
    /// ST-WO-007 Reject (UC-WO-022; BR-07/BR-15; `docs/13` §4.17). Only from AWAITING_CUSTOMER_ACCEPTANCE.
    /// Aggregate-local guard only: eligibility (the caller is exactly <see cref="AcceptanceContactId"/>) and the
    /// decision reason's own validation are both the Application service's responsibility before this is called.
    /// </summary>
    public void Reject()
    {
        if (!WorkOrderStatusTransitions.IsAllowed(Status, WorkOrderStatus.CorrectiveActionRequired))
        {
            throw new DomainRuleViolationException("Only a Work Order awaiting customer acceptance can be rejected.");
        }

        Status = WorkOrderStatus.CorrectiveActionRequired;
    }

    /// <summary>
    /// ST-WO-008 Submit Corrective Plan (`docs/13` §4.21; Ticket 6). Only from CORRECTIVE_ACTION_REQUIRED.
    /// Aggregate-local guard only: the Corrective Action's own guard (its status must be DRAFT) and the caller's
    /// Team Lead role/scope are the Application service's responsibility before this is called. This transition
    /// exists on <see cref="WorkOrder"/>, not <see cref="CorrectiveAction"/>, because the resource whose status
    /// actually advances for concurrency purposes is the Work Order — see `docs/13` §4.21's own concurrency note.
    /// </summary>
    public void SubmitCorrectivePlan()
    {
        if (!WorkOrderStatusTransitions.IsAllowed(Status, WorkOrderStatus.CorrectivePlanPending))
        {
            throw new DomainRuleViolationException("Only a Work Order awaiting a corrective action can have its plan submitted.");
        }

        Status = WorkOrderStatus.CorrectivePlanPending;
    }

    /// <summary>
    /// ST-WO-009 Approve Corrective Plan (`docs/13` §4.21; Ticket 6). Only from CORRECTIVE_PLAN_PENDING.
    /// Aggregate-local guard only: the Corrective Action's own guard (its status must be PENDING_PLAN_APPROVAL) and
    /// the caller's Supervisor role/scope are the Application service's responsibility before this is called.
    /// </summary>
    public void ApproveCorrectivePlan()
    {
        if (!WorkOrderStatusTransitions.IsAllowed(Status, WorkOrderStatus.CorrectivePlanApproved))
        {
            throw new DomainRuleViolationException("Only a Work Order with a submitted corrective plan can have its plan approved.");
        }

        Status = WorkOrderStatus.CorrectivePlanApproved;
    }

    /// <summary>
    /// ST-WO-006 Close (BR-08; `docs/13` §4.20). Only from COMPLETED. Aggregate-local guard only: the data
    /// prerequisites (a current Work Summary that was reviewed, a customer ACCEPT on the current submission, and a
    /// reviewed Cost Summary) and the caller's Supervisor role/scope are the Application service's responsibility
    /// before this is called. <paramref name="closedBy"/> and <paramref name="closedAt"/> (WO-011/WO-012) are
    /// server-derived, never client input. Terminal: there is no Reopen in MVP.
    /// </summary>
    public void Close(Guid closedBy, DateTime closedAt)
    {
        if (!WorkOrderStatusTransitions.IsAllowed(Status, WorkOrderStatus.Closed))
        {
            throw new DomainRuleViolationException("Only a COMPLETED Work Order can be closed.");
        }

        // Validate both before assigning either, so a bad argument never leaves the aggregate half-closed.
        var validatedClosedBy = DomainGuard.NotEmpty(closedBy, nameof(closedBy));
        var validatedClosedAt = DomainGuard.Utc(closedAt, nameof(closedAt));

        ClosedBy = validatedClosedBy;
        ClosedAt = validatedClosedAt;
        Status = WorkOrderStatus.Closed;
    }

    /// <summary>
    /// ST-WO-011 Cancel (UC-WO-026; BR-09/BR-12; `docs/13` §4.25). Allowed from every non-terminal, pre-Accept
    /// status the transition matrix lists (OPEN/SCHEDULED/IN_PROGRESS/AWAITING_SUPERVISOR_REVIEW/
    /// AWAITING_CUSTOMER_ACCEPTANCE/CORRECTIVE_ACTION_REQUIRED/CORRECTIVE_PLAN_PENDING/CORRECTIVE_PLAN_APPROVED);
    /// denied once COMPLETED, CLOSED or already CANCELLED. Aggregate-local guard only: the caller's Supervisor
    /// role/scope and the "no Service Visit currently IN_PROGRESS" guard (`docs/13` §4.25 Decision — Cancel must
    /// deny, unchanged, rather than force-terminate an active Work Session) are the Application service's
    /// responsibility before this is called. Cascading the cancellation to this Work Order's still-SCHEDULED
    /// Service Visits, and leaving any in-flight Corrective Action row as unmodified history, are likewise handled
    /// by the Application service — this method only ever touches the Work Order's own status and reason.
    /// </summary>
    public void Cancel(string reason)
    {
        if (!WorkOrderStatusTransitions.IsAllowed(Status, WorkOrderStatus.Cancelled))
        {
            throw new DomainRuleViolationException("This Work Order cannot be cancelled from its current status.");
        }

        CancelReason = DomainGuard.RequiredText(reason, ReasonMaxLength, nameof(reason));
        Status = WorkOrderStatus.Cancelled;
    }
}
