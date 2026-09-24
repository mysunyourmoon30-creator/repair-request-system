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
    public static AuditHistory SubmittedForAcceptance(CommandContext context, WorkOrder workOrder, DateTime occurredAt) =>
        new(
            workOrder.TenantId,
            WorkOrderEntityType,
            workOrder.Id,
            SubmittedForAcceptanceAction,
            fromState: WorkOrderStatusCodes.ToCode(WorkOrderStatus.AwaitingSupervisorReview),
            toState: WorkOrderStatusCodes.ToCode(WorkOrderStatus.AwaitingCustomerAcceptance),
            oldValueJson: null,
            newValueJson: null,
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
