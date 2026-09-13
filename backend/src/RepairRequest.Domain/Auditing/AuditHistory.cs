using RepairRequest.Domain.Common;

namespace RepairRequest.Domain.Auditing;

/// <summary>
/// Append-only audit trail (RR-DD-001 AUD-001, AUD-003..AUD-013; RR-DBD-001 audit_history).
/// Polymorphic subject reference (entity_type + entity_id) without a foreign key to
/// every business table (RR-ERD-001 section 6). tenant_id is carried because the
/// approved audit-timeline access path is tenant_id + entity_type + entity_id +
/// occurred_at (RR-DBD-001 section 5). Instances expose no mutators.
/// </summary>
public sealed class AuditHistory
{
    public const int EntityTypeMaxLength = 30;
    public const int ActionCodeMaxLength = 50;
    public const int StateMaxLength = 50;
    public const int ReasonMaxLength = 1000;

    private AuditHistory()
    {
        EntityType = null!;
        ActionCode = null!;
    }

    public AuditHistory(
        Guid tenantId,
        string entityType,
        Guid entityId,
        string actionCode,
        string? fromState,
        string? toState,
        string? oldValueJson,
        string? newValueJson,
        string? reason,
        Guid actorId,
        DateTime occurredAt,
        Guid correlationId)
    {
        TenantId = DomainGuard.NotEmpty(tenantId, nameof(tenantId));
        EntityType = DomainGuard.RequiredText(entityType, EntityTypeMaxLength, nameof(entityType));
        EntityId = DomainGuard.NotEmpty(entityId, nameof(entityId));
        ActionCode = DomainGuard.RequiredText(actionCode, ActionCodeMaxLength, nameof(actionCode));
        FromState = DomainGuard.OptionalText(fromState, StateMaxLength, nameof(fromState));
        ToState = DomainGuard.OptionalText(toState, StateMaxLength, nameof(toState));
        OldValueJson = oldValueJson;
        NewValueJson = newValueJson;
        Reason = DomainGuard.OptionalText(reason, ReasonMaxLength, nameof(reason));
        ActorId = DomainGuard.NotEmpty(actorId, nameof(actorId));
        OccurredAt = DomainGuard.Utc(occurredAt, nameof(occurredAt));
        CorrelationId = DomainGuard.NotEmpty(correlationId, nameof(correlationId));
    }

    /// <summary>AUD-001.</summary>
    public Guid Id { get; private set; }

    /// <summary>Tenant scope for the audit timeline access path.</summary>
    public Guid TenantId { get; private set; }

    /// <summary>AUD-003.</summary>
    public string EntityType { get; private set; }

    /// <summary>AUD-004.</summary>
    public Guid EntityId { get; private set; }

    /// <summary>AUD-005.</summary>
    public string ActionCode { get; private set; }

    /// <summary>AUD-006.</summary>
    public string? FromState { get; private set; }

    /// <summary>AUD-007.</summary>
    public string? ToState { get; private set; }

    /// <summary>AUD-008. JSON document of changed fields.</summary>
    public string? OldValueJson { get; private set; }

    /// <summary>AUD-009. JSON document of changed fields.</summary>
    public string? NewValueJson { get; private set; }

    /// <summary>AUD-010.</summary>
    public string? Reason { get; private set; }

    /// <summary>AUD-011. Derived actor or system identity.</summary>
    public Guid ActorId { get; private set; }

    /// <summary>AUD-012. UTC; immutable.</summary>
    public DateTime OccurredAt { get; private set; }

    /// <summary>AUD-013.</summary>
    public Guid CorrelationId { get; private set; }
}
