using System.Text.RegularExpressions;
using RepairRequest.Application.Common;
using RepairRequest.Application.Security;
using RepairRequest.Domain.WorkOrders;

namespace RepairRequest.Application.WorkOrders;

/// <summary>
/// Cost Summary Prepare (CST-API-001; Team Lead only; BR-08; `docs/13` §4.18). Runs in one transaction, checks in
/// the same deterministic order as every other command in this codebase: scope (404 — <see
/// cref="IDataScope.WorkOrders"/> site-wide, the same model Work Summary review uses), row version (409
/// CONCURRENCY_CONFLICT — against the Work Order's own RowVersion on first Prepare, or the Cost Summary's own
/// RowVersion on a later edit; see <see cref="ICostSummaryStore"/>'s own remarks), the right source state (409
/// STATE_CONFLICT — Work Order must be COMPLETED; an already-reviewed Cost Summary can never be re-Prepared),
/// then field validation (422). Success creates or updates the single Cost Summary row for this Work Order and
/// writes one audit row, all in the same save — the Work Order's own status is never touched by this ticket.
/// </summary>
public sealed class CostSummaryService
{
    // CST-005 "ISO currency" — format only (exactly 3 upper-case letters); no ISO-4217 master-data list exists
    // anywhere in this codebase, and inventing one would be scope creep beyond CST-API-001.
    private static readonly Regex CurrencyCodePattern = new("^[A-Z]{3}$", RegexOptions.Compiled);

    // decimal(18,2): reject anything that would silently round or overflow rather than let SQL Server do it.
    private const decimal MaxTotalAmount = 9_999_999_999_999_999.99m;

    private readonly ICostSummaryStore _store;
    private readonly TimeProvider _clock;

    public CostSummaryService(ICostSummaryStore store, TimeProvider clock)
    {
        _store = store;
        _clock = clock;
    }

    public Task<CommandResult<CostSummaryDto>> PrepareAsync(
        CommandContext context, Guid workOrderId, byte[] expectedRowVersion, decimal? totalAmount, string? currencyCode, string? note, CancellationToken cancellationToken) =>
        _store.RunInTransactionAsync(() => PrepareLockedAsync(context, workOrderId, expectedRowVersion, totalAmount, currencyCode, note, cancellationToken), cancellationToken);

    private async Task<CommandResult<CostSummaryDto>> PrepareLockedAsync(
        CommandContext context, Guid workOrderId, byte[] expectedRowVersion, decimal? totalAmount, string? currencyCode, string? note, CancellationToken cancellationToken)
    {
        var workOrder = await _store.LoadWorkOrderForPrepareAsync(context.User, workOrderId, cancellationToken);
        if (workOrder is null)
        {
            return CommandError.NotFound;
        }

        var existing = await _store.GetTrackedCostSummaryAsync(workOrderId, cancellationToken);

        // Two-phase concurrency target: the Work Order's own RowVersion never advances during Prepare (its
        // status is untouched), so only the FIRST Prepare (no existing row) can meaningfully check against it;
        // every later edit must check against the Cost Summary's own RowVersion instead (see ICostSummaryStore).
        var expectedTarget = existing?.RowVersion ?? workOrder.RowVersion;
        if (!expectedTarget.AsSpan().SequenceEqual(expectedRowVersion))
        {
            return CommandError.ConcurrencyConflict;
        }

        if (workOrder.Status != WorkOrderStatus.Completed)
        {
            return CommandError.StateConflict("Only a COMPLETED Work Order can have its Cost Summary prepared.");
        }

        if (existing?.ReviewedAt is not null)
        {
            return CommandError.StateConflict("This Work Order's Cost Summary has already been reviewed and can no longer be prepared or edited.");
        }

        var errors = new Dictionary<string, string[]>();

        if (totalAmount is null || totalAmount < 0 || totalAmount > MaxTotalAmount || decimal.Round(totalAmount.Value, 2) != totalAmount.Value)
        {
            errors[CostSummaryFields.TotalAmount] = ["totalAmount is required, must not be negative, and must have at most 2 decimal places."];
        }

        var normalizedCurrency = currencyCode?.Trim().ToUpperInvariant() ?? string.Empty;
        if (!CurrencyCodePattern.IsMatch(normalizedCurrency))
        {
            errors[CostSummaryFields.CurrencyCode] = ["currencyCode must be exactly 3 letters (ISO currency format)."];
        }

        var trimmedNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (trimmedNote is not null && trimmedNote.Length > CostSummary.NoteMaxLength)
        {
            errors[CostSummaryFields.Note] = [$"note must not exceed {CostSummary.NoteMaxLength} characters."];
        }

        if (errors.Count > 0)
        {
            return CommandError.Validation(errors);
        }

        // The client's totalAmount is the only cost input the baseline defines (no line items exist to sum —
        // docs/05/07/08 confirm no itemized cost table); "server-calculated" here means the server is the sole
        // authority over the canonical stored value after validation above, never a raw pass-through of
        // unvalidated client input.
        var now = WorkOrderScheduleService.ToUtc(_clock.GetUtcNow())!.Value;
        CostSummary costSummary;
        WorkOrderSaveOutcome outcome;

        if (existing is null)
        {
            costSummary = CostSummary.Create(workOrder.TenantId, workOrder.Id, totalAmount!.Value, normalizedCurrency, trimmedNote, context.User.UserId, now);
            _store.Add(costSummary);
            _store.AddAudit(WorkOrderAudit.CostSummaryPrepared(context, workOrder, costSummary, now));
            outcome = await _store.SaveNewAsync(workOrder, expectedRowVersion, cancellationToken);
        }
        else
        {
            existing.UpdatePreparation(totalAmount!.Value, normalizedCurrency, trimmedNote, context.User.UserId, now);
            costSummary = existing;
            _store.AddAudit(WorkOrderAudit.CostSummaryUpdated(context, workOrder, costSummary, now));
            outcome = await _store.SaveUpdateAsync(costSummary, expectedRowVersion, cancellationToken);
        }

        if (outcome != WorkOrderSaveOutcome.Saved)
        {
            return CommandError.ConcurrencyConflict;
        }

        return CommandResult<CostSummaryDto>.Success(new CostSummaryDto(
            costSummary.Id, costSummary.WorkOrderId, costSummary.TotalAmount, costSummary.CurrencyCode, costSummary.Note,
            costSummary.PreparedBy, costSummary.PreparedAt, costSummary.ReviewedBy, costSummary.ReviewedAt, costSummary.RowVersion));
    }
}
