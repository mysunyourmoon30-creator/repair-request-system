using RepairRequest.Application.Security;

namespace RepairRequest.Application.Common;

/// <summary>The caller and correlation id of one command (RR-API-001 section 1 audit linkage).</summary>
public sealed record CommandContext(CurrentUser User, Guid CorrelationId);

/// <summary>Controlled failure outcomes of commands (RR-API-001 section 2 error contract).</summary>
public enum CommandFailure
{
    /// <summary>404 NOT_FOUND: nonexistent or outside the caller's tenant/site scope (identical response).</summary>
    NotFound,

    /// <summary>422 VALIDATION_FAILED: field, relationship, duplicate-code or inactive-master validation.</summary>
    ValidationFailed,

    /// <summary>409 STATE_CONFLICT: illegal state change or a dependency guard (e.g. DEC-PS1-013).</summary>
    StateConflict,

    /// <summary>409 CONCURRENCY_CONFLICT: stale row version; nothing was written.</summary>
    ConcurrencyConflict,

    /// <summary>
    /// 403 ACCESS_DENIED: the caller may see the record but an object-level rule forbids the action (e.g. not the assigned
    /// approver, or a decision on the caller's own request). Nothing was written.
    /// </summary>
    AccessDenied
}

public sealed class CommandError
{
    private CommandError(
        CommandFailure failure,
        string? message = null,
        IReadOnlyDictionary<string, string[]>? errors = null,
        int? activeChildCount = null,
        int? duplicateCount = null)
    {
        Failure = failure;
        Message = message;
        Errors = errors ?? new Dictionary<string, string[]>();
        ActiveChildCount = activeChildCount;
        DuplicateCount = duplicateCount;
    }

    public CommandFailure Failure { get; }

    public string? Message { get; }

    public IReadOnlyDictionary<string, string[]> Errors { get; }

    /// <summary>Number of active children that blocked a master-data deactivation (RR-DEC-001 DEC-PS1-013 UI note).</summary>
    public int? ActiveChildCount { get; }

    /// <summary>Number of matching active Repair Requests behind a BR-14 duplicate warning (DEC-PRE-S1-007-10); count only.</summary>
    public int? DuplicateCount { get; }

    public static CommandError NotFound { get; } = new(CommandFailure.NotFound);

    public static CommandError AccessDenied { get; } = new(CommandFailure.AccessDenied);

    public static CommandError ConcurrencyConflict { get; } =
        new(CommandFailure.ConcurrencyConflict, "The record was changed by another request. Reload it before retrying.");

    public static CommandError Validation(IReadOnlyDictionary<string, string[]> errors) =>
        new(CommandFailure.ValidationFailed, "One or more fields are invalid.", errors);

    public static CommandError Validation(string field, string message) =>
        Validation(new Dictionary<string, string[]> { [field] = [message] });

    /// <summary>422 VALIDATION_FAILED for a BR-14 duplicate warning without a continuation reason.</summary>
    public static CommandError DuplicateWarning(string field, string message, int duplicateCount) =>
        new(
            CommandFailure.ValidationFailed,
            "A continuation reason is required to submit a possible duplicate.",
            new Dictionary<string, string[]> { [field] = [message] },
            duplicateCount: duplicateCount);

    public static CommandError StateConflict(string message, int? activeChildCount = null) =>
        new(CommandFailure.StateConflict, message, activeChildCount: activeChildCount);
}

/// <summary>Either the command's value or a controlled <see cref="CommandError"/>.</summary>
public sealed class CommandResult<T>
{
    private CommandResult(T? value, CommandError? error)
    {
        Value = value;
        Error = error;
    }

    public T? Value { get; }

    public CommandError? Error { get; }

    public bool Succeeded => Error is null;

    public static CommandResult<T> Success(T value) => new(value, null);

    public static implicit operator CommandResult<T>(CommandError error) => new(default, error);
}
