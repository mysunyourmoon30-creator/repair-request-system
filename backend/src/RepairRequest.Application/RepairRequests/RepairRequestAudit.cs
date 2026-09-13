using System.Text.Json;
using RepairRequest.Application.Common;
using RepairRequest.Domain.Auditing;
using RepairRequest.Domain.RepairRequests;
using RepairRequestAggregate = RepairRequest.Domain.RepairRequests.RepairRequest;

namespace RepairRequest.Application.RepairRequests;

/// <summary>
/// Audit records for Draft create/edit (UC-RR-001 "Audit create/edit"; ST-RR-001), written through the existing
/// append-only audit_history table in the same transaction as the change. Values carry only Draft fields.
/// </summary>
public static class RepairRequestAudit
{
    public const string EntityType = "REPAIR_REQUEST";
    public const string DraftCreatedAction = "REPAIR_REQUEST_DRAFT_CREATED";
    public const string DraftUpdatedAction = "REPAIR_REQUEST_DRAFT_UPDATED";

    public static AuditHistory DraftCreated(CommandContext context, RepairRequestAggregate draft, DateTime occurredAt)
    {
        var values = new Dictionary<string, object?>();
        foreach (var (field, value) in DraftValues(draft))
        {
            if (value is not null)
            {
                values[field] = value;
            }
        }

        return new AuditHistory(
            draft.TenantId,
            EntityType,
            draft.Id,
            DraftCreatedAction,
            fromState: null,
            toState: RepairRequestStatusCodes.ToCode(RepairRequestStatus.Draft),
            oldValueJson: null,
            newValueJson: JsonSerializer.Serialize(values),
            reason: null,
            context.User.UserId,
            occurredAt,
            context.CorrelationId);
    }

    public static AuditHistory DraftUpdated(
        CommandContext context,
        RepairRequestAggregate draft,
        IReadOnlyDictionary<string, object?> oldValues,
        IReadOnlyDictionary<string, object?> newValues,
        DateTime occurredAt) =>
        new(
            draft.TenantId,
            EntityType,
            draft.Id,
            DraftUpdatedAction,
            fromState: null,
            toState: null,
            oldValueJson: JsonSerializer.Serialize(oldValues),
            newValueJson: JsonSerializer.Serialize(newValues),
            reason: null,
            context.User.UserId,
            occurredAt,
            context.CorrelationId);

    /// <summary>The S1-005 Draft fields in API naming, in a stable order.</summary>
    public static IReadOnlyList<(string Field, object? Value)> DraftValues(RepairRequestAggregate draft) =>
    [
        (RepairRequestFields.SiteId, draft.SiteId),
        (RepairRequestFields.EquipmentId, draft.EquipmentId),
        (RepairRequestFields.Description, draft.Description),
        (RepairRequestFields.PreferredStartAt, draft.PreferredStartAt),
        (RepairRequestFields.PreferredEndAt, draft.PreferredEndAt)
    ];
}
