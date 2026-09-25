using RepairRequest.Domain.Common;

namespace RepairRequest.Domain.WorkOrders;

/// <summary>
/// Customer Acceptance decision history (`docs/02` ACC-001/003..007/009; `docs/07` §3 "WorkOrder 1:N
/// CustomerAcceptance"; `docs/13` §4.17 schema decision). Append-only: one row per decision, never updated or
/// deleted, so a rejected-then-corrected Work Order keeps its full round-by-round trace. <see cref="TenantId"/>
/// is not a documented ACC-* field, but every other aggregate in this codebase carries it at the same
/// undocumented "-002" slot (RR-002, WO-002, SV-002, NTF-002, and <see cref="WorkSummary"/>'s own TenantId) — an
/// established technical convention, not a guessed business rule.
/// </summary>
public sealed class CustomerAcceptance
{
    public const int DecisionReasonMaxLength = 1000;

    /// <summary>EF Core materialization.</summary>
    private CustomerAcceptance()
    {
    }

    private CustomerAcceptance(
        Guid tenantId, Guid workOrderId, int acceptanceRoundNo, Guid acceptanceContactId, AcceptanceDecision decision, string? decisionReason, DateTime decidedAt)
    {
        TenantId = DomainGuard.NotEmpty(tenantId, nameof(tenantId));
        WorkOrderId = DomainGuard.NotEmpty(workOrderId, nameof(workOrderId));
        AcceptanceRoundNo = acceptanceRoundNo;
        AcceptanceContactId = DomainGuard.NotEmpty(acceptanceContactId, nameof(acceptanceContactId));
        Decision = decision;
        DecisionReason = DomainGuard.OptionalText(decisionReason, DecisionReasonMaxLength, nameof(decisionReason));
        DecidedAt = DomainGuard.Utc(decidedAt, nameof(decidedAt));
    }

    /// <summary>
    /// ST-WO-005 Accept (UC-WO-021). <paramref name="acceptanceRoundNo"/> is the Application service's
    /// responsibility (count of this Work Order's existing rows + 1) — the UNIQUE(work_order_id, round_no) index
    /// is the DB-level backstop against a race producing a duplicate round.
    /// </summary>
    public static CustomerAcceptance Accept(Guid tenantId, Guid workOrderId, int acceptanceRoundNo, Guid acceptanceContactId, DateTime decidedAt) =>
        new(tenantId, workOrderId, acceptanceRoundNo, acceptanceContactId, AcceptanceDecision.Accept, decisionReason: null, decidedAt);

    /// <summary>
    /// ST-WO-007 Reject (UC-WO-022; BR-07/BR-15). <paramref name="decisionReason"/> is required (ACC-007 "Required
    /// REJECT") — non-blank/length validation is the Application service's responsibility before this is called.
    /// </summary>
    public static CustomerAcceptance Reject(Guid tenantId, Guid workOrderId, int acceptanceRoundNo, Guid acceptanceContactId, string decisionReason, DateTime decidedAt) =>
        new(tenantId, workOrderId, acceptanceRoundNo, acceptanceContactId, AcceptanceDecision.Reject, DomainGuard.RequiredText(decisionReason, DecisionReasonMaxLength, nameof(decisionReason)), decidedAt);

    /// <summary>ACC-001. Assigned on insert (sequential GUID).</summary>
    public Guid Id { get; private set; }

    /// <summary>Tenant scope; server-derived; immutable — see class remarks.</summary>
    public Guid TenantId { get; private set; }

    /// <summary>ACC-003. FK to the Work Order this decision was made on.</summary>
    public Guid WorkOrderId { get; private set; }

    /// <summary>ACC-004. Unique per Work Order (1-based).</summary>
    public int AcceptanceRoundNo { get; private set; }

    /// <summary>ACC-005. Must match the Work Order's designated <see cref="WorkOrder.AcceptanceContactId"/> at the time of the decision.</summary>
    public Guid AcceptanceContactId { get; private set; }

    /// <summary>ACC-006.</summary>
    public AcceptanceDecision Decision { get; private set; }

    /// <summary>ACC-007. Required on Reject; always null on Accept.</summary>
    public string? DecisionReason { get; private set; }

    /// <summary>ACC-009. Server time.</summary>
    public DateTime DecidedAt { get; private set; }
}
