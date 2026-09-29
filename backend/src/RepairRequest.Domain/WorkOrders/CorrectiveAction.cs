using RepairRequest.Domain.Common;

namespace RepairRequest.Domain.WorkOrders;

/// <summary>
/// Corrective Action cycle (`docs/02` CA-001/003..012; `docs/07` §3 "CustomerAcceptance 1:0..1 CorrectiveAction —
/// REJECT creates one corrective cycle"; `docs/13` §4.17/§4.21/§4.22). <see cref="CreateDraft"/> (ST-CA-001,
/// Ticket 3), <see cref="SubmitPlan"/> (ST-CA-002, Ticket 6), <see cref="ApprovePlan"/> (ST-CA-003, Ticket 6) and
/// <see cref="ScheduleRework"/> (CA-API-003, `docs/13` §4.22) are implemented; resubmission/re-review/re-acceptance
/// beyond a scheduled rework (`UC-WO-024`) belongs to a future ticket. <see cref="CorrectiveServiceVisitId"/> stays
/// nullable — null until <see cref="ScheduleRework"/> runs, set once, never cleared.
/// <see cref="OwnerTeamLeadId"/> (CA-007) is documented "Y" (required) in the baseline, but nothing in ST-CA-001
/// or UC-WO-022 assigns a Team Lead at DRAFT-creation time. Per `docs/13` §4.21 Decision (a) (Portfolio Project
/// Owner directive, recorded not silently applied), it stays null through DRAFT and is bound atomically, once, by
/// <see cref="SubmitPlan"/> to the authorized submitting Team Lead — mirroring the established
/// "assign on first use" precedent already used for <see cref="WorkOrder.AcceptanceContactId"/>. No other mutator
/// exists for it, and the Corrective Action's own status guard (only reachable from <see cref="CorrectiveActionStatus.Draft"/>)
/// makes a second assignment unreachable, so no unaudited owner change is possible.
/// </summary>
public sealed class CorrectiveAction
{
    public const int PlanTextMaxLength = 4000;

    /// <summary>EF Core materialization.</summary>
    private CorrectiveAction()
    {
    }

    private CorrectiveAction(Guid tenantId, Guid workOrderId, Guid acceptanceId, int cycleNo)
    {
        TenantId = DomainGuard.NotEmpty(tenantId, nameof(tenantId));
        WorkOrderId = DomainGuard.NotEmpty(workOrderId, nameof(workOrderId));
        AcceptanceId = DomainGuard.NotEmpty(acceptanceId, nameof(acceptanceId));
        CycleNo = cycleNo;
        Status = CorrectiveActionStatus.Draft;
    }

    /// <summary>
    /// ST-CA-001 (UC-WO-022; system-triggered, "Acceptance decision=REJECT"). <paramref name="acceptanceId"/> is
    /// the REJECT <see cref="CustomerAcceptance"/> row that caused this cycle (CA-004 "FK rejected acceptance").
    /// <paramref name="cycleNo"/> is the Application service's responsibility (count of this Work Order's
    /// existing rows + 1) — the UNIQUE(work_order_id, cycle_no) index is the DB-level backstop.
    /// </summary>
    public static CorrectiveAction CreateDraft(Guid tenantId, Guid workOrderId, Guid acceptanceId, int cycleNo) =>
        new(tenantId, workOrderId, acceptanceId, cycleNo);

    /// <summary>
    /// ST-CA-002 Submit Plan (CA-API-001; `docs/13` §4.21 Decision a). Only from DRAFT. Aggregate-local guard
    /// only: eligibility (caller holds Team Lead, current Site scope) is the Application service's responsibility
    /// before this is called. <paramref name="ownerTeamLeadId"/> is bound here, atomically, to the calling Team
    /// Lead — the field's only mutator, ever. <paramref name="planText"/>/<paramref name="planFileAssetId"/>
    /// non-blank/length validation is the Application service's responsibility before this is called.
    /// </summary>
    public void SubmitPlan(Guid ownerTeamLeadId, string planText, Guid planFileAssetId)
    {
        if (!CorrectiveActionStatusTransitions.IsAllowed(Status, CorrectiveActionStatus.PendingPlanApproval))
        {
            throw new DomainRuleViolationException("Only a DRAFT Corrective Action can have its plan submitted.");
        }

        // Validate every argument before assigning any, so a bad one never leaves the aggregate half-submitted.
        var validatedOwnerTeamLeadId = DomainGuard.NotEmpty(ownerTeamLeadId, nameof(ownerTeamLeadId));
        var validatedPlanText = DomainGuard.RequiredText(planText, PlanTextMaxLength, nameof(planText));
        var validatedPlanFileAssetId = DomainGuard.NotEmpty(planFileAssetId, nameof(planFileAssetId));

        OwnerTeamLeadId = validatedOwnerTeamLeadId;
        PlanText = validatedPlanText;
        PlanFileAssetId = validatedPlanFileAssetId;
        Status = CorrectiveActionStatus.PendingPlanApproval;
    }

