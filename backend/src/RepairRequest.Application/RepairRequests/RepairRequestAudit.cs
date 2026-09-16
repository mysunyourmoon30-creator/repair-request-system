using System.Text.Json;
using RepairRequest.Application.Common;
using RepairRequest.Domain.Approvals;
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
    public const string ApprovedAction = "REPAIR_REQUEST_APPROVED";
    public const string RejectedAction = "REPAIR_REQUEST_REJECTED";
    public const string CancelledAction = "REPAIR_REQUEST_CANCELLED";

    public const string ApprovalIdField = "approvalId";
    public const string ApprovalStepNoField = "approvalStepNo";

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

    /// <summary>
    /// ST-RR-004 success: UNDER_REVIEW -> APPROVED. The actor is the authenticated assigned approver; the decided approval step
    /// is referenced by id and number. No reason is required for Approve.
    /// </summary>
    public static AuditHistory Approved(CommandContext context, RepairRequestAggregate request, RepairRequestApproval approval, DateTime occurredAt) =>
        Decision(context, request, approval, ApprovedAction, RepairRequestStatus.Approved, reason: null, occurredAt);

    /// <summary>ST-RR-005 success: UNDER_REVIEW -> REJECTED with the required reason as the audit reason (AUD-010).</summary>
    public static AuditHistory Rejected(CommandContext context, RepairRequestAggregate request, RepairRequestApproval approval, DateTime occurredAt) =>
        Decision(context, request, approval, RejectedAction, RepairRequestStatus.Rejected, request.RejectReason, occurredAt);

    /// <summary>
    /// ST-RR-007 success: the source state (DRAFT, SUBMITTED, UNDER_REVIEW or APPROVED) -> CANCELLED, with the required cancel
    /// reason as the audit reason. The actor is the owning Requester.
    /// </summary>
    public static AuditHistory Cancelled(CommandContext context, RepairRequestAggregate request, RepairRequestStatus fromState, DateTime occurredAt) =>
        new(
            request.TenantId,
            EntityType,
            request.Id,
            CancelledAction,
            fromState: RepairRequestStatusCodes.ToCode(fromState),
            toState: RepairRequestStatusCodes.ToCode(RepairRequestStatus.Cancelled),
            oldValueJson: null,
            newValueJson: null,
            reason: request.CancelReason,
            context.User.UserId,
            occurredAt,
            context.CorrelationId);

    private static AuditHistory Decision(
        CommandContext context,
        RepairRequestAggregate request,
        RepairRequestApproval approval,
        string actionCode,
        RepairRequestStatus toState,
        string? reason,
        DateTime occurredAt) =>
        new(
            request.TenantId,
            EntityType,
            request.Id,
            actionCode,
            fromState: RepairRequestStatusCodes.ToCode(RepairRequestStatus.UnderReview),
            toState: RepairRequestStatusCodes.ToCode(toState),
            oldValueJson: null,
            newValueJson: JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                [ApprovalIdField] = approval.Id,
                [ApprovalStepNoField] = approval.ApprovalStepNo
            }),
            reason: reason,
            context.User.UserId,
            occurredAt,
            context.CorrelationId);

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
