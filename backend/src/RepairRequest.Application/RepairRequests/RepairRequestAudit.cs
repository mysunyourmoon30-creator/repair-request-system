using System.Text.Json;
using RepairRequest.Application.Common;
using RepairRequest.Domain.Approvals;
using RepairRequest.Domain.Auditing;
using RepairRequest.Domain.RepairRequests;
using RepairRequest.Domain.WorkOrders;
using RepairRequestAggregate = RepairRequest.Domain.RepairRequests.RepairRequest;

namespace RepairRequest.Application.RepairRequests;

/// <summary>
/// Audit records for Draft create/edit (UC-RR-001 "Audit create/edit"; ST-RR-001), Submit and Resubmit (UC-RR-002 "Audit
/// submit/reason"; ST-RR-002), decisions (ST-RR-004..006), Cancel (ST-RR-007) and Convert (ST-RR-008; S2-002), written
/// through the append-only audit_history table in the same transaction as the change. Failed commands are not audited.
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
    public const string ReturnedForCorrectionAction = "REPAIR_REQUEST_RETURNED_FOR_CORRECTION";
    public const string ResubmittedAction = "REPAIR_REQUEST_RESUBMITTED";
    public const string ConvertedAction = "REPAIR_REQUEST_CONVERTED";

    public const string ApprovalIdField = "approvalId";
    public const string ApprovalStepNoField = "approvalStepNo";
    public const string ApprovalCycleNoField = "approvalCycleNo";
    public const string WorkOrderIdField = "workOrderId";
    public const string WorkOrderNoField = "workOrderNo";

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
    /// Resubmit success (ST-RR-002 after ST-RR-006): DRAFT -> SUBMITTED. The unchanged Request No and the original submitted_at
    /// (SLA start, DEC-PRE-S1-010-02) are recorded; the resubmission time is <paramref name="occurredAt"/>. The audit reason is
    /// the continuation reason applied to this resubmission only (DEC-PRE-S1-010-04), with the duplicate count, never identifiers.
    /// </summary>
    public static AuditHistory Resubmitted(
        CommandContext context,
        RepairRequestAggregate request,
        int duplicateCount,
        string? appliedContinuationReason,
        DateTime occurredAt)
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
            ResubmittedAction,
            fromState: RepairRequestStatusCodes.ToCode(RepairRequestStatus.Draft),
            toState: RepairRequestStatusCodes.ToCode(RepairRequestStatus.Submitted),
            oldValueJson: null,
            newValueJson: JsonSerializer.Serialize(values),
            reason: appliedContinuationReason,
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
    /// ST-RR-006 success: UNDER_REVIEW -> DRAFT with the required reason (stored on the approval step, APR-008) as the audit
    /// reason. The returned step is referenced by id, step and approval cycle.
    /// </summary>
    public static AuditHistory ReturnedForCorrection(CommandContext context, RepairRequestAggregate request, RepairRequestApproval approval, DateTime occurredAt) =>
        Decision(context, request, approval, ReturnedForCorrectionAction, RepairRequestStatus.Draft, approval.DecisionReason, occurredAt);

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

    /// <summary>
    /// ST-RR-008 success: APPROVED -> CONVERTED. No reason applies (absent from BR-04's list). The new Work Order is
    /// referenced by id and number, embedded in newValueJson — the same one-row-with-embedded-reference pattern
    /// <see cref="Decision"/> uses for the approval it references, rather than a second audit_history row.
    /// </summary>
    public static AuditHistory Converted(CommandContext context, RepairRequestAggregate request, WorkOrder workOrder, DateTime occurredAt) =>
        new(
            request.TenantId,
            EntityType,
            request.Id,
            ConvertedAction,
            fromState: RepairRequestStatusCodes.ToCode(RepairRequestStatus.Approved),
            toState: RepairRequestStatusCodes.ToCode(RepairRequestStatus.Converted),
            oldValueJson: null,
            newValueJson: JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                [WorkOrderIdField] = workOrder.Id,
                [WorkOrderNoField] = workOrder.WorkOrderNo
            }),
            reason: null,
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
                [ApprovalStepNoField] = approval.ApprovalStepNo,
                [ApprovalCycleNoField] = approval.ApprovalCycleNo
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
