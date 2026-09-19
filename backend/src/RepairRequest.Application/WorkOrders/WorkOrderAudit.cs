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
