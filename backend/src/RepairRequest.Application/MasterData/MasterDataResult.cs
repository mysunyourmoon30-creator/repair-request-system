namespace RepairRequest.Application.MasterData;

/// <summary>Controlled failure outcomes of master-data commands (RR-API-001 section 2 error contract).</summary>
public enum MasterDataFailure
{
    /// <summary>404 NOT_FOUND: nonexistent or outside the caller's tenant/site scope (identical response).</summary>
    NotFound,

    /// <summary>422 VALIDATION_FAILED: field, duplicate-code or inactive-parent validation.</summary>
    ValidationFailed,

    /// <summary>409 STATE_CONFLICT: illegal state change or deactivation dependency guard (DEC-PS1-013).</summary>
    StateConflict,

    /// <summary>409 CONCURRENCY_CONFLICT: stale row version; nothing was written.</summary>
    ConcurrencyConflict
}

public sealed class MasterDataError
{
    private MasterDataError(
        MasterDataFailure failure,
        string? message = null,
        IReadOnlyDictionary<string, string[]>? errors = null,
        int? activeChildCount = null)
    {
        Failure = failure;
        Message = message;
        Errors = errors ?? new Dictionary<string, string[]>();
        ActiveChildCount = activeChildCount;
    }

    public MasterDataFailure Failure { get; }

    public string? Message { get; }

    public IReadOnlyDictionary<string, string[]> Errors { get; }

    /// <summary>Number of active children that blocked a deactivation (RR-DEC-001 DEC-PS1-013 UI note).</summary>
    public int? ActiveChildCount { get; }

    public static MasterDataError NotFound { get; } = new(MasterDataFailure.NotFound);

    public static MasterDataError ConcurrencyConflict { get; } =
        new(MasterDataFailure.ConcurrencyConflict, "The record was changed by another request. Reload it before retrying.");

    public static MasterDataError Validation(IReadOnlyDictionary<string, string[]> errors) =>
        new(MasterDataFailure.ValidationFailed, "One or more fields are invalid.", errors);

    public static MasterDataError Validation(string field, string message) =>
        Validation(new Dictionary<string, string[]> { [field] = [message] });

    public static MasterDataError StateConflict(string message, int? activeChildCount = null) =>
        new(MasterDataFailure.StateConflict, message, activeChildCount: activeChildCount);
}

/// <summary>Either the command's value or a controlled <see cref="MasterDataError"/>.</summary>
public sealed class MasterDataResult<T>
{
    private MasterDataResult(T? value, MasterDataError? error)
    {
        Value = value;
        Error = error;
    }

    public T? Value { get; }

    public MasterDataError? Error { get; }

    public bool Succeeded => Error is null;

    public static MasterDataResult<T> Success(T value) => new(value, null);

    public static implicit operator MasterDataResult<T>(MasterDataError error) => new(default, error);
}
