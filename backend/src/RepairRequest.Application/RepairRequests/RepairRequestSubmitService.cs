using RepairRequest.Application.Common;
using RepairRequest.Domain.RepairRequests;
using RepairRequestAggregate = RepairRequest.Domain.RepairRequests.RepairRequest;

namespace RepairRequest.Application.RepairRequests;

/// <summary>
/// UC-RR-002 Submit Request and Handle Duplicate Warning (ST-RR-002 DRAFT -> SUBMITTED; FR-02; BR-01/03/14/16; D-12;
/// DEC-PRE-S1-007-01..12). Role capability (REQUESTER) is enforced by the API policy. Validation order is deterministic:
/// owned request in scope (404) -> row version (409) -> DRAFT state (409) -> selected Site in business scope (404) ->
/// all field/master/lookup/contact/CLEAN-photo prerequisites in one 422 -> one atomic store call, serialized per BR-14
/// duplicate key: duplicate warning (422 with count only) or Request No + transition + audit (409 on concurrency or lock
/// timeout). The duplicate query never runs for an invalid request, and concurrent Submits of different matching Drafts
/// cannot both pass it without a continuation reason.
/// Successful Submit records <c>submitted_at</c> as the SLA start marker only; no routing, notification or SLA
/// calculation happens here.
/// </summary>
public sealed class RepairRequestSubmitService
{
    /// <summary>BR-14 duplicate window.</summary>
    public static readonly TimeSpan DuplicateWindow = TimeSpan.FromHours(24);

    private readonly IRepairRequestDraftStore _drafts;
    private readonly IRepairRequestSubmitStore _submits;
    private readonly TimeProvider _clock;

    public RepairRequestSubmitService(IRepairRequestDraftStore drafts, IRepairRequestSubmitStore submits, TimeProvider clock)
    {
        _drafts = drafts;
        _submits = submits;
        _clock = clock;
    }

    public async Task<CommandResult<RepairRequestDraftDto>> SubmitAsync(
        CommandContext context,
        Guid repairRequestId,
        byte[] expectedRowVersion,
        string? duplicateContinuationReason,
        CancellationToken cancellationToken)
    {
        // Ownership and business scope are part of the lookup: another user's or an out-of-scope request is a 404.
        var request = await _drafts.FindOwnAsync(context.User, repairRequestId, cancellationToken);
        if (request is null)
        {
            return CommandError.NotFound;
        }

        if (!request.RowVersion.AsSpan().SequenceEqual(expectedRowVersion))
        {
            return CommandError.ConcurrencyConflict;
        }

        if (request.Status != RepairRequestStatus.Draft)
        {
            return CommandError.StateConflict("Only a DRAFT Repair Request can be submitted.");
        }

        var errors = new Dictionary<string, string[]>();
        var reason = ContinuationReason(duplicateContinuationReason, errors);
        RequirePresent(request, errors);

        var query = new DraftSelectionQuery(
            request.SiteId, request.EquipmentId, request.RequestCategoryCode, request.PriorityCode, request.RequestContactId);
        if (!query.IsEmpty)
        {
            var selection = await _drafts.GetDraftSelectionAsync(context.User, query, cancellationToken);

            if (query.SiteId is not null && selection.SiteStatus is null)
            {
                return CommandError.NotFound;
            }

            RepairRequestSelectionRules.Check(query, selection, errors);
        }

        if (!await _submits.HasCleanPhotoAsync(request.TenantId, request.Id, cancellationToken))
        {
            errors[RepairRequestFields.Attachments] = ["At least one CLEAN JPG, JPEG or PNG photo is required to submit."];
        }

        if (errors.Count > 0)
        {
            return CommandError.Validation(errors);
        }

        var now = UtcNow();
        var duplicateQuery = new DuplicateQuery(
            request.TenantId, request.Id, request.SiteId!.Value, request.RequestCategoryCode!, request.EquipmentId, now - DuplicateWindow);

        // The duplicate count, the reason decision, Request No allocation, transition and audit run atomically in the store,
        // serialized per duplicate key: a concurrent Submit of a different matching Draft is always observed.
        var outcome = await _submits.SubmitAsync(
            request,
            expectedRowVersion,
            duplicateQuery,
            now.Year,
            hasContinuationReason: reason is not null,
            (sequence, duplicateCount) =>
            {
                // A reason sent when there is no duplicate has nothing to continue from: it is ignored, not stored and not audited.
                var appliedReason = duplicateCount > 0 ? reason : null;
                request.Submit(RequestNumber.Format(now.Year, sequence), context.User.UserId, now, appliedReason);
                return RepairRequestAudit.Submitted(context, request, duplicateCount, now);
            },
            cancellationToken);

        return outcome.Status switch
        {
            RepairRequestSubmitStatus.Saved => CommandResult<RepairRequestDraftDto>.Success(RepairRequestDraftDto.From(request)),
            RepairRequestSubmitStatus.DuplicateWarning => CommandError.DuplicateWarning(
                RepairRequestFields.DuplicateContinuationReason,
                "An active Repair Request for the same Site, Category and Equipment was submitted within the last 24 hours. A continuation reason is required to submit.",
                outcome.DuplicateCount),
            _ => CommandError.ConcurrencyConflict
        };
    }

    private static string? ContinuationReason(string? value, IDictionary<string, string[]> errors)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        if (trimmed.Length > RepairRequestAggregate.ReasonMaxLength)
        {
            errors[RepairRequestFields.DuplicateContinuationReason] = [$"The continuation reason must not exceed {RepairRequestAggregate.ReasonMaxLength} characters."];
            return null;
        }

        return trimmed;
    }

    private static void RequirePresent(RepairRequestAggregate request, IDictionary<string, string[]> errors)
    {
        if (request.SiteId is null)
        {
            errors[RepairRequestFields.SiteId] = ["A Site is required to submit."];
        }

        if (request.RequestCategoryCode is null)
        {
            errors[RepairRequestFields.RequestCategoryCode] = ["A Category is required to submit."];
        }

        if (request.PriorityCode is null)
        {
            errors[RepairRequestFields.PriorityCode] = ["A Priority is required to submit."];
        }

        if (request.RequestContactId is null)
        {
            errors[RepairRequestFields.RequestContactId] = ["A request contact is required to submit."];
        }

        if (string.IsNullOrWhiteSpace(request.Description))
        {
            errors[RepairRequestFields.Description] = ["A problem description is required to submit."];
        }

        if (request.PreferredStartAt is null)
        {
            errors[RepairRequestFields.PreferredStartAt] = ["A preferred start is required to submit."];
        }

        if (request.PreferredEndAt is null)
        {
            errors[RepairRequestFields.PreferredEndAt] = ["A preferred end is required to submit."];
        }
    }

    /// <summary>UTC truncated to the datetime2(3) precision used by persisted timestamps.</summary>
    private DateTime UtcNow()
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        return now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMillisecond));
    }
}
