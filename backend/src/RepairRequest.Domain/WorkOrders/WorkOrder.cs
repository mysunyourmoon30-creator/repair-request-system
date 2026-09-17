using RepairRequest.Domain.Common;

namespace RepairRequest.Domain.WorkOrders;

/// <summary>
/// Work Order aggregate root (RR-DD-001 WO-001..013; RR-DBD-001 work_order).
/// S2-001 establishes the persisted shape for the List/Detail read endpoints only; Convert
/// (ST-RR-008/UC-WO-001), Schedule, Reassign and every later transition are out of scope
/// (DEC-S2-001-05, `docs/12` Section 11). <see cref="CreatedAt"/> is a technical ordering
/// column, not one of the documented WO-001..013 business fields (DEC-S2-001-04).
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
    /// originating Repair Request and the generated Work Order No. are server-derived, never client input. This
    /// factory exists for the Convert command (not yet implemented) and for S2-001 test seeding.
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
}
