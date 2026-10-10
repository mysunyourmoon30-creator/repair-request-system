using RepairRequest.Application.Audit;

namespace RepairRequest.Api.Contracts.Audit;

/// <summary>
/// Query of AUD-API-001 (`docs/15` §4–§5): keyset paging only. <c>cursor</c> is the opaque value of a previous page's
/// <c>nextCursor</c>; there is no <c>page</c>, no filter and no sort parameter in V1.
/// </summary>
public sealed class AuditTimelineRequest
{
    public int? PageSize { get; init; }

    public string? Cursor { get; init; }
}

/// <summary>Who performed the action: id and kind only (D5) — never a name or e-mail.</summary>
public sealed record AuditTimelineActorResponse(Guid ActorId, string Kind);

/// <summary>
/// One timeline item (`docs/15` §4). There is deliberately no reason, correlation id, raw JSON or identity field;
/// <see cref="Details"/> carries only allowlisted scalar values.
/// </summary>
public sealed record AuditTimelineItemResponse(
    Guid AuditId,
    DateTime OccurredAt,
    string ActionCode,
    string EntityType,
    Guid EntityId,
    string? FromState,
    string? ToState,
    AuditTimelineActorResponse Actor,
    IReadOnlyDictionary<string, object?> Details);

/// <summary><c>nextCursor</c> is null exactly when <c>hasMore</c> is false.</summary>
public sealed record AuditTimelineResponse(IReadOnlyList<AuditTimelineItemResponse> Items, string? NextCursor, bool HasMore);

public static class AuditTimelineResponses
{
    public static AuditTimelineResponse ToResponse(AuditTimelinePageDto page) =>
        new(page.Items.Select(ToResponse).ToList(), page.NextCursor, page.HasMore);

    private static AuditTimelineItemResponse ToResponse(AuditTimelineItemDto item) =>
        new(
            item.AuditId,
            item.OccurredAt,
            item.ActionCode,
            item.EntityType,
            item.EntityId,
            item.FromState,
            item.ToState,
            new AuditTimelineActorResponse(item.Actor.ActorId, ActorKind(item.Actor.Kind)),
            item.Details);

    private static string ActorKind(AuditActorKind kind) => kind switch
    {
        AuditActorKind.User => "USER",
        AuditActorKind.System => "SYSTEM",
        _ => "UNKNOWN",
    };
}
