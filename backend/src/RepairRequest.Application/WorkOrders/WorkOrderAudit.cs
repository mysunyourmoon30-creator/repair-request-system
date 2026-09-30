using System.Text.Json;
using RepairRequest.Application.Common;
using RepairRequest.Domain.Auditing;
using RepairRequest.Domain.WorkOrders;

namespace RepairRequest.Application.WorkOrders;

/// <summary>
/// Audit records for Schedule (ST-WO-001; S2-003) and the Service Visit actions (ST-SV-004..009), written through
/// the append-only audit_history table in the same transaction as the change. Failed commands are not audited.
/// Schedule's EntityType is the Work Order (the entity whose status actually changes); every Visit action's
/// EntityType is the Service Visit itself, mirroring how Convert's EntityType is the Repair Request that changes,
/// with the created Work Order only referenced in newValueJson.
/// </summary>
public static class WorkOrderAudit
{
    public const string WorkOrderEntityType = "WORK_ORDER";
    public const string ServiceVisitEntityType = "SERVICE_VISIT";

    public const string ScheduledAction = "WORK_ORDER_SCHEDULED";
    public const string WorkStartedAction = "WORK_ORDER_STARTED";

    /// <summary>ST-WO-010 (`docs/13` §4.23). Self-named, no literal baseline action-code string exists — kept distinct from <see cref="WorkStartedAction"/> per the established one-code-per-transition-ID convention (confirmed by Portfolio Project Owner directive, not the default).</summary>
    public const string ReworkStartedAction = "WORK_ORDER_REWORK_STARTED";
    public const string RescheduledAction = "SERVICE_VISIT_RESCHEDULED";
    public const string ReassignedAction = "SERVICE_VISIT_REASSIGNED";
    public const string CancelledAction = "SERVICE_VISIT_CANCELLED";
    public const string MarkedMissedAction = "SERVICE_VISIT_MARKED_MISSED";
    public const string MissedDecidedAction = "SERVICE_VISIT_MISSED_DECIDED";
    public const string CheckedInAction = "SERVICE_VISIT_CHECKED_IN";

    public const string ServiceVisitIdField = "serviceVisitId";
    public const string NewServiceVisitIdField = "newServiceVisitId";
    public const string WorkSessionIdField = "workSessionId";

    public const string WorkSessionEntityType = "WORK_SESSION";
    public const string PausedAction = "WORK_SESSION_PAUSED";
    public const string ResumedAction = "WORK_SESSION_RESUMED";
    public const string CheckedOutAction = "WORK_SESSION_CHECKED_OUT";
    public const string VisitCompletedAction = "SERVICE_VISIT_COMPLETED";
    public const string WorkSessionPauseIdField = "workSessionPauseId";
    public const string PausedAtField = "pausedAt";
    public const string ResumedAtField = "resumedAt";

    public const string WorkSummarySubmittedAction = "WORK_SUMMARY_SUBMITTED";
    public const string SubmittedForAcceptanceAction = "WORK_ORDER_SUBMITTED_FOR_ACCEPTANCE";
    public const string WorkSummaryIdField = "workSummaryId";

    public const string AcceptedAction = "WORK_ORDER_ACCEPTED";
    public const string AcceptanceContactIdField = "acceptanceContactId";
    public const string AcceptanceContactSnapshotField = "acceptanceContactSnapshot";

    public const string RejectedAction = "WORK_ORDER_REJECTED";
    public const string CustomerAcceptanceIdField = "customerAcceptanceId";
    public const string CorrectiveActionIdField = "correctiveActionId";
    public const string WorkOrderIdField = "workOrderId";

    /// <summary>
    /// ST-WO-006 (`docs/13` §4.20). The baseline names only "Close audit" (TC-WO-011); this follows the same
    /// self-named `&lt;ENTITY&gt;_&lt;PAST_TENSE_VERB&gt;` convention as every other action code in this class.
    /// </summary>
    public const string ClosedAction = "WORK_ORDER_CLOSED";

