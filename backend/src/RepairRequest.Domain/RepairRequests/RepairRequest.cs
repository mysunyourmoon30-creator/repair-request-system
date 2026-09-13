using RepairRequest.Domain.Common;

namespace RepairRequest.Domain.RepairRequests;

/// <summary>
/// Repair Request aggregate root (RR-DD-001 RR-001..RR-020; RR-DBD-001 repair_request).
/// S1-001 establishes the persisted shape and the initial DRAFT state; S1-005 adds Draft editing
/// (ST-RR-001). Submit, review and cancel commands (ST-RR-002..007) are added by later Sprint 1
/// tickets; CONVERTED (ST-RR-008) exists for lifecycle compatibility but Work Order creation is
/// Sprint 2 scope.
/// </summary>
public sealed class RepairRequest
{
    public const int RequestNoMaxLength = 30;
    public const int RequestCategoryCodeMaxLength = 30;
    public const int DescriptionMaxLength = 2000;
    public const int PriorityCodeMaxLength = 20;
    public const int ReasonMaxLength = 1000;

    /// <summary>EF Core materialization.</summary>
    private RepairRequest()
    {
    }

    private RepairRequest(Guid tenantId, Guid createdBy)
    {
        TenantId = DomainGuard.NotEmpty(tenantId, nameof(tenantId));
        CreatedBy = DomainGuard.NotEmpty(createdBy, nameof(createdBy));
        Status = RepairRequestStatus.Draft;
    }

    /// <summary>
    /// Starts a new DRAFT owned by the authenticated Requester. Tenant and actor are
    /// server-derived (D-11 / BR-16) and are never accepted from client input.
    /// </summary>
    public static RepairRequest CreateDraft(Guid tenantId, Guid createdBy) => new(tenantId, createdBy);

    /// <summary>RR-001. Assigned on insert (sequential GUID).</summary>
    public Guid Id { get; private set; }

    /// <summary>RR-002. Customer/Company scope; server-derived; immutable.</summary>
    public Guid TenantId { get; private set; }

    /// <summary>RR-003. Unique; generated at SUBMITTED; immutable.</summary>
    public string? RequestNo { get; private set; }

    /// <summary>RR-004.</summary>
    public RepairRequestStatus Status { get; private set; }

    /// <summary>RR-005. Required at Submit.</summary>
    public Guid? SiteId { get; private set; }

    /// <summary>RR-006. Conditional; must belong to the selected Site.</summary>
    public Guid? EquipmentId { get; private set; }

    /// <summary>RR-007. Conditional; must belong to the selected Site. Location master is not Sprint 1 scope.</summary>
    public Guid? LocationId { get; private set; }

    /// <summary>RR-008. Required at Submit.</summary>
    public string? RequestCategoryCode { get; private set; }

    /// <summary>RR-009. Required at Submit; non-blank.</summary>
    public string? Description { get; private set; }

    /// <summary>RR-010. Required at Submit.</summary>
    public string? PriorityCode { get; private set; }

    /// <summary>RR-011. Required when the BR-14 duplicate warning is overridden.</summary>
    public string? DuplicateContinuationReason { get; private set; }

    /// <summary>RR-012. Requester identity; derived actor; immutable.</summary>
    public Guid CreatedBy { get; private set; }

    /// <summary>RR-013. Derived actor on Submit.</summary>
    public Guid? SubmittedBy { get; private set; }

    /// <summary>RR-014. UTC; SLA start.</summary>
    public DateTime? SubmittedAt { get; private set; }

    /// <summary>RR-015. Required on Cancel.</summary>
    public string? CancelReason { get; private set; }

    /// <summary>RR-016. Required on Reject.</summary>
    public string? RejectReason { get; private set; }

    /// <summary>RR-017. Optimistic concurrency token.</summary>
    public byte[] RowVersion { get; private set; } = [];

    /// <summary>RR-018. Required at Submit. Contact master is not Sprint 1 scope.</summary>
    public Guid? RequestContactId { get; private set; }

    /// <summary>RR-019. UTC; required at Submit.</summary>
    public DateTime? PreferredStartAt { get; private set; }

    /// <summary>RR-020. UTC; required at Submit; must be &gt;= PreferredStartAt.</summary>
    public DateTime? PreferredEndAt { get; private set; }

    /// <summary>
    /// ST-RR-001 Create/Edit (DRAFT -> DRAFT). Replaces the S1-005 editable Draft fields; every field is optional
    /// while DRAFT (RR-DD-001 "Y@Submit"). Aggregate-local rules are enforced here: only a DRAFT can be edited,
    /// Equipment requires a Site, timestamps are UTC and the preferred window is ordered. Scope, active-master and
    /// Equipment-belongs-to-Site checks need the database and are enforced by the Application layer.
    /// Category, priority, location and contact are not editable until their masters exist (S1-005 decision E1).
    /// </summary>
    public void EditDraft(Guid? siteId, Guid? equipmentId, string? description, DateTime? preferredStartAt, DateTime? preferredEndAt)
    {
        if (Status != RepairRequestStatus.Draft)
        {
            throw new DomainRuleViolationException("Only a DRAFT Repair Request can be edited.");
        }

        if (siteId is { } site)
        {
            DomainGuard.NotEmpty(site, nameof(siteId));
        }

        if (equipmentId is { } equipment)
        {
            DomainGuard.NotEmpty(equipment, nameof(equipmentId));

            if (siteId is null)
            {
                throw new ArgumentException("Equipment requires a selected Site.", nameof(equipmentId));
            }
        }

        if (preferredStartAt is { } start)
        {
            DomainGuard.Utc(start, nameof(preferredStartAt));
        }

        if (preferredEndAt is { } end)
        {
            DomainGuard.Utc(end, nameof(preferredEndAt));

            if (preferredStartAt is { } windowStart && end < windowStart)
            {
                throw new ArgumentException("The preferred end must not be earlier than the preferred start.", nameof(preferredEndAt));
            }
        }

        SiteId = siteId;
        EquipmentId = equipmentId;
        Description = DomainGuard.OptionalText(description, DescriptionMaxLength, nameof(description));
        PreferredStartAt = preferredStartAt;
        PreferredEndAt = preferredEndAt;
    }
}
