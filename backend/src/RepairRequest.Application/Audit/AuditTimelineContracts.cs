namespace RepairRequest.Application.Audit;

/// <summary>
/// The server-derived set of audit subjects that make up one Work Order's timeline (docs/15 D2): the Work Order, its
/// Service Visits, the Work Sessions under those Visits, its Corrective Actions and the originating Repair Request
/// (whose only contribution is the REPAIR_REQUEST_CONVERTED event). Child ids are never taken from the client.
/// </summary>
public sealed record AuditTimelineSources(
    Guid TenantId,
    Guid WorkOrderId,
    Guid RepairRequestId,
    IReadOnlyList<Guid> VisitIds,
    IReadOnlyList<Guid> SessionIds,
    IReadOnlyList<Guid> CorrectiveActionIds);

/// <summary>The keyset position: the (occurred_at, audit_history_id) of the last delivered row (docs/15 §5).</summary>
public readonly record struct TimelinePosition(DateTime OccurredAt, Guid AuditId);

/// <summary>
/// One audit row as read from the store: only the columns the timeline may expose. <see cref="NewValueJson"/> is
/// read only for action codes that have a details allowlist entry and is never returned as text (docs/15 §4.1).
/// </summary>
public sealed record AuditTimelineRow(
    Guid AuditId,
    DateTime OccurredAt,
    string EntityType,
    Guid EntityId,
    string ActionCode,
    string? FromState,
    string? ToState,
    Guid ActorId,
    string? NewValueJson);

/// <summary>D5: USER (an existing user of the same tenant), SYSTEM (a system actor constant) or UNKNOWN.</summary>
public enum AuditActorKind
{
    User,
    System,
    Unknown,
}

public sealed record AuditTimelineActorDto(Guid ActorId, AuditActorKind Kind);

/// <summary>
/// One timeline item (docs/15 §4). There is deliberately no reason, correlation id, raw JSON, name, e-mail or
/// payload text: <see cref="Details"/> carries only allowlisted scalar keys.
/// </summary>
public sealed record AuditTimelineItemDto(
    Guid AuditId,
    DateTime OccurredAt,
    string ActionCode,
    string EntityType,
    Guid EntityId,
    string? FromState,
    string? ToState,
    AuditTimelineActorDto Actor,
    IReadOnlyDictionary<string, object?> Details);

public sealed record AuditTimelinePageDto(IReadOnlyList<AuditTimelineItemDto> Items, string? NextCursor, bool HasMore);

public enum AuditTimelineOutcome
{
    Ok,

    /// <summary>The cursor is malformed, tampered, or not authentic for this tenant, entity type and Work Order (generic 400).</summary>
    BadCursor,

    /// <summary>Unsupported entity type, missing Work Order, or outside the caller's scope (one identical 404).</summary>
    NotFound,
}

public sealed record AuditTimelineResult(AuditTimelineOutcome Outcome, AuditTimelinePageDto? Page)
{
    public static AuditTimelineResult BadCursor { get; } = new(AuditTimelineOutcome.BadCursor, null);

    public static AuditTimelineResult NotFound { get; } = new(AuditTimelineOutcome.NotFound, null);

    public static AuditTimelineResult Ok(AuditTimelinePageDto page) => new(AuditTimelineOutcome.Ok, page);
}
