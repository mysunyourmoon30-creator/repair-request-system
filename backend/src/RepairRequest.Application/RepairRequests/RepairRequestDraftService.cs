using RepairRequest.Application.Common;
using RepairRequest.Application.Security;
using RepairRequest.Domain.MasterData;
using RepairRequest.Domain.RepairRequests;
using RepairRequestAggregate = RepairRequest.Domain.RepairRequests.RepairRequest;

namespace RepairRequest.Application.RepairRequests;

/// <summary>
/// UC-RR-001 Create / Edit Repair Request Draft (ST-RR-001; FR-01; BR-02/03/16; D-11). Role capability (REQUESTER)
/// is enforced by the API policy before these run. The requester is always the caller and the tenant is always the
/// caller's tenant; only the owner may edit, and only while DRAFT. Submit-only requirements are not applied here.
/// </summary>
public sealed class RepairRequestDraftService
{
    private readonly IRepairRequestDraftStore _store;
    private readonly TimeProvider _clock;

    public RepairRequestDraftService(IRepairRequestDraftStore store, TimeProvider clock)
    {
        _store = store;
        _clock = clock;
    }

    public Task<RepairRequestDraftDto?> GetAsync(CurrentUser user, Guid repairRequestId, CancellationToken cancellationToken) =>
        _store.GetAsync(user, repairRequestId, cancellationToken);

