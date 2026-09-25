using RepairRequest.Domain.Common;

namespace RepairRequest.Domain.WorkOrders;

/// <summary>
/// Corrective Action cycle (`docs/02` CA-001/003..012; `docs/07` §3 "CustomerAcceptance 1:0..1 CorrectiveAction —
/// REJECT creates one corrective cycle"; `docs/13` §4.17). Only <see cref="CreateDraft"/> is exercised in this
/// ticket's scope (ST-CA-001) — the plan/approve/rework/resubmit cycle (ST-CA-002/003 and beyond) belongs to a
/// future ticket, so every field of that later half is nullable here and populated by nothing yet.
/// <see cref="OwnerTeamLeadId"/> (CA-007) is documented "Y" (required) in the baseline, but nothing in ST-CA-001
/// or UC-WO-022 assigns a Team Lead at DRAFT-creation time — that assignment is ST-CA-002's own step ("Submit
/// Plan", Team Lead actor). Per `docs/13` §4.17 (recorded, not silently applied), this field is a directed
/// deviation to nullable-for-now, mirroring the established "trim a guard, defer, record it" pattern already used
/// for BR-06's Check-out split and Ticket 2's Close guard.
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

    /// <summary>CA-006. Closed three-value allowlist — see <see cref="CorrectiveActionStatus"/>. Always DRAFT in this ticket's scope.</summary>
    public CorrectiveActionStatus Status { get; private set; }

    /// <summary>CA-007. Nullable-for-now deviation — see class remarks.</summary>
    public Guid? OwnerTeamLeadId { get; private set; }

    /// <summary>CA-008. Required only before Submit Plan (future ticket).</summary>
    public string? PlanText { get; private set; }

    /// <summary>CA-009. Required only before Submit Plan (future ticket).</summary>
    public Guid? PlanFileAssetId { get; private set; }

    /// <summary>CA-010. Set only on Supervisor approval (future ticket).</summary>
    public Guid? ApprovedBy { get; private set; }

    /// <summary>CA-011. Set only on Supervisor approval (future ticket).</summary>
    public DateTime? ApprovedAt { get; private set; }

    /// <summary>CA-012. Set only once the corrective Service Visit is scheduled (future ticket).</summary>
    public Guid? CorrectiveServiceVisitId { get; private set; }
}
