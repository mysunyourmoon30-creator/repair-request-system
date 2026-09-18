using RepairRequest.Application.Common;
using RepairRequest.Domain.WorkOrders;

namespace RepairRequest.Application.WorkOrders;

/// <summary>
/// ST-WO-001 Schedule (UC-WO-003; S2-003) by a Coordinator. One Schedule runs in one transaction, and the checks
/// run in this deterministic order: the Work Order is within the Coordinator's site scope (404 otherwise); row
/// version (409 CONCURRENCY_CONFLICT); source state OPEN (409 STATE_CONFLICT); team, technician and the schedule
/// window all present and the window valid (422); the technician is an active TECHNICIAN Site-scoped to the Work
/// Order (422). Success sets the Work Order's owner team, moves it to SCHEDULED, and creates its first Service
/// Visit (SCHEDULED). "Team" is not validated further — no Team master-data entity exists (see
/// <see cref="ServiceVisit"/>'s own doc comment).
/// </summary>
public sealed class WorkOrderScheduleService
{
    private readonly IWorkOrderScheduleStore _store;
    private readonly TimeProvider _clock;

    public WorkOrderScheduleService(IWorkOrderScheduleStore store, TimeProvider clock)
    {
        _store = store;
        _clock = clock;
    }

    public Task<CommandResult<Guid>> ScheduleAsync(
        CommandContext context,
        Guid workOrderId,
        byte[] expectedRowVersion,
        Guid? ownerTeamId,
        Guid? assignedTechnicianId,
        DateTimeOffset? scheduledStartAt,
        DateTimeOffset? scheduledEndAt,
        CancellationToken cancellationToken) =>
        _store.RunInTransactionAsync(
            () => ScheduleLockedAsync(context, workOrderId, expectedRowVersion, ownerTeamId, assignedTechnicianId, scheduledStartAt, scheduledEndAt, cancellationToken),
            cancellationToken);

    private async Task<CommandResult<Guid>> ScheduleLockedAsync(
        CommandContext context,
        Guid workOrderId,
        byte[] expectedRowVersion,
        Guid? ownerTeamId,
        Guid? assignedTechnicianId,
        DateTimeOffset? scheduledStartAt,
        DateTimeOffset? scheduledEndAt,
        CancellationToken cancellationToken)
    {
        var loaded = await _store.LoadForScheduleAsync(context.User, workOrderId, cancellationToken);
        if (loaded is null)
        {
            return CommandError.NotFound;
        }

        var (workOrder, siteId) = loaded;

        if (!workOrder.RowVersion.AsSpan().SequenceEqual(expectedRowVersion))
        {
            return CommandError.ConcurrencyConflict;
        }

        if (!WorkOrderStatusTransitions.IsAllowed(workOrder.Status, WorkOrderStatus.Scheduled))
        {
            return CommandError.StateConflict("Only an OPEN Work Order can be scheduled.");
        }

        if (ownerTeamId is not { } team || team == Guid.Empty)
        {
            return CommandError.Validation(WorkOrderFields.OwnerTeamId, "A team identifier is required to schedule.");
        }

        var start = ToUtc(scheduledStartAt);
        var end = ToUtc(scheduledEndAt);
        if (start is null)
        {
            return CommandError.Validation(WorkOrderFields.ScheduledStartAt, "A scheduled start is required to schedule.");
        }

        if (end is null)
        {
            return CommandError.Validation(WorkOrderFields.ScheduledEndAt, "A scheduled end is required to schedule.");
        }

        if (end < start)
        {
            return CommandError.Validation(WorkOrderFields.ScheduledEndAt, "The scheduled end must not be earlier than the scheduled start.");
        }

        if (assignedTechnicianId is not { } technician
            || siteId is null
            || !await _store.IsTechnicianEligibleAsync(workOrder.TenantId, technician, siteId.Value, cancellationToken))
        {
            return CommandError.Validation(
                WorkOrderFields.AssignedTechnicianId, "The technician must be an active Technician assigned to this Work Order's Site.");
        }

        var now = UtcNow();
        var visit = ServiceVisit.Create(workOrder.TenantId, workOrder.Id, ServiceVisitType.Initial, team, technician, start.Value, end.Value);

        workOrder.Schedule(team);
        _store.Add(visit);
        _store.AddAudit(WorkOrderAudit.Scheduled(context, workOrder, visit, now));

        var outcome = await _store.SaveChangesAsync(workOrder, expectedRowVersion, cancellationToken);

        return outcome == WorkOrderSaveOutcome.Saved
            ? CommandResult<Guid>.Success(workOrder.Id)
            : CommandError.ConcurrencyConflict;
    }

    /// <summary>UTC truncated to the datetime2(3) precision used by persisted timestamps.</summary>
    internal static DateTime? ToUtc(DateTimeOffset? value)
    {
        if (value is null)
        {
            return null;
        }

        var utc = value.Value.UtcDateTime;
        return utc.AddTicks(-(utc.Ticks % TimeSpan.TicksPerMillisecond));
    }

    private DateTime UtcNow() => ToUtc(_clock.GetUtcNow())!.Value;
}
