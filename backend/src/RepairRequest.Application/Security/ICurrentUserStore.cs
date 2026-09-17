namespace RepairRequest.Application.Security;

/// <summary>Resolves a user's tenant and approved roles from the identity store in one bounded query.</summary>
public interface ICurrentUserStore
{
    /// <summary>Returns null when the user does not exist (for example, a token for a removed account).</summary>
    Task<CurrentUser?> FindAsync(Guid userId, CancellationToken cancellationToken);
}

/// <summary>The caller of the current request, resolved at most once per request.</summary>
public interface ICurrentUserAccessor
{
    /// <summary>Returns null when the request is unauthenticated or its subject cannot be resolved.</summary>
    Task<CurrentUser?> GetAsync(CancellationToken cancellationToken);
}
