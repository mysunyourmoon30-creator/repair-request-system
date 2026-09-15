using System.Text.Json;
using RepairRequest.Application.Common;
using RepairRequest.Domain.Auditing;
using RepairRequest.Domain.RepairRequests;
using RepairRequestAggregate = RepairRequest.Domain.RepairRequests.RepairRequest;

namespace RepairRequest.Application.RepairRequests;

/// <summary>
/// Audit records for Draft create/edit (UC-RR-001 "Audit create/edit"; ST-RR-001) and Submit (UC-RR-002 "Audit
/// submit/reason"; ST-RR-002), written through the append-only audit_history table in the same transaction as the change.
/// Failed commands are not audited.
/// </summary>
public static class RepairRequestAudit
{
    public const string EntityType = "REPAIR_REQUEST";
    public const string DraftCreatedAction = "REPAIR_REQUEST_DRAFT_CREATED";
    public const string DraftUpdatedAction = "REPAIR_REQUEST_DRAFT_UPDATED";
    public const string SubmittedAction = "REPAIR_REQUEST_SUBMITTED";

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

    /// <summary>
    /// ST-RR-002 success: DRAFT -> SUBMITTED with the generated Request No and the SLA start marker. The continuation reason
    /// is the audit reason only when a duplicate warning was overridden; the duplicate count is recorded, never identifiers.
    /// </summary>
    public static AuditHistory Submitted(CommandContext context, RepairRequestAggregate request, int duplicateCount, DateTime occurredAt)
    {
        var values = new Dictionary<string, object?>
        {
            [RepairRequestFields.RequestNo] = request.RequestNo,
            [RepairRequestFields.SubmittedAt] = request.SubmittedAt
        };

        if (duplicateCount > 0)
        {
            values[RepairRequestFields.DuplicateCount] = duplicateCount;
        }

        return new AuditHistory(
            request.TenantId,
            EntityType,
            request.Id,
            SubmittedAction,
            fromState: RepairRequestStatusCodes.ToCode(RepairRequestStatus.Draft),
            toState: RepairRequestStatusCodes.ToCode(RepairRequestStatus.Submitted),
            oldValueJson: null,
            newValueJson: JsonSerializer.Serialize(values),
            reason: request.DuplicateContinuationReason,
            context.User.UserId,
            occurredAt,
            context.CorrelationId);
    }

    /// <summary>The editable Draft fields in API naming, in a stable order.</summary>
    public static IReadOnlyList<(string Field, object? Value)> DraftValues(RepairRequestAggregate draft) =>
    [
        (RepairRequestFields.SiteId, draft.SiteId),
        (RepairRequestFields.EquipmentId, draft.EquipmentId),
        (RepairRequestFields.RequestCategoryCode, draft.RequestCategoryCode),
        (RepairRequestFields.PriorityCode, draft.PriorityCode),
        (RepairRequestFields.RequestContactId, draft.RequestContactId),
        (RepairRequestFields.Description, draft.Description),
        (RepairRequestFields.PreferredStartAt, draft.PreferredStartAt),
        (RepairRequestFields.PreferredEndAt, draft.PreferredEndAt)
    ];
}