    /// <summary>
    /// ST-CA-003 Approve Plan (CA-API-002; `docs/13` §4.21). Only from PENDING_PLAN_APPROVAL. Aggregate-local
    /// guard only: eligibility (caller holds Supervisor, current Site scope) is the Application service's
    /// responsibility before this is called. No Separation of Duties is enforced here — `docs/13` §4.21 Decision
    /// (d) records the absence of a same-user guard as a review risk, not a baseline requirement.
    /// </summary>
    public void ApprovePlan(Guid approvedBy, DateTime approvedAt)
    {
        if (!CorrectiveActionStatusTransitions.IsAllowed(Status, CorrectiveActionStatus.Approved))
        {
            throw new DomainRuleViolationException("Only a Corrective Action with a submitted plan can be approved.");
        }

        // Validate both before assigning either, so a bad argument never leaves the aggregate half-approved.
        var validatedApprovedBy = DomainGuard.NotEmpty(approvedBy, nameof(approvedBy));
        var validatedApprovedAt = DomainGuard.Utc(approvedAt, nameof(approvedAt));

        ApprovedBy = validatedApprovedBy;
        ApprovedAt = validatedApprovedAt;
        Status = CorrectiveActionStatus.Approved;
    }

    /// <summary>
    /// CA-API-003 Schedule Rework (`docs/13` §4.22; Portfolio Project Owner directive — Coordinator only, not a
    /// baseline-literal actor). Only from APPROVED, and only once — <see cref="CorrectiveServiceVisitId"/> is a
    /// single nullable FK, not a collection, so a second call is rejected rather than silently overwriting the
    /// first Visit's link. Aggregate-local guard only: eligibility (caller holds Coordinator, current Site scope),
    /// the linked Service Visit's own creation (team/technician/window) and the Work Order's own status are all
    /// the Application service's responsibility before this is called. Deliberately does <b>not</b> touch
    /// <see cref="CorrectiveActionStatus"/> or the Work Order's status — baseline `ST-WO-010` ("Start Rework")
    /// is a separate, not-yet-implemented transition fired later by the assigned Technician, guarded by "Corrective
    /// Visit assigned/scheduled" (`docs/03` §3) — i.e. by this method having already run.
    /// </summary>
    public void ScheduleRework(Guid correctiveServiceVisitId)
    {
        if (Status != CorrectiveActionStatus.Approved)
        {
            throw new DomainRuleViolationException("Only an APPROVED Corrective Action can have its rework scheduled.");
        }

        if (CorrectiveServiceVisitId is not null)
        {
            throw new DomainRuleViolationException("This Corrective Action's rework has already been scheduled.");
        }

        CorrectiveServiceVisitId = DomainGuard.NotEmpty(correctiveServiceVisitId, nameof(correctiveServiceVisitId));
    }

    /// <summary>CA-001. Assigned on insert (sequential GUID).</summary>
    public Guid Id { get; private set; }

    /// <summary>Tenant scope; server-derived; immutable — see <see cref="CustomerAcceptance"/>'s own remarks for the "-002 slot" convention.</summary>
    public Guid TenantId { get; private set; }

    /// <summary>CA-003. FK to the Work Order this cycle belongs to.</summary>
    public Guid WorkOrderId { get; private set; }

    /// <summary>CA-004. FK to the rejected <see cref="CustomerAcceptance"/> row.</summary>
    public Guid AcceptanceId { get; private set; }

    /// <summary>CA-005. Unique per Work Order (1-based).</summary>
    public int CycleNo { get; private set; }

    /// <summary>CA-006. Closed three-value allowlist — see <see cref="CorrectiveActionStatus"/>.</summary>
    public CorrectiveActionStatus Status { get; private set; }

    /// <summary>CA-007. Null through DRAFT; bound once, atomically, by <see cref="SubmitPlan"/> — see class remarks.</summary>
    public Guid? OwnerTeamLeadId { get; private set; }

    /// <summary>CA-008. Set by <see cref="SubmitPlan"/>.</summary>
    public string? PlanText { get; private set; }

    /// <summary>CA-009. Set by <see cref="SubmitPlan"/>.</summary>
    public Guid? PlanFileAssetId { get; private set; }

    /// <summary>CA-010. Set by <see cref="ApprovePlan"/>.</summary>
    public Guid? ApprovedBy { get; private set; }

    /// <summary>CA-011. Set by <see cref="ApprovePlan"/>.</summary>
    public DateTime? ApprovedAt { get; private set; }

    /// <summary>CA-012. Set only once, by <see cref="ScheduleRework"/> (`docs/13` §4.22).</summary>
    public Guid? CorrectiveServiceVisitId { get; private set; }

    /// <summary>
    /// Optimistic concurrency token for this row itself (`docs/13` §4.22). Absent until this ticket — Submit/Approve
    /// Plan never needed one because they always also change the Work Order's own Status, so its RowVersion gave a
    /// real compare-and-swap. <see cref="ScheduleRework"/> changes neither, so it needs its own token, the same
    /// reason <see cref="CostSummary.RowVersion"/> exists for that aggregate's own re-edit case.
    /// </summary>
    public byte[] RowVersion { get; private set; } = [];
}