    /// <summary>
    /// Corrective Action's own entity type (`docs/13` §4.21; Ticket 6) — the first action codes in this class
    /// whose entity is not the Work Order/Service Visit/Work Session, since the Corrective Action itself is the
    /// row that changed; the Work Order id (whose own status also changes, ST-WO-008/009) is referenced in
    /// <c>new_value_json</c> instead.
    /// </summary>
    public const string CorrectiveActionEntityType = "CORRECTIVE_ACTION";

    /// <summary>ST-CA-002 (`docs/13` §4.21). Self-named, no literal baseline action-code string exists.</summary>
    public const string CorrectiveActionPlanSubmittedAction = "CORRECTIVE_ACTION_PLAN_SUBMITTED";

    /// <summary>ST-CA-003 (`docs/13` §4.21). Self-named, no literal baseline action-code string exists.</summary>
    public const string CorrectiveActionPlanApprovedAction = "CORRECTIVE_ACTION_PLAN_APPROVED";

    /// <summary>CA-API-003 (`docs/13` §4.22). Self-named, same established convention — no literal baseline action-code string exists.</summary>
    public const string CorrectiveActionReworkScheduledAction = "CORRECTIVE_ACTION_REWORK_SCHEDULED";

    /// <summary>
    /// No literal baseline action-code string exists for Cost Summary Prepare anywhere in `docs/02`, `docs/06` or
    /// `docs/09` (BR-08's own "System Result/Audit" text names only Close's audit; TC-CST-001's "Expected Audit"
    /// names only "Cost review audit", which describes Review, not Prepare). These two names follow the same
    /// self-consistent `&lt;ENTITY&gt;_&lt;PAST_TENSE_VERB&gt;` convention every other action code in this class
    /// already uses (none of which is baseline-literal text either) — not a new guess, the same established
    /// technical convention. Per Portfolio Project Owner directive (`docs/13` §4.18), first-time create and a
    /// later edit are distinct, separately named events — never one shared name that would make the two
    /// indistinguishable in the audit trail.
    /// </summary>
    public const string CostSummaryPreparedAction = "COST_SUMMARY_PREPARED";
    public const string CostSummaryUpdatedAction = "COST_SUMMARY_UPDATED";

    /// <summary>CST-API-002 (`docs/13` §4.19). Same established self-named convention as Prepare/Update — flagged for the same reason.</summary>
    public const string CostSummaryReviewedAction = "COST_SUMMARY_REVIEWED";
    public const string CostSummaryIdField = "costSummaryId";
    public const string TotalAmountField = "totalAmount";
    public const string CurrencyCodeField = "currencyCode";

