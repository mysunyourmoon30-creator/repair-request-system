namespace RepairRequest.Infrastructure.Identity;

/// <summary>
/// Persisted refresh token (DEC-PS1-004; S1-002 decisions B4/M1/M4). Only a SHA-256 hash of
/// the 256-bit random token is stored. Tokens issued by one login share a family (session):
/// rotation revokes the presented token and links it to its replacement; presenting an
/// already-replaced token is treated as reuse and revokes the whole family.
/// </summary>
public sealed class RefreshToken
{
    public const int TokenHashLength = 32;

    private RefreshToken()
    {
        TokenHash = null!;
    }

    public RefreshToken(Guid tenantId, Guid userId, Guid familyId, byte[] tokenHash, DateTime createdAt, DateTime expiresAt)
    {
        if (tenantId == Guid.Empty || userId == Guid.Empty || familyId == Guid.Empty)
        {
            throw new ArgumentException("Tenant, user and family identifiers are required.");
        }

        if (tokenHash is null || tokenHash.Length != TokenHashLength)
        {
            throw new ArgumentException($"Token hash must be {TokenHashLength} bytes.", nameof(tokenHash));
        }

        if (createdAt.Kind != DateTimeKind.Utc || expiresAt.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Refresh token timestamps must be UTC.");
        }

        if (expiresAt <= createdAt)
        {
            throw new ArgumentException("Expiry must be later than creation.", nameof(expiresAt));
        }

        TenantId = tenantId;
        UserId = userId;
        FamilyId = familyId;
        TokenHash = tokenHash;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid UserId { get; private set; }

    /// <summary>Session identifier shared by every token rotated from one login.</summary>
    public Guid FamilyId { get; private set; }

    /// <summary>SHA-256 of the decoded token bytes. The plaintext token is never persisted.</summary>
    public byte[] TokenHash { get; private set; }

    public DateTime CreatedAt { get; private set; }

    public DateTime ExpiresAt { get; private set; }

    /// <summary>Set when the token is rotated, explicitly revoked, or its family is revoked.</summary>
    public DateTime? RevokedAt { get; private set; }

    /// <summary>Set only by rotation; a non-null value marks the token as already used.</summary>
    public Guid? ReplacedByTokenId { get; private set; }
}
