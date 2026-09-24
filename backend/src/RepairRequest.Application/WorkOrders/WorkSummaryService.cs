using RepairRequest.Application.Common;
using RepairRequest.Application.Security;
using RepairRequest.Domain.WorkOrders;

namespace RepairRequest.Application.WorkOrders;

/// <summary>
/// Submit Work Summary (ST-WO-003; UC-WO-020; Technician) and Submit for Acceptance (ST-WO-004; Team Lead or
/// Supervisor), per `docs/13` §4.15. Each runs in one transaction, checks in the same deterministic order as
/// every other command in this codebase: scope (404), row version against the Work Order's own RowVersion (409
/// CONCURRENCY_CONFLICT — the Work Order is the resource named in both endpoints' URLs, so it carries the
/// client's If-Match, the same convention Check-in gives the Service Visit and Check-out gives the Work
/// Session), the right source state (409 STATE_CONFLICT), then field validation (422, Submit only). Success sets
/// the Work Order's status, and for Submit, creates the new Work Summary row — all with server-derived data, one
/// save, one audit row.
/// </summary>
public sealed class WorkSummaryService
{
    private readonly IWorkSummaryStore _store;
    private readonly TimeProvider _clock;

    public WorkSummaryService(IWorkSummaryStore store, TimeProvider clock)
    {
        _store = store;
        _clock = clock;
    }

    public Task<WorkSummaryDto?> GetAsync(CurrentUser user, Guid workOrderId, CancellationToken cancellationToken) =>
        _store.GetWorkSummaryAsync(user, workOrderId, cancellationToken);

    public Task<CommandResult<Guid>> SubmitAsync(
        CommandContext context, Guid workOrderId, byte[] expectedRowVersion, string? summaryText, string? repairOutcomeCode, CancellationToken cancellationToken) =>
        _store.RunInTransactionAsync(() => SubmitLockedAsync(context, workOrderId, expectedRowVersion, summaryText, repairOutcomeCode, cancellationToken), cancellationToken);

    private async Task<CommandResult<Guid>> SubmitLockedAsync(
        CommandContext context, Guid workOrderId, byte[] expectedRowVersion, string? summaryText, string? repairOutcomeCode, CancellationToken cancellationToken)
    {
        var loaded = await _store.LoadForSubmitAsync(context.User, workOrderId, cancellationToken);
        if (loaded is null)
        {
            return CommandError.NotFound;
        }

        var (workOrder, visit) = loaded;

        if (!workOrder.RowVersion.AsSpan().SequenceEqual(expectedRowVersion))
        {
            return CommandError.ConcurrencyConflict;
        }

        if (visit is null)
        {
            return CommandError.StateConflict("The Work Session must be checked out before Work Summary can be submitted.");
        }

        if (!WorkOrderStatusTransitions.IsAllowed(workOrder.Status, WorkOrderStatus.AwaitingSupervisorReview))
        {
            return CommandError.StateConflict(
                workOrder.Status == WorkOrderStatus.AwaitingSupervisorReview
                    ? "This Work Order's Work Summary was already submitted."
                    : "Only an IN_PROGRESS Work Order can have its Work Summary submitted.");
        }

        var errors = new Dictionary<string, string[]>();

        var trimmedSummary = summaryText?.Trim() ?? string.Empty;
        if (trimmedSummary.Length == 0 || trimmedSummary.Length > WorkSummary.SummaryTextMaxLength)
        {
            errors[WorkSummaryFields.SummaryText] = [$"A summary of 1 to {WorkSummary.SummaryTextMaxLength} characters is required."];
        }

        // Closed allowlist (`docs/13` §4.15 Decision, Portfolio Project Owner directive): normalized to
        // uppercase before matching, so "repaired", " Repaired ", "REPAIRED" all resolve to the same code; an
        // unrecognized, null, empty or whitespace-only value is rejected — never stored as an arbitrary string.
        var normalizedOutcome = repairOutcomeCode?.Trim().ToUpperInvariant() ?? string.Empty;
        var hasValidOutcome = RepairOutcomeCodes.TryParse(normalizedOutcome, out var outcomeCode);
        if (!hasValidOutcome)
        {
            errors[WorkSummaryFields.RepairOutcomeCode] = [$"repairOutcomeCode must be one of: {string.Join(", ", RepairOutcomeCodes.All)}."];
        }

        if (errors.Count > 0)
        {
            return CommandError.Validation(errors);
        }

        var summary = WorkSummary.Create(workOrder.TenantId, workOrder.Id, visit.Id, trimmedSummary, outcomeCode);
        workOrder.SubmitWorkSummary();

        var now = WorkOrderScheduleService.ToUtc(_clock.GetUtcNow())!.Value;
        _store.Add(summary);
        _store.AddAudit(WorkOrderAudit.WorkSummarySubmitted(context, workOrder, summary, now));

        var outcome = await _store.SaveChangesAsync(workOrder, expectedRowVersion, cancellationToken);
        return outcome == WorkOrderSaveOutcome.Saved ? CommandResult<Guid>.Success(workOrder.Id) : CommandError.ConcurrencyConflict;
    }

    public Task<CommandResult<Guid>> SubmitForAcceptanceAsync(
        CommandContext context, Guid workOrderId, byte[] expectedRowVersion, CancellationToken cancellationToken) =>
        _store.RunInTransactionAsync(() => SubmitForAcceptanceLockedAsync(context, workOrderId, expectedRowVersion, cancellationToken), cancellationToken);

    private async Task<CommandResult<Guid>> SubmitForAcceptanceLockedAsync(
        CommandContext context, Guid workOrderId, byte[] expectedRowVersion, CancellationToken cancellationToken)
    {
        var workOrder = await _store.LoadForAcceptanceSubmitAsync(context.User, workOrderId, cancellationToken);
        if (workOrder is null)
        {
            return CommandError.NotFound;
        }

        if (!workOrder.RowVersion.AsSpan().SequenceEqual(expectedRowVersion))
        {
            return CommandError.ConcurrencyConflict;
        }

        if (!WorkOrderStatusTransitions.IsAllowed(workOrder.Status, WorkOrderStatus.AwaitingCustomerAcceptance))
        {
            return CommandError.StateConflict(
                workOrder.Status == WorkOrderStatus.AwaitingCustomerAcceptance
                    ? "This Work Order was already submitted for acceptance."
                    : "Only a Work Order awaiting supervisor review can be submitted for acceptance.");
        }

        var now = WorkOrderScheduleService.ToUtc(_clock.GetUtcNow())!.Value;
        workOrder.SubmitForAcceptance();

        _store.AddAudit(WorkOrderAudit.SubmittedForAcceptance(context, workOrder, now));

        var outcome = await _store.SaveChangesAsync(workOrder, expectedRowVersion, cancellationToken);
        return outcome == WorkOrderSaveOutcome.Saved ? CommandResult<Guid>.Success(workOrder.Id) : CommandError.ConcurrencyConflict;
    }
}
