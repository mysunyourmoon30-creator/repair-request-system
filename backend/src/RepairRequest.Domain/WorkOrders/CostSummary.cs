using RepairRequest.Domain.Common;

namespace RepairRequest.Domain.WorkOrders;

/// <summary>
/// Cost Summary (`docs/05` CST-001/003..011; BR-08; ST-WO-006's own guard; `docs/13` §4.18). Exactly one row per
/// Work Order (CST-003 "unique current summary"; `docs/07` ER "WorkOrder 1:0..1 CostSummary") — mutated in place
/// by repeated Prepare calls, not revisioned like <see cref="WorkSummary"/>. No status enum exists in the
/// baseline for Cost Summary itself (confirmed absent from `docs/03`'s own "Object -&gt; Allowed states" table) —
/// per Portfolio Project Owner directive, <see cref="ReviewedAt"/> being null/non-null is the only state signal:
/// null means prepared-but-unreviewed, non-null means reviewed. Nothing in this ticket's scope (Cost Summary
/// Prepare only) ever sets <see cref="ReviewedBy"/>/<see cref="ReviewedAt"/> — that is the future Review ticket's
/// sole responsibility; the guard here exists only so an already-reviewed row can never be silently overwritten
/// once that ticket ships.
/// </summary>
public sealed class CostSummary
{
    public const int CurrencyCodeLength = 3;
    public const int NoteMaxLength = 2000;

    /// <summary>EF Core materialization.</summary>
    private CostSummary()
    {
    }

    private CostSummary(Guid tenantId, Guid workOrderId, decimal totalAmount, string currencyCode, string? note, Guid preparedBy, DateTime preparedAt)
    {
        TenantId = DomainGuard.NotEmpty(tenantId, nameof(tenantId));
        WorkOrderId = DomainGuard.NotEmpty(workOrderId, nameof(workOrderId));
        SetPrepared(totalAmount, currencyCode, note, preparedBy, preparedAt);
    }

    /// <summary>
    /// First-time Prepare (CST-API-001; Team Lead). <paramref name="totalAmount"/>/<paramref name="currencyCode"/>
    /// non-negative/format validation is the Application service's responsibility before this is called.
    /// </summary>
    public static CostSummary Create(Guid tenantId, Guid workOrderId, decimal totalAmount, string currencyCode, string? note, Guid preparedBy, DateTime preparedAt) =>
        new(tenantId, workOrderId, totalAmount, currencyCode, note, preparedBy, preparedAt);

    /// <summary>
    /// Re-Prepare (edit) an existing, not-yet-reviewed row. Only from a null <see cref="ReviewedAt"/> — once
    /// reviewed, this Cost Summary is immutable to Prepare (decision: Prepare/Update after Review returns 409).
    /// </summary>
    public void UpdatePreparation(decimal totalAmount, string currencyCode, string? note, Guid preparedBy, DateTime preparedAt)
    {
        if (ReviewedAt is not null)
        {
            throw new DomainRuleViolationException("This Work Order's Cost Summary has already been reviewed.");
        }

        SetPrepared(totalAmount, currencyCode, note, preparedBy, preparedAt);
    }

    private void SetPrepared(decimal totalAmount, string currencyCode, string? note, Guid preparedBy, DateTime preparedAt)
    {
        TotalAmount = totalAmount;
        CurrencyCode = DomainGuard.RequiredText(currencyCode, CurrencyCodeLength, nameof(currencyCode));
        Note = DomainGuard.OptionalText(note, NoteMaxLength, nameof(note));
        PreparedBy = DomainGuard.NotEmpty(preparedBy, nameof(preparedBy));
        PreparedAt = DomainGuard.Utc(preparedAt, nameof(preparedAt));
    }

    /// <summary>
    /// CST-API-002 (`docs/13` §4.19; Supervisor only). Only from a null <see cref="ReviewedAt"/> — a second
    /// Review is rejected here too, defense-in-depth alongside the Application service's own check. Eligibility
    /// (caller holds Supervisor, is not <see cref="PreparedBy"/>, current Site scope) is the Application
    /// service's responsibility before this is called.
    /// </summary>
    public void Review(Guid reviewedBy, DateTime reviewedAt)
    {
        if (ReviewedAt is not null)
        {
            throw new DomainRuleViolationException("This Work Order's Cost Summary has already been reviewed.");
        }

        ReviewedBy = DomainGuard.NotEmpty(reviewedBy, nameof(reviewedBy));
        ReviewedAt = DomainGuard.Utc(reviewedAt, nameof(reviewedAt));
    }

    /// <summary>CST-001. Assigned on insert (sequential GUID).</summary>
    public Guid Id { get; private set; }

    /// <summary>Tenant scope; server-derived; immutable — same undocumented "-002 slot" convention as every other aggregate in this codebase (e.g. <see cref="CustomerAcceptance"/>'s own remarks).</summary>
    public Guid TenantId { get; private set; }

    /// <summary>CST-003. Unique FK — exactly one Cost Summary per Work Order.</summary>
    public Guid WorkOrderId { get; private set; }

    /// <summary>CST-004. Required, &gt;=0.</summary>
    public decimal TotalAmount { get; private set; }

    /// <summary>CST-005. Required, ISO currency format (3 letters) — no ISO-4217 master list exists in this codebase; format only, not membership.</summary>
    public string CurrencyCode { get; private set; } = null!;

    /// <summary>CST-006. Conditional (optional).</summary>
    public string? Note { get; private set; }

    /// <summary>CST-007. The Team Lead who most recently prepared/edited this row.</summary>
    public Guid PreparedBy { get; private set; }

    /// <summary>CST-008. Server time of the most recent Prepare/edit.</summary>
    public DateTime PreparedAt { get; private set; }

    /// <summary>CST-009. Null until the future Review ticket sets it. Never mutated by anything in this ticket's scope.</summary>
    public Guid? ReviewedBy { get; private set; }

    /// <summary>CST-010. Null means prepared-but-unreviewed; non-null means reviewed. Never mutated by anything in this ticket's scope.</summary>
    public DateTime? ReviewedAt { get; private set; }

    /// <summary>CST-011. Optimistic concurrency token for this row itself (see class remarks on the two-phase If-Match design in `docs/13` §4.18).</summary>
    public byte[] RowVersion { get; private set; } = [];
}