    /// <summary>ST-WO-001 success: OPEN -&gt; SCHEDULED, with the new first Service Visit referenced. No reason applies.</summary>
    public static AuditHistory Scheduled(CommandContext context, WorkOrder workOrder, ServiceVisit visit, DateTime occurredAt) =>
        new(
            workOrder.TenantId,
            WorkOrderEntityType,
            workOrder.Id,
            ScheduledAction,
            fromState: WorkOrderStatusCodes.ToCode(WorkOrderStatus.Open),
            toState: WorkOrderStatusCodes.ToCode(WorkOrderStatus.Scheduled),
            oldValueJson: null,
            newValueJson: JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                [ServiceVisitIdField] = visit.Id,
                [WorkOrderFields.OwnerTeamId] = workOrder.OwnerTeamId,
                [WorkOrderFields.AssignedTechnicianId] = visit.AssignedTechnicianId,
                [WorkOrderFields.ScheduledStartAt] = visit.ScheduledStartAt,
                [WorkOrderFields.ScheduledEndAt] = visit.ScheduledEndAt
            }),
            reason: null,
            context.User.UserId,
            occurredAt,
            context.CorrelationId);

    /// <summary>ST-WO-002 success (S3-001): SCHEDULED -&gt; IN_PROGRESS, triggered by the Technician's Check-in. No reason applies.</summary>
    public static AuditHistory WorkStarted(CommandContext context, WorkOrder workOrder, DateTime occurredAt) =>
        new(
            workOrder.TenantId,
            WorkOrderEntityType,
            workOrder.Id,
            WorkStartedAction,
            fromState: WorkOrderStatusCodes.ToCode(WorkOrderStatus.Scheduled),
            toState: WorkOrderStatusCodes.ToCode(WorkOrderStatus.InProgress),
            oldValueJson: null,
            newValueJson: null,
            reason: null,
            context.User.UserId,
            occurredAt,
            context.CorrelationId);

    /// <summary>
    /// ST-WO-010 success (`docs/13` §4.23): CORRECTIVE_PLAN_APPROVED -&gt; IN_PROGRESS ("Start Rework"), triggered
    /// by the assigned Technician's Check-in on the corrective Service Visit — the same physical action as
    /// <see cref="WorkStarted"/>, but a genuinely different transition (a different source state), so it gets its
    /// own audit action code rather than reusing <see cref="WorkStartedAction"/> — the same one-code-per-
    /// transition-ID convention every other entry in this class already follows. No reason applies.
    /// </summary>
    public static AuditHistory ReworkStarted(CommandContext context, WorkOrder workOrder, DateTime occurredAt) =>
        new(
            workOrder.TenantId,
            WorkOrderEntityType,
            workOrder.Id,
            ReworkStartedAction,
            fromState: WorkOrderStatusCodes.ToCode(WorkOrderStatus.CorrectivePlanApproved),
            toState: WorkOrderStatusCodes.ToCode(WorkOrderStatus.InProgress),
            oldValueJson: null,
            newValueJson: null,
            reason: null,
            context.User.UserId,
            occurredAt,
            context.CorrelationId);

    /// <summary>ST-SV-002 success (S3-001): SCHEDULED -&gt; IN_PROGRESS, with the new Work Session referenced. No reason applies.</summary>
    public static AuditHistory CheckedIn(CommandContext context, ServiceVisit visit, WorkSession session, DateTime occurredAt) =>
        new(
            visit.TenantId,
            ServiceVisitEntityType,
            visit.Id,
            CheckedInAction,
            fromState: ServiceVisitStatusCodes.ToCode(ServiceVisitStatus.Scheduled),
            toState: ServiceVisitStatusCodes.ToCode(ServiceVisitStatus.InProgress),
            oldValueJson: null,
            newValueJson: JsonSerializer.Serialize(new Dictionary<string, object?> { [WorkSessionIdField] = session.Id }),
            reason: null,
            context.User.UserId,
            occurredAt,
            context.CorrelationId);

    /// <summary>
    /// ST-WS-002 success (S3-002): CHECKED_IN -&gt; PAUSED on the Work Session itself. The pause reason is recorded
    /// as the audit reason; the new pause period and its server time are referenced in the new value.
    /// </summary>
    public static AuditHistory Paused(CommandContext context, WorkSession session, WorkSessionPause pause, DateTime occurredAt) =>
        new(
            session.TenantId,
            WorkSessionEntityType,
            session.Id,
            PausedAction,
            fromState: WorkSessionStatusCodes.ToCode(WorkSessionStatus.CheckedIn),
            toState: WorkSessionStatusCodes.ToCode(WorkSessionStatus.Paused),
            oldValueJson: null,
            newValueJson: JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                [WorkSessionPauseIdField] = pause.Id,
                [PausedAtField] = pause.PausedAt
            }),
            reason: pause.PauseReason,
            context.User.UserId,
            occurredAt,
            context.CorrelationId);

    /// <summary>
    /// ST-WS-003 success (S3-003): PAUSED -&gt; CHECKED_IN on the Work Session itself. No reason applies (Resume
    /// has none, unlike Pause); the closed pause period and its server time are referenced in the new value.
    /// </summary>
    public static AuditHistory Resumed(CommandContext context, WorkSession session, WorkSessionPause pause, DateTime occurredAt) =>
        new(
            session.TenantId,
            WorkSessionEntityType,
            session.Id,
            ResumedAction,
            fromState: WorkSessionStatusCodes.ToCode(WorkSessionStatus.Paused),
            toState: WorkSessionStatusCodes.ToCode(WorkSessionStatus.CheckedIn),
            oldValueJson: null,
            newValueJson: JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                [WorkSessionPauseIdField] = pause.Id,
                [ResumedAtField] = pause.ResumedAt
            }),
            reason: null,
            context.User.UserId,
            occurredAt,
            context.CorrelationId);

    /// <summary>
    /// ST-WS-004 success (S3-004): CHECKED_IN -&gt; CHECKED_OUT on the Work Session itself. No reason applies —
    /// BR-06's summary/outcome/evidence half is not part of this ticket (see <see cref="WorkSession.CheckOut"/>'s
    /// own doc comment).
    /// </summary>
    public static AuditHistory CheckedOut(CommandContext context, WorkSession session, DateTime occurredAt) =>
        new(
            session.TenantId,
            WorkSessionEntityType,
            session.Id,
            CheckedOutAction,
            fromState: WorkSessionStatusCodes.ToCode(WorkSessionStatus.CheckedIn),
            toState: WorkSessionStatusCodes.ToCode(WorkSessionStatus.CheckedOut),
            oldValueJson: null,
            newValueJson: null,
            reason: null,
            context.User.UserId,
            occurredAt,
            context.CorrelationId);

    /// <summary>
    /// ST-SV-003 success (S3-004): IN_PROGRESS -&gt; COMPLETED on the Service Visit, triggered by the Technician's
    /// Check-out. The Work Session that closed it is referenced in the new value, mirroring how <see cref="CheckedIn"/>
    /// references the session it opened.
    /// </summary>
    public static AuditHistory VisitCompleted(CommandContext context, ServiceVisit visit, WorkSession session, DateTime occurredAt) =>
        new(
            visit.TenantId,
            ServiceVisitEntityType,
            visit.Id,
            VisitCompletedAction,
            fromState: ServiceVisitStatusCodes.ToCode(ServiceVisitStatus.InProgress),
            toState: ServiceVisitStatusCodes.ToCode(ServiceVisitStatus.Completed),
            oldValueJson: null,
            newValueJson: JsonSerializer.Serialize(new Dictionary<string, object?> { [WorkSessionIdField] = session.Id }),
            reason: null,
            context.User.UserId,
            occurredAt,
            context.CorrelationId);

    /// <summary>
    /// ST-WO-003 success (UC-WO-020; `docs/13` §4.15): IN_PROGRESS -&gt; AWAITING_SUPERVISOR_REVIEW on the Work
    /// Order itself, triggered by the Technician's Submit. The new Work Summary is referenced (id and outcome
    /// code only — the summary text is not duplicated into the audit trail). No reason applies.
    /// </summary>
    public static AuditHistory WorkSummarySubmitted(CommandContext context, WorkOrder workOrder, WorkSummary summary, DateTime occurredAt) =>
        new(
            workOrder.TenantId,
            WorkOrderEntityType,
            workOrder.Id,
            WorkSummarySubmittedAction,
            fromState: WorkOrderStatusCodes.ToCode(WorkOrderStatus.InProgress),
            toState: WorkOrderStatusCodes.ToCode(WorkOrderStatus.AwaitingSupervisorReview),
            oldValueJson: null,
            newValueJson: JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                [WorkSummaryIdField] = summary.Id,
                [WorkSummaryFields.RepairOutcomeCode] = RepairOutcomeCodes.ToCode(summary.RepairOutcomeCode)
            }),
            reason: null,
            context.User.UserId,
            occurredAt,
            context.CorrelationId);

    /// <summary>
    /// ST-WO-004 success (`docs/13` §4.15): AWAITING_SUPERVISOR_REVIEW -&gt; AWAITING_CUSTOMER_ACCEPTANCE on the
    /// Work Order, triggered by either the Team Lead or the Supervisor. No reason applies.
    /// </summary>
    /// <summary>
    /// ST-WO-004 success (`docs/13` §4.15/§4.16): AWAITING_SUPERVISOR_REVIEW -&gt; AWAITING_CUSTOMER_ACCEPTANCE,
    /// triggered by either the Team Lead or the Supervisor. The newly designated Acceptance Contact's id and
    /// server-derived snapshot are referenced in the new value. No reason applies.
    /// </summary>
    public static AuditHistory SubmittedForAcceptance(CommandContext context, WorkOrder workOrder, DateTime occurredAt) =>
        new(
            workOrder.TenantId,
            WorkOrderEntityType,
            workOrder.Id,
            SubmittedForAcceptanceAction,
            fromState: WorkOrderStatusCodes.ToCode(WorkOrderStatus.AwaitingSupervisorReview),
            toState: WorkOrderStatusCodes.ToCode(WorkOrderStatus.AwaitingCustomerAcceptance),
            oldValueJson: null,
            newValueJson: JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                [AcceptanceContactIdField] = workOrder.AcceptanceContactId,
                [AcceptanceContactSnapshotField] = workOrder.AcceptanceContactSnapshot
            }),
            reason: null,
            context.User.UserId,
            occurredAt,
            context.CorrelationId);

    /// <summary>
    /// ST-WO-005 success (UC-WO-021; `docs/13` §4.16): AWAITING_CUSTOMER_ACCEPTANCE -&gt; COMPLETED, triggered by
    /// the exact designated Acceptance Contact. No reason applies.
    /// </summary>
    public static AuditHistory Accepted(CommandContext context, WorkOrder workOrder, DateTime occurredAt) =>
        new(
            workOrder.TenantId,
            WorkOrderEntityType,
            workOrder.Id,
            AcceptedAction,
            fromState: WorkOrderStatusCodes.ToCode(WorkOrderStatus.AwaitingCustomerAcceptance),
            toState: WorkOrderStatusCodes.ToCode(WorkOrderStatus.Completed),
            oldValueJson: null,
            newValueJson: null,
            reason: null,
            context.User.UserId,
            occurredAt,
            context.CorrelationId);

    /// <summary>
    /// ST-WO-007 success (UC-WO-022; BR-07/BR-15; `docs/13` §4.17): AWAITING_CUSTOMER_ACCEPTANCE -&gt;
    /// CORRECTIVE_ACTION_REQUIRED, triggered by the exact designated Acceptance Contact. The decision reason is
    /// recorded as the audit reason (mirroring how <see cref="Paused"/> records the pause reason); the new
    /// CustomerAcceptance and CorrectiveAction rows are referenced in the new value.
    /// </summary>
    public static AuditHistory Rejected(CommandContext context, WorkOrder workOrder, CustomerAcceptance acceptance, CorrectiveAction correctiveAction, string decisionReason, DateTime occurredAt) =>
        new(
            workOrder.TenantId,
            WorkOrderEntityType,
            workOrder.Id,
            RejectedAction,
            fromState: WorkOrderStatusCodes.ToCode(WorkOrderStatus.AwaitingCustomerAcceptance),
            toState: WorkOrderStatusCodes.ToCode(WorkOrderStatus.CorrectiveActionRequired),
            oldValueJson: null,
            newValueJson: JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                [CustomerAcceptanceIdField] = acceptance.Id,
                [CorrectiveActionIdField] = correctiveAction.Id
            }),
            reason: decisionReason,
            context.User.UserId,
            occurredAt,
            context.CorrelationId);

    /// <summary>
    /// CST-API-001 first-time success (`docs/13` §4.18): the Work Order's own status does not change (it stays
    /// COMPLETED throughout Prepare — see <see cref="CostSummary"/>'s own class remarks), so fromState/toState
    /// are both COMPLETED; this records the write, not a transition. The new Cost Summary's id and the values
    /// written are referenced in the new value. No reason applies. Distinct from <see cref="CostSummaryUpdated"/>
    /// per Portfolio Project Owner directive — create and edit are never merged under one shared event name.
    /// </summary>
    public static AuditHistory CostSummaryPrepared(CommandContext context, WorkOrder workOrder, CostSummary costSummary, DateTime occurredAt) =>
        new(
            workOrder.TenantId,
            WorkOrderEntityType,
            workOrder.Id,
            CostSummaryPreparedAction,
            fromState: WorkOrderStatusCodes.ToCode(WorkOrderStatus.Completed),
            toState: WorkOrderStatusCodes.ToCode(WorkOrderStatus.Completed),
            oldValueJson: null,
            newValueJson: JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                [CostSummaryIdField] = costSummary.Id,
                [TotalAmountField] = costSummary.TotalAmount,
                [CurrencyCodeField] = costSummary.CurrencyCode
            }),
            reason: null,
            context.User.UserId,
            occurredAt,
            context.CorrelationId);

    /// <summary>
    /// CST-API-001 edit success (`docs/13` §4.18): a later Prepare of an existing, not-yet-reviewed Cost
    /// Summary. Same shape as <see cref="CostSummaryPrepared"/> (the Work Order's status still does not change),
    /// but its own distinct action code — never sharing <see cref="CostSummaryPreparedAction"/> — so create and
    /// edit remain distinguishable in the audit trail.
    /// </summary>
    public static AuditHistory CostSummaryUpdated(CommandContext context, WorkOrder workOrder, CostSummary costSummary, DateTime occurredAt) =>
        new(
            workOrder.TenantId,
            WorkOrderEntityType,
            workOrder.Id,
            CostSummaryUpdatedAction,
            fromState: WorkOrderStatusCodes.ToCode(WorkOrderStatus.Completed),
            toState: WorkOrderStatusCodes.ToCode(WorkOrderStatus.Completed),
            oldValueJson: null,
            newValueJson: JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                [CostSummaryIdField] = costSummary.Id,
                [TotalAmountField] = costSummary.TotalAmount,
                [CurrencyCodeField] = costSummary.CurrencyCode
            }),
            reason: null,
            context.User.UserId,
            occurredAt,
            context.CorrelationId);

    /// <summary>
    /// CST-API-002 success (`docs/13` §4.19): the Work Order's own status does not change (Review never
    /// transitions it, only Close does, per BR-08/ST-WO-006) — fromState/toState are both COMPLETED, recording
    /// the write. The Cost Summary's id is referenced; the reviewer is <c>context.User.UserId</c> (the same
    /// column every audit row already uses for its actor), so no separate reviewer field is needed in the JSON
    /// payload.
    /// </summary>
    public static AuditHistory CostSummaryReviewed(CommandContext context, WorkOrder workOrder, CostSummary costSummary, DateTime occurredAt) =>
        new(
            workOrder.TenantId,
            WorkOrderEntityType,
            workOrder.Id,
            CostSummaryReviewedAction,
            fromState: WorkOrderStatusCodes.ToCode(WorkOrderStatus.Completed),
            toState: WorkOrderStatusCodes.ToCode(WorkOrderStatus.Completed),
            oldValueJson: null,
            newValueJson: JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                [CostSummaryIdField] = costSummary.Id
            }),
            reason: null,
            context.User.UserId,
            occurredAt,
            context.CorrelationId);

    /// <summary>
    /// ST-WO-006 success (BR-08; `docs/13` §4.20): COMPLETED -&gt; CLOSED by a Supervisor. The reviewed Cost Summary
    /// that satisfied the guard is referenced in the new value; the closing Supervisor is <c>context.User.UserId</c>
    /// (the audit row's own actor column — <c>closed_by</c> is deliberately not exposed elsewhere, Q7). No reason applies.
    /// </summary>
    public static AuditHistory Closed(CommandContext context, WorkOrder workOrder, Guid costSummaryId, DateTime occurredAt) =>
        new(
            workOrder.TenantId,
            WorkOrderEntityType,
            workOrder.Id,
            ClosedAction,
            fromState: WorkOrderStatusCodes.ToCode(WorkOrderStatus.Completed),
            toState: WorkOrderStatusCodes.ToCode(WorkOrderStatus.Closed),
            oldValueJson: null,
            newValueJson: JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                [CostSummaryIdField] = costSummaryId
            }),
            reason: null,
            context.User.UserId,
            occurredAt,
            context.CorrelationId);

    /// <summary>
    /// ST-CA-002 success (`docs/13` §4.21): DRAFT -&gt; PENDING_PLAN_APPROVAL by the submitting Team Lead. The Work
    /// Order id (whose own status advances to CORRECTIVE_PLAN_PENDING, ST-WO-008) is referenced in the new value.
    /// </summary>
    public static AuditHistory CorrectiveActionPlanSubmitted(CommandContext context, CorrectiveAction correctiveAction, WorkOrder workOrder, DateTime occurredAt) =>
        new(
            correctiveAction.TenantId,
            CorrectiveActionEntityType,
            correctiveAction.Id,
            CorrectiveActionPlanSubmittedAction,
            fromState: CorrectiveActionStatusCodes.ToCode(CorrectiveActionStatus.Draft),
            toState: CorrectiveActionStatusCodes.ToCode(CorrectiveActionStatus.PendingPlanApproval),
            oldValueJson: null,
            newValueJson: JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                [WorkOrderIdField] = workOrder.Id
            }),
            reason: null,
            context.User.UserId,
            occurredAt,
            context.CorrelationId);

    /// <summary>
    /// ST-CA-003 success (`docs/13` §4.21): PENDING_PLAN_APPROVAL -&gt; APPROVED by a Supervisor. The Work Order id
    /// (whose own status advances to CORRECTIVE_PLAN_APPROVED, ST-WO-009) is referenced in the new value.
    /// </summary>
    public static AuditHistory CorrectiveActionPlanApproved(CommandContext context, CorrectiveAction correctiveAction, WorkOrder workOrder, DateTime occurredAt) =>
        new(
            correctiveAction.TenantId,
            CorrectiveActionEntityType,
            correctiveAction.Id,
            CorrectiveActionPlanApprovedAction,
            fromState: CorrectiveActionStatusCodes.ToCode(CorrectiveActionStatus.PendingPlanApproval),
            toState: CorrectiveActionStatusCodes.ToCode(CorrectiveActionStatus.Approved),
            oldValueJson: null,
            newValueJson: JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                [WorkOrderIdField] = workOrder.Id
            }),
            reason: null,
            context.User.UserId,
            occurredAt,
            context.CorrelationId);

    /// <summary>
    /// CA-API-003 success (`docs/13` §4.22): the Corrective Action's own Status stays APPROVED throughout (fromState
    /// == toState, same "records the write, not a transition" shape as <see cref="CostSummaryPrepared"/>) — Schedule
    /// Rework deliberately does not advance ST-CA-* or ST-WO-*; the new corrective Service Visit and the Work
    /// Order id are both referenced in the new value. No reason applies.
    /// </summary>
    public static AuditHistory CorrectiveActionReworkScheduled(CommandContext context, CorrectiveAction correctiveAction, WorkOrder workOrder, ServiceVisit visit, DateTime occurredAt) =>
        new(
            correctiveAction.TenantId,
            CorrectiveActionEntityType,
            correctiveAction.Id,
            CorrectiveActionReworkScheduledAction,
            fromState: CorrectiveActionStatusCodes.ToCode(CorrectiveActionStatus.Approved),
            toState: CorrectiveActionStatusCodes.ToCode(CorrectiveActionStatus.Approved),
            oldValueJson: null,
            newValueJson: JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                [WorkOrderIdField] = workOrder.Id,
                [ServiceVisitIdField] = visit.Id,
                [ServiceVisitFields.AssignedTeamId] = visit.AssignedTeamId,
                [ServiceVisitFields.AssignedTechnicianId] = visit.AssignedTechnicianId,
                [ServiceVisitFields.ScheduledStartAt] = visit.ScheduledStartAt,
                [ServiceVisitFields.ScheduledEndAt] = visit.ScheduledEndAt
            }),
            reason: null,
            context.User.UserId,
            occurredAt,
            context.CorrelationId);

    /// <summary>ST-SV-004/005 success: settles back at SCHEDULED (see <see cref="ServiceVisit.Reschedule"/>'s own doc comment).</summary>
    public static AuditHistory Rescheduled(CommandContext context, ServiceVisit visit, DateTime occurredAt) =>
        new(
            visit.TenantId,
            ServiceVisitEntityType,
            visit.Id,
            RescheduledAction,
            fromState: ServiceVisitStatusCodes.ToCode(ServiceVisitStatus.Scheduled),
            toState: ServiceVisitStatusCodes.ToCode(ServiceVisitStatus.Scheduled),
            oldValueJson: null,
            newValueJson: JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                [ServiceVisitFields.ScheduledStartAt] = visit.ScheduledStartAt,
                [ServiceVisitFields.ScheduledEndAt] = visit.ScheduledEndAt
            }),
            reason: visit.RescheduleReason,
            context.User.UserId,
            occurredAt,
            context.CorrelationId);

    /// <summary>ST-SV-006 success: stays SCHEDULED, new team/technician referenced.</summary>
    public static AuditHistory Reassigned(CommandContext context, ServiceVisit visit, DateTime occurredAt) =>
        new(
            visit.TenantId,
            ServiceVisitEntityType,
            visit.Id,
            ReassignedAction,
            fromState: ServiceVisitStatusCodes.ToCode(ServiceVisitStatus.Scheduled),
            toState: ServiceVisitStatusCodes.ToCode(ServiceVisitStatus.Scheduled),
            oldValueJson: null,
            newValueJson: JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                [ServiceVisitFields.AssignedTeamId] = visit.AssignedTeamId,
                [ServiceVisitFields.AssignedTechnicianId] = visit.AssignedTechnicianId
            }),
            reason: visit.ReassignReason,
            context.User.UserId,
            occurredAt,
            context.CorrelationId);

    /// <summary>ST-SV-007 success: SCHEDULED -&gt; CANCELLED. Work Order/SLA continue untouched.</summary>
    public static AuditHistory Cancelled(CommandContext context, ServiceVisit visit, DateTime occurredAt) =>
        new(
            visit.TenantId,
            ServiceVisitEntityType,
            visit.Id,
            CancelledAction,
            fromState: ServiceVisitStatusCodes.ToCode(ServiceVisitStatus.Scheduled),
            toState: ServiceVisitStatusCodes.ToCode(ServiceVisitStatus.Cancelled),
            oldValueJson: null,
            newValueJson: null,
            reason: visit.CancelReason,
            context.User.UserId,
            occurredAt,
            context.CorrelationId);

    /// <summary>ST-SV-008 success: SCHEDULED -&gt; MISSED.</summary>
    public static AuditHistory MarkedMissed(CommandContext context, ServiceVisit visit, DateTime occurredAt) =>
        new(
            visit.TenantId,
            ServiceVisitEntityType,
            visit.Id,
            MarkedMissedAction,
            fromState: ServiceVisitStatusCodes.ToCode(ServiceVisitStatus.Scheduled),
            toState: ServiceVisitStatusCodes.ToCode(ServiceVisitStatus.Missed),
            oldValueJson: null,
            newValueJson: null,
            reason: visit.MissedReason,
            context.User.UserId,
            occurredAt,
            context.CorrelationId);

    /// <summary>
    /// ST-SV-009/D-15 success: the original stays MISSED (never reopened). The decision has no dedicated SV-* reason
    /// column (unlike the other actions), so <paramref name="reason"/> is recorded here only, not on the entity.
    /// <paramref name="newVisit"/> is the follow-up Visit created for RESCHEDULE/FOLLOW_UP/REASSIGN, or null for
    /// NO_FOLLOW_UP.
    /// </summary>
    public static AuditHistory MissedDecided(CommandContext context, ServiceVisit original, string reason, ServiceVisit? newVisit, DateTime occurredAt)
    {
        var values = new Dictionary<string, object?>
        {
            [ServiceVisitFields.Decision] = MissedVisitDecisionCodes.ToCode(original.MissedDecisionCode!.Value)
        };

        if (newVisit is not null)
        {
            values[NewServiceVisitIdField] = newVisit.Id;
        }

        return new(
            original.TenantId,
            ServiceVisitEntityType,
            original.Id,
            MissedDecidedAction,
            fromState: ServiceVisitStatusCodes.ToCode(ServiceVisitStatus.Missed),
            toState: ServiceVisitStatusCodes.ToCode(ServiceVisitStatus.Missed),
            oldValueJson: null,
            newValueJson: JsonSerializer.Serialize(values),
            reason: reason,
            context.User.UserId,
            occurredAt,
            context.CorrelationId);
    }
}
