using RepairRequest.Application.Common;
using RepairRequest.Domain.MasterData;

namespace RepairRequest.Application.MasterData;

/// <summary>
/// Command steps shared by the Customer / Site / Equipment services, following the RR-ARCH-001 section 8
/// template: scoped lookup (404) -> row version (409) -> validation (422) -> state/guard (409) -> change + audit
/// -> one transaction -> fresh row version.
/// </summary>
internal static class MasterDataCommandSteps
{
    public const string InactiveParentMessage = "The {0} is inactive.";

    public static CommandError DuplicateCode(string field) =>
        CommandError.Validation(field, "The code is already used within its scope.");

    public static DateTime UtcNow(TimeProvider clock)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        return now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMillisecond));
    }

    public static bool RowVersionMatches(MasterDataEntity entity, byte[] expectedRowVersion) =>
        entity.RowVersion.AsSpan().SequenceEqual(expectedRowVersion);

    public static async Task<CommandResult<TDto>> UpdateCodeAsync<TEntity, TDto>(
        IMasterDataStore store,
        TimeProvider clock,
        CommandContext context,
        TEntity? entity,
        byte[] expectedRowVersion,
        string? requestedCode,
        string codeField,
        Func<TEntity, string> currentCode,
        Action<TEntity, string> changeCode,
        Func<TEntity, string, Task<bool>> codeTakenByOther,
        Func<TEntity, TDto> toDto,
        CancellationToken cancellationToken)
        where TEntity : MasterDataEntity
    {
        if (entity is null)
        {
            return CommandError.NotFound;
        }

        if (!RowVersionMatches(entity, expectedRowVersion))
        {
            return CommandError.ConcurrencyConflict;
        }

        var errors = new Dictionary<string, string[]>();
        var code = MasterDataValidation.Code(requestedCode, codeField, errors);
        if (code is null)
        {
            return CommandError.Validation(errors);
        }

        var oldCode = currentCode(entity);
        if (string.Equals(oldCode, code, StringComparison.Ordinal))
        {
            // No change: nothing is written and nothing is audited.
            return CommandResult<TDto>.Success(toDto(entity));
        }

        if (await codeTakenByOther(entity, code))
        {
            return DuplicateCode(codeField);
        }

        changeCode(entity, code);
        store.AddAudit(MasterDataAudit.Updated(context, entity, codeField, oldCode, code, UtcNow(clock)));

        return await SaveAsync(store, entity, expectedRowVersion, codeField, toDto, cancellationToken);
    }

    public static async Task<CommandResult<TDto>> ActivateAsync<TEntity, TDto>(
        IMasterDataStore store,
        TimeProvider clock,
        CommandContext context,
        TEntity? entity,
        byte[] expectedRowVersion,
        Func<TEntity, Task<MasterDataStatus?>>? parentStatus,
        string? parentField,
        string? parentName,
        Func<TEntity, TDto> toDto,
        CancellationToken cancellationToken)
        where TEntity : MasterDataEntity
    {
        if (entity is null)
        {
            return CommandError.NotFound;
        }

        if (!RowVersionMatches(entity, expectedRowVersion))
        {
            return CommandError.ConcurrencyConflict;
        }

        if (entity.Status == MasterDataStatus.Active)
        {
            return CommandError.StateConflict($"The {entity.GetType().Name} is already active.");
        }

        // Decision D2: a Site / Equipment cannot become active under an inactive parent.
        if (parentStatus is not null && await parentStatus(entity) != MasterDataStatus.Active)
        {
            return CommandError.Validation(parentField!, string.Format(InactiveParentMessage, parentName));
        }

        var previousReason = entity.DeactivateReason;
        entity.Activate();
        store.AddAudit(MasterDataAudit.Activated(context, entity, previousReason, UtcNow(clock)));

        return await SaveAsync(store, entity, expectedRowVersion, null, toDto, cancellationToken);
    }

    public static async Task<CommandResult<TDto>> DeactivateAsync<TEntity, TDto>(
        IMasterDataStore store,
        TimeProvider clock,
        CommandContext context,
        TEntity? entity,
        byte[] expectedRowVersion,
        string? requestedReason,
        Func<TEntity, Task<int>>? countActiveChildren,
        string? childName,
        Func<TEntity, TDto> toDto,
        CancellationToken cancellationToken)
        where TEntity : MasterDataEntity
    {
        if (entity is null)
        {
            return CommandError.NotFound;
        }

        if (!RowVersionMatches(entity, expectedRowVersion))
        {
            return CommandError.ConcurrencyConflict;
        }

        var errors = new Dictionary<string, string[]>();
        var reason = MasterDataValidation.Reason(requestedReason, errors);
        if (reason is null)
        {
            return CommandError.Validation(errors);
        }

        if (entity.Status == MasterDataStatus.Inactive)
        {
            return CommandError.StateConflict($"The {entity.GetType().Name} is already inactive.");
        }

        // DEC-PS1-013: no deactivation while active children exist; children are never deactivated automatically.
        if (countActiveChildren is not null)
        {
            var activeChildren = await countActiveChildren(entity);
            if (activeChildren > 0)
            {
                return CommandError.StateConflict(
                    $"The {entity.GetType().Name} cannot be deactivated while {activeChildren} active {childName} exist.",
                    activeChildren);
            }
        }

        entity.Deactivate(reason);
        store.AddAudit(MasterDataAudit.Deactivated(context, entity, reason, UtcNow(clock)));

        return await SaveAsync(store, entity, expectedRowVersion, null, toDto, cancellationToken);
    }

    public static async Task<CommandResult<TDto>> SaveAsync<TEntity, TDto>(
        IMasterDataStore store,
        TEntity entity,
        byte[]? expectedRowVersion,
        string? codeField,
        Func<TEntity, TDto> toDto,
        CancellationToken cancellationToken)
        where TEntity : MasterDataEntity
    {
        var outcome = await store.SaveChangesAsync(entity, expectedRowVersion, cancellationToken);

        if (outcome == MasterDataSaveOutcome.Saved)
        {
            return CommandResult<TDto>.Success(toDto(entity));
        }

        if (outcome == MasterDataSaveOutcome.DuplicateCode && codeField is not null)
        {
            return DuplicateCode(codeField);
        }

        return CommandError.ConcurrencyConflict;
    }
}