    public async Task<CommandResult<RepairRequestDraftDto>> CreateAsync(
        CommandContext context,
        RepairRequestDraftFields fields,
        CancellationToken cancellationToken)
    {
        var (values, error) = await ValidateAsync(context.User, fields, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var draft = RepairRequestAggregate.CreateDraft(context.User.TenantId, context.User.UserId);
        Apply(draft, values!);

        _store.Add(draft);
        _store.AddAudit(RepairRequestAudit.DraftCreated(context, draft, UtcNow()));

        return await SaveAsync(draft, null, cancellationToken);
    }

    public async Task<CommandResult<RepairRequestDraftDto>> UpdateAsync(
        CommandContext context,
        Guid repairRequestId,
        byte[] expectedRowVersion,
        RepairRequestDraftFields fields,
        CancellationToken cancellationToken)
    {
        // Ownership is part of the lookup: a Draft of another user returns the same 404 as a missing one.
        var draft = await _store.FindOwnAsync(context.User, repairRequestId, cancellationToken);
        if (draft is null)
        {
            return CommandError.NotFound;
        }

        if (!draft.RowVersion.AsSpan().SequenceEqual(expectedRowVersion))
        {
            return CommandError.ConcurrencyConflict;
        }

        if (draft.Status != RepairRequestStatus.Draft)
        {
            return CommandError.StateConflict("Only a DRAFT Repair Request can be edited.");
        }

        // Decision E2: the complete resulting Draft is validated on every save.
        var (values, error) = await ValidateAsync(context.User, fields, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var before = RepairRequestAudit.DraftValues(draft);
        Apply(draft, values!);
        var after = RepairRequestAudit.DraftValues(draft);

        var oldValues = new Dictionary<string, object?>();
        var newValues = new Dictionary<string, object?>();
        for (var index = 0; index < before.Count; index++)
        {
            if (!Equals(before[index].Value, after[index].Value))
            {
                oldValues[before[index].Field] = before[index].Value;
                newValues[after[index].Field] = after[index].Value;
            }
        }

        if (newValues.Count == 0)
        {
            // No change: nothing is written and nothing is audited.
            return CommandResult<RepairRequestDraftDto>.Success(ToDto(draft));
        }

        _store.AddAudit(RepairRequestAudit.DraftUpdated(context, draft, oldValues, newValues, UtcNow()));

        return await SaveAsync(draft, expectedRowVersion, cancellationToken);
    }

    private async Task<(DraftValues? Values, CommandError? Error)> ValidateAsync(
        CurrentUser user,
        RepairRequestDraftFields fields,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();

        var description = fields.Description?.Trim();
        if (string.IsNullOrEmpty(description))
        {
            description = null;
        }
        else if (description.Length > RepairRequestAggregate.DescriptionMaxLength)
        {
            errors[RepairRequestFields.Description] = [$"The description must not exceed {RepairRequestAggregate.DescriptionMaxLength} characters."];
        }

        var start = ToUtc(fields.PreferredStartAt);
        var end = ToUtc(fields.PreferredEndAt);
        if (start is not null && end is not null && end < start)
        {
            errors[RepairRequestFields.PreferredEndAt] = ["The preferred end must not be earlier than the preferred start."];
        }

        if (fields.SiteId == Guid.Empty)
        {
            errors[RepairRequestFields.SiteId] = ["The Site identifier is invalid."];
        }

        if (fields.EquipmentId == Guid.Empty)
        {
            errors[RepairRequestFields.EquipmentId] = ["The Equipment identifier is invalid."];
        }
        else if (fields.EquipmentId is not null && fields.SiteId is null)
        {
            errors[RepairRequestFields.EquipmentId] = ["Equipment requires a selected Site."];
        }

        if (errors.Count > 0)
        {
            return (null, CommandError.Validation(errors));
        }

        if (fields.SiteId is { } siteId)
        {
            var selection = await _store.GetSiteSelectionAsync(user, siteId, fields.EquipmentId, cancellationToken);

            // A Site outside the caller's scope is indistinguishable from a nonexistent one (S1-003 decision 3).
            if (selection is null)
            {
                return (null, CommandError.NotFound);
            }

            if (selection.SiteStatus != MasterDataStatus.Active)
            {
                errors[RepairRequestFields.SiteId] = ["The Site is inactive."];
            }

            if (fields.EquipmentId is not null)
            {
                if (selection.EquipmentStatus is null)
                {
                    errors[RepairRequestFields.EquipmentId] = ["The Equipment does not belong to the selected Site."];
                }
                else if (selection.EquipmentStatus != MasterDataStatus.Active)
                {
                    errors[RepairRequestFields.EquipmentId] = ["The Equipment is inactive."];
                }
            }

            if (errors.Count > 0)
            {
                return (null, CommandError.Validation(errors));
            }
        }

        return (new DraftValues(fields.SiteId, fields.EquipmentId, description, start, end), null);
    }

    private async Task<CommandResult<RepairRequestDraftDto>> SaveAsync(
        RepairRequestAggregate draft,
        byte[]? expectedRowVersion,
        CancellationToken cancellationToken)
    {
        var outcome = await _store.SaveChangesAsync(draft, expectedRowVersion, cancellationToken);

        return outcome == RepairRequestSaveOutcome.Saved
            ? CommandResult<RepairRequestDraftDto>.Success(ToDto(draft))
            : CommandError.ConcurrencyConflict;
    }

    private static void Apply(RepairRequestAggregate draft, DraftValues values) =>
        draft.EditDraft(values.SiteId, values.EquipmentId, values.Description, values.PreferredStartAt, values.PreferredEndAt);

    /// <summary>UTC truncated to the datetime2(3) precision used by persisted timestamps.</summary>
    private static DateTime? ToUtc(DateTimeOffset? value)
    {
        if (value is null)
        {
            return null;
        }

        var utc = value.Value.UtcDateTime;
        return utc.AddTicks(-(utc.Ticks % TimeSpan.TicksPerMillisecond));
    }

    private DateTime UtcNow() => ToUtc(_clock.GetUtcNow())!.Value;

    private static RepairRequestDraftDto ToDto(RepairRequestAggregate draft) =>
        new(
            draft.Id,
            draft.Status,
            draft.RequestNo,
            draft.SiteId,
            draft.EquipmentId,
            draft.Description,
            draft.PreferredStartAt,
            draft.PreferredEndAt,
            draft.CreatedBy,
            draft.RowVersion);

    private sealed record DraftValues(Guid? SiteId, Guid? EquipmentId, string? Description, DateTime? PreferredStartAt, DateTime? PreferredEndAt);
}
