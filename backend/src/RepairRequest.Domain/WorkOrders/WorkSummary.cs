using RepairRequest.Domain.Common;

namespace RepairRequest.Domain.WorkOrders;

/// <summary>
/// Work Summary (RR-DD-001 WSM-001/003..007; UC-WO-020; ST-WO-003). One row per Work Order in this ticket's
/// scope — <see cref="RevisionNo"/> is always 1, since no resubmission/revise cycle is implemented yet (only the
/// single Technician-submit, Team-Lead-or-Supervisor-review flow is in scope; `docs/13` §4.15). The unique
/// (ServiceVisitId, RevisionNo) index this maps to is the DB-level backstop for that invariant, matching
/// WSM-005's own "Unique per Visit revision" description. <see cref="RepairOutcomeCode"/> is a closed six-value
/// allowlist (<see cref="WorkOrders.RepairOutcomeCode"/>) per Portfolio Project Owner directive — RR-DD-001
/// names this "Active Outcome" but defines no master-data table or value list in the baseline itself; parsing
/// and rejecting an unknown code is the Application service's responsibility before this is called, so by the
/// time this constructor runs the value is already a valid enum member.
/// </summary>
public sealed class WorkSummary
{
    public const int SummaryTextMaxLength = 4000;

    /// <summary>EF Core materialization.</summary>
    private WorkSummary()
    {
    }

    private WorkSummary(Guid tenantId, Guid workOrderId, Guid serviceVisitId, int revisionNo, string summaryText, RepairOutcomeCode repairOutcomeCode)
    {
        TenantId = DomainGuard.NotEmpty(tenantId, nameof(tenantId));
        WorkOrderId = DomainGuard.NotEmpty(workOrderId, nameof(workOrderId));
        ServiceVisitId = DomainGuard.NotEmpty(serviceVisitId, nameof(serviceVisitId));
        RevisionNo = revisionNo;
        SummaryText = DomainGuard.RequiredText(summaryText, SummaryTextMaxLength, nameof(summaryText));
        RepairOutcomeCode = repairOutcomeCode;
    }

    /// <summary>
    /// ST-WO-003 Submit Work Summary (UC-WO-020). <paramref name="summaryText"/> is required (WSM-006 "Y@Check-out");
    /// trimming and blank/length checks are the Application service's responsibility before this is called.
    /// <paramref name="repairOutcomeCode"/> is already a parsed, valid allowlist member by this point.
    /// </summary>
    public static WorkSummary Create(Guid tenantId, Guid workOrderId, Guid serviceVisitId, string summaryText, RepairOutcomeCode repairOutcomeCode) =>
        new(tenantId, workOrderId, serviceVisitId, revisionNo: 1, summaryText, repairOutcomeCode);

    /// <summary>WSM-001. Assigned on insert (sequential GUID).</summary>
    public Guid Id { get; private set; }

    /// <summary>Tenant scope; server-derived; immutable.</summary>
    public Guid TenantId { get; private set; }

    /// <summary>WSM-003. FK to the Work Order this summary closes out.</summary>
    public Guid WorkOrderId { get; private set; }

    /// <summary>WSM-004. FK to the checked-out Service Visit the summary describes.</summary>
    public Guid ServiceVisitId { get; private set; }

    /// <summary>WSM-005. Always 1 in this ticket's scope (see class remarks).</summary>
    public int RevisionNo { get; private set; }

    /// <summary>WSM-006. Required, non-blank, at most <see cref="SummaryTextMaxLength"/> characters.</summary>
    public string SummaryText { get; private set; } = null!;

    /// <summary>WSM-007. Closed six-value allowlist — see class remarks.</summary>
    public RepairOutcomeCode RepairOutcomeCode { get; private set; }

    /// <summary>Optimistic concurrency token (not used as an If-Match target in this ticket — see <see cref="WorkOrder.RowVersion"/>).</summary>
    public byte[] RowVersion { get; private set; } = [];
}
