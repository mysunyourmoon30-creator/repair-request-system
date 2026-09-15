using RepairRequest.Domain.Common;

namespace RepairRequest.Domain.RepairRequests;

/// <summary>
/// Repair Request aggregate root (RR-DD-001 RR-001..RR-020; RR-DBD-001 repair_request).
/// S1-001 establishes the persisted shape and the initial DRAFT state; S1-005 adds Draft editing (ST-RR-001) and S1-007
/// adds Submit (ST-RR-002). Review and cancel commands (ST-RR-003..007) are added by later tickets; CONVERTED (ST-RR-008)
/// exists for lifecycle compatibility but Work Order creation is Sprint 2 scope.
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

    /// <summary>RR-003. Unique within the tenant; generated at SUBMITTED; immutable (DEC-PRE-S1-007-11).</summary>
    public string? RequestNo { get; private set; }

    /// <summary>RR-004.</summary>
    public RepairRequestStatus Status { get; private set; }

    /// <summary>RR-005. Required at Submit.</summary>
    public Guid? SiteId { get; private set; }

    /// <summary>RR-006. Optional in Sprint 1; must belong to the selected Site.</summary>
    public Guid? EquipmentId { get; private set; }

    /// <summary>RR-007. Location is not in Sprint 1 (DEC-PRE-S1-007-05); never set by any command.</summary>
    public Guid? LocationId { get; private set; }

    /// <summary>RR-008. Tenant-scoped Category lookup code; required at Submit (DEC-PRE-S1-007-01).</summary>
    public string? RequestCategoryCode { get; private set; }

    /// <summary>RR-009. Required at Submit; non-blank.</summary>
    public string? Description { get; private set; }

    /// <summary>RR-010. Tenant-scoped Priority lookup code; required at Submit (DEC-PRE-S1-007-02).</summary>
    public string? PriorityCode { get; private set; }

    /// <summary>RR-011. Required when the BR-14 duplicate warning is overridden.</summary>
    public string? DuplicateContinuationReason { get; private set; }

    /// <summary>RR-012. Requester identity; derived actor; immutable.</summary>
    public Guid CreatedBy { get; private set; }

    /// <summary>RR-013. Derived actor on Submit.</summary>
    public Guid? SubmittedBy { get; private set; }

    /// <summary>RR-014. UTC; the SLA start marker (DEC-PRE-S1-007-12).</summary>
    public DateTime? SubmittedAt { get; private set; }

    /// <summary>RR-015. Required on Cancel.</summary>
    public string? CancelReason { get; private set; }

    /// <summary>RR-016. Required on Reject.</summary>
    public string? RejectReason { get; private set; }

    /// <summary>RR-017. Optimistic concurrency token.</summary>
    public byte[] RowVersion { get; private set; } = [];

    /// <summary>
    /// RR-018. Required at Submit. References a user of the same tenant with business scope on the selected Site
    /// (DEC-PRE-S1-007-04); a reference, not a snapshot. The ACTIVE-user check is deferred (DEC-PRE-S1-007-04, REQ-FU-USR-001).
    /// </summary>
    public Guid? RequestContactId { get; private set; }

    /// <summary>RR-019. UTC; required at Submit.</summary>
    public DateTime? PreferredStartAt { get; private set; }

    /// <summary>RR-020. UTC; required at Submit; must be &gt;= PreferredStartAt.</summary>
    public DateTime? PreferredEndAt { get; private set; }

    /// <summary>
    /// ST-RR-001 Create/Edit (DRAFT -> DRAFT). Replaces the editable Draft fields; every field is optional while DRAFT
    /// (RR-DD-001 "Y@Submit"). Aggregate-local rules are enforced here: only a DRAFT can be edited, Equipment and the request
    /// contact require a Site, codes are non-blank and bounded, timestamps are UTC and the preferred window is ordered.
    /// Scope, active-master, lookup and contact-eligibility checks need the database and are enforced by the Application
    /// layer. Location is not editable in Sprint 1 (DEC-PRE-S1-007-05).
    /// </summary>
    public void EditDraft(
        Guid? siteId,
        Guid? equipmentId,
        string? requestCategoryCode,
        string? priorityCode,
        Guid? requestContactId,
        string? description,
        DateTime? preferredStartAt,
        DateTime? preferredEndAt)
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

        if (requestContactId is { } contact)
        {
            DomainGuard.NotEmpty(contact, nameof(requestContactId));

            if (siteId is null)
            {
                throw new ArgumentException("The request contact requires a selected Site.", nameof(requestContactId));
            }
        }

        var category = OptionalCode(requestCategoryCode, RequestCategoryCodeMaxLength, nameof(requestCategoryCode));
        var priority = OptionalCode(priorityCode, PriorityCodeMaxLength, nameof(priorityCode));
        var text = DomainGuard.OptionalText(description, DescriptionMaxLength, nameof(description));

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
        RequestCategoryCode = category;
        PriorityCode = priority;
        RequestContactId = requestContactId;
        Description = text;
        PreferredStartAt = preferredStartAt;
        PreferredEndAt = preferredEndAt;
    }

    /// <summary>
    /// ST-RR-002 Submit (DRAFT -> SUBMITTED) by the owning Requester. Aggregate-local guards only: current state DRAFT,
    /// Request No not yet assigned (immutable once generated), Submit-required fields present, owner as actor and UTC
    /// time. Master, lookup, contact, CLEAN-photo and duplicate rules need the database and are enforced by the
    /// Application layer before this runs. <see cref="SubmittedAt"/> is the SLA start marker; no SLA due, risk or breach
    /// value is calculated (DEC-PRE-S1-007-12).
    /// </summary>
    public void Submit(string requestNo, Guid submittedBy, DateTime submittedAt, string? duplicateContinuationReason)
    {
        if (Status != RepairRequestStatus.Draft || !RepairRequestStatusTransitions.IsAllowed(Status, RepairRequestStatus.Submitted))
        {
            throw new DomainRuleViolationException("Only a DRAFT Repair Request can be submitted.");
        }

        if (RequestNo is not null)
        {
            throw new DomainRuleViolationException("The Request No is immutable once generated.");
        }

        var number = DomainGuard.RequiredText(requestNo, RequestNoMaxLength, nameof(requestNo));
        DomainGuard.NotEmpty(submittedBy, nameof(submittedBy));
        DomainGuard.Utc(submittedAt, nameof(submittedAt));
        var reason = DomainGuard.OptionalText(duplicateContinuationReason, ReasonMaxLength, nameof(duplicateContinuationReason));

        if (submittedBy != CreatedBy)
        {
            throw new DomainRuleViolationException("Only the Requester who owns the Draft can submit it.");
        }

        if (SiteId is null
            || RequestCategoryCode is null
            || PriorityCode is null
            || RequestContactId is null
            || string.IsNullOrWhiteSpace(Description)
            || PreferredStartAt is null
            || PreferredEndAt is null)
        {
            throw new DomainRuleViolationException("The Draft is missing data required at Submit.");
        }

        RequestNo = number;
        SubmittedBy = submittedBy;
        SubmittedAt = submittedAt;
        DuplicateContinuationReason = reason;
        Status = RepairRequestStatus.Submitted;
    }

    private static string? OptionalCode(string? value, int maxLength, string paramName) =>
        value is null ? null : DomainGuard.RequiredText(value, maxLength, paramName);
}
