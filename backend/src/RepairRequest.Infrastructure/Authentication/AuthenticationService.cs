using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RepairRequest.Application.Authentication;
using RepairRequest.Domain.Auditing;
using RepairRequest.Infrastructure.Identity;
using RepairRequest.Infrastructure.Persistence;

namespace RepairRequest.Infrastructure.Authentication;

/// <summary>
/// ASP.NET Core Identity login plus refresh-token rotation, reuse detection and revocation
/// (DEC-PS1-004; S1-002 decisions B1-B4, M1-M5). Passwords, tokens and emails are never logged
/// or audited. Security events for known users are appended to audit_history.
/// </summary>
internal sealed class AuthenticationService : IAuthenticationService
{
    private const string ResultSuccess = "SUCCESS";
    private const string ResultFailure = "FAILURE";

    private readonly RepairRequestDbContext _db;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly JwtAccessTokenIssuer _accessTokenIssuer;
    private readonly DummyPasswordHash _dummyPasswordHash;
    private readonly RefreshTokenOptions _refreshTokenOptions;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AuthenticationService> _logger;

    public AuthenticationService(
        RepairRequestDbContext db,
        UserManager<ApplicationUser> userManager,
        JwtAccessTokenIssuer accessTokenIssuer,
        DummyPasswordHash dummyPasswordHash,
        IOptions<RefreshTokenOptions> refreshTokenOptions,
        TimeProvider timeProvider,
        ILogger<AuthenticationService> logger)
    {
        _db = db;
        _userManager = userManager;
        _accessTokenIssuer = accessTokenIssuer;
        _dummyPasswordHash = dummyPasswordHash;
        _refreshTokenOptions = refreshTokenOptions.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<AuthenticationResult> LoginAsync(string email, string password, Guid correlationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(email);
        ArgumentNullException.ThrowIfNull(password);

        var user = await _userManager.FindByEmailAsync(email);
        if (user is null)
        {
            _dummyPasswordHash.Verify(password);
            _logger.LogWarning(
                "Authentication event {EventCode}: login identity not recognised. CorrelationId: {CorrelationId}",
                AuthenticationEventCodes.LoginFailed,
                correlationId);
            return AuthenticationResult.Failed;
        }

        if (await _userManager.IsLockedOutAsync(user))
        {
            _dummyPasswordHash.Verify(password);
            AddAudit(user.TenantId, user.Id, AuthenticationEventCodes.LoginRejectedLockedOut, ResultFailure, correlationId);
            await _db.SaveChangesAsync(cancellationToken);
            LogUserEvent(LogLevel.Warning, AuthenticationEventCodes.LoginRejectedLockedOut, user.Id, correlationId);
            return AuthenticationResult.Failed;
        }

        if (!await _userManager.CheckPasswordAsync(user, password))
        {
            await _userManager.AccessFailedAsync(user);
            AddAudit(user.TenantId, user.Id, AuthenticationEventCodes.LoginFailed, ResultFailure, correlationId);
            LogUserEvent(LogLevel.Warning, AuthenticationEventCodes.LoginFailed, user.Id, correlationId);

            if (await _userManager.IsLockedOutAsync(user))
            {
                AddAudit(user.TenantId, user.Id, AuthenticationEventCodes.AccountLockedOut, ResultFailure, correlationId);
                LogUserEvent(LogLevel.Warning, AuthenticationEventCodes.AccountLockedOut, user.Id, correlationId);
            }

            await _db.SaveChangesAsync(cancellationToken);
            return AuthenticationResult.Failed;
        }

        if (await _userManager.GetAccessFailedCountAsync(user) > 0)
        {
            await _userManager.ResetAccessFailedCountAsync(user);
        }

        var familyId = Guid.NewGuid();
        var tokens = await CreateTokensAsync(user.TenantId, user.Id, familyId, cancellationToken);
        AddAudit(user.TenantId, user.Id, AuthenticationEventCodes.LoginSucceeded, ResultSuccess, correlationId, familyId);
        await _db.SaveChangesAsync(cancellationToken);

        LogUserEvent(LogLevel.Information, AuthenticationEventCodes.LoginSucceeded, user.Id, correlationId);
        return AuthenticationResult.Success(tokens.Issued);
    }

    public async Task<AuthenticationResult> RefreshAsync(string refreshToken, Guid correlationId, CancellationToken cancellationToken)
    {
        var stored = await FindStoredTokenAsync(refreshToken, cancellationToken);
        if (stored is null)
        {
            _logger.LogWarning(
                "Authentication event {EventCode}: refresh token not recognised. CorrelationId: {CorrelationId}",
                AuthenticationEventCodes.RefreshRejected,
                correlationId);
            return AuthenticationResult.Failed;
        }

        var now = UtcNow();

        if (stored.ReplacedByTokenId is not null)
        {
            await RevokeFamilyAfterReuseAsync(stored, now, correlationId, cancellationToken);
            return AuthenticationResult.Failed;
        }

        if (stored.RevokedAt is not null || stored.ExpiresAt <= now.UtcDateTime)
        {
            var reason = stored.RevokedAt is not null ? "REVOKED" : "EXPIRED";
            AddAudit(stored.TenantId, stored.UserId, AuthenticationEventCodes.RefreshRejected, ResultFailure, correlationId, stored.FamilyId, reason);
            await _db.SaveChangesAsync(cancellationToken);
            LogUserEvent(LogLevel.Warning, AuthenticationEventCodes.RefreshRejected, stored.UserId, correlationId);
            return AuthenticationResult.Failed;
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        var replacement = await CreateTokensAsync(stored.TenantId, stored.UserId, stored.FamilyId, cancellationToken);

        // Atomic claim: only one caller can rotate a given token. A concurrent rotation of the same
        // token loses the claim and is treated as reuse (decision M1).
        var claimed = await _db.RefreshTokens
            .Where(token => token.Id == stored.Id && token.RevokedAt == null && token.ReplacedByTokenId == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(token => token.RevokedAt, (DateTime?)now.UtcDateTime)
                    .SetProperty(token => token.ReplacedByTokenId, (Guid?)replacement.Entity.Id),
                cancellationToken);

        if (claimed != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            _db.Entry(replacement.Entity).State = EntityState.Detached;
            await RevokeFamilyAfterReuseAsync(stored, now, correlationId, cancellationToken);
            return AuthenticationResult.Failed;
        }

        AddAudit(stored.TenantId, stored.UserId, AuthenticationEventCodes.TokenRefreshed, ResultSuccess, correlationId, stored.FamilyId);
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        LogUserEvent(LogLevel.Information, AuthenticationEventCodes.TokenRefreshed, stored.UserId, correlationId);
        return AuthenticationResult.Success(replacement.Issued);
    }

    public async Task RevokeAsync(string refreshToken, Guid correlationId, CancellationToken cancellationToken)
    {
        var stored = await FindStoredTokenAsync(refreshToken, cancellationToken);
        if (stored is null)
        {
            return;
        }

        var revokedCount = await RevokeFamilyAsync(stored.FamilyId, UtcNow(), cancellationToken);
        AddAudit(stored.TenantId, stored.UserId, AuthenticationEventCodes.SessionRevoked, ResultSuccess, correlationId, stored.FamilyId, revokedTokenCount: revokedCount);
        await _db.SaveChangesAsync(cancellationToken);

        LogUserEvent(LogLevel.Information, AuthenticationEventCodes.SessionRevoked, stored.UserId, correlationId);
    }

    private async Task<StoredToken?> FindStoredTokenAsync(string? refreshToken, CancellationToken cancellationToken)
    {
        if (!RefreshTokenCrypto.TryHash(refreshToken, out var tokenHash))
        {
            return null;
        }

        return await _db.RefreshTokens
            .AsNoTracking()
            .Where(token => token.TokenHash == tokenHash)
            .Select(token => new StoredToken(
                token.Id,
                token.TenantId,
                token.UserId,
                token.FamilyId,
                token.ExpiresAt,
                token.RevokedAt,
                token.ReplacedByTokenId))
            .SingleOrDefaultAsync(cancellationToken);
    }

    private async Task<(IssuedTokens Issued, RefreshToken Entity)> CreateTokensAsync(
        Guid tenantId,
        Guid userId,
        Guid familyId,
        CancellationToken cancellationToken)
    {
        var roleCodes = await (
                from userRole in _db.UserRoles.AsNoTracking()
                join role in _db.Roles.AsNoTracking() on userRole.RoleId equals role.Id
                where userRole.UserId == userId
                select role.Name!)
            .ToListAsync(cancellationToken);

        var accessToken = _accessTokenIssuer.Issue(userId, roleCodes);

        var now = UtcNow();
        var refreshExpiresAt = now + _refreshTokenOptions.Lifetime;
        var plaintext = RefreshTokenCrypto.Generate();

        var entity = new RefreshToken(
            tenantId,
            userId,
            familyId,
            RefreshTokenCrypto.Hash(plaintext),
            now.UtcDateTime,
            refreshExpiresAt.UtcDateTime);

        _db.RefreshTokens.Add(entity);

        return (new IssuedTokens(accessToken.Token, accessToken.ExpiresAt, plaintext, refreshExpiresAt), entity);
    }

    private async Task RevokeFamilyAfterReuseAsync(StoredToken stored, DateTimeOffset now, Guid correlationId, CancellationToken cancellationToken)
    {
        var revokedCount = await RevokeFamilyAsync(stored.FamilyId, now, cancellationToken);
        AddAudit(stored.TenantId, stored.UserId, AuthenticationEventCodes.RefreshTokenReuseDetected, ResultFailure, correlationId, stored.FamilyId, revokedTokenCount: revokedCount);
        await _db.SaveChangesAsync(cancellationToken);
        LogUserEvent(LogLevel.Warning, AuthenticationEventCodes.RefreshTokenReuseDetected, stored.UserId, correlationId);
    }

    private Task<int> RevokeFamilyAsync(Guid familyId, DateTimeOffset now, CancellationToken cancellationToken) =>
        _db.RefreshTokens
            .Where(token => token.FamilyId == familyId && token.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(token => token.RevokedAt, (DateTime?)now.UtcDateTime), cancellationToken);

    private void AddAudit(
        Guid tenantId,
        Guid userId,
        string eventCode,
        string result,
        Guid correlationId,
        Guid? tokenFamilyId = null,
        string? reason = null,
        int? revokedTokenCount = null)
    {
        var details = new Dictionary<string, object?> { ["result"] = result };
        if (tokenFamilyId is not null)
        {
            details["tokenFamilyId"] = tokenFamilyId;
        }

        if (reason is not null)
        {
            details["reason"] = reason;
        }

        if (revokedTokenCount is not null)
        {
            details["revokedTokenCount"] = revokedTokenCount;
        }

        _db.AuditHistory.Add(new AuditHistory(
            tenantId,
            AuthenticationEventCodes.AuditEntityType,
            userId,
            eventCode,
            fromState: null,
            toState: null,
            oldValueJson: null,
            newValueJson: JsonSerializer.Serialize(details),
            reason: null,
            actorId: userId,
            occurredAt: UtcNow().UtcDateTime,
            correlationId: correlationId));
    }

    private void LogUserEvent(LogLevel level, string eventCode, Guid userId, Guid correlationId) =>
        _logger.Log(
            level,
            "Authentication event {EventCode} for user {UserId}. CorrelationId: {CorrelationId}",
            eventCode,
            userId,
            correlationId);

    /// <summary>UTC now truncated to the datetime2(3) precision used by persisted timestamps.</summary>
    private DateTimeOffset UtcNow()
    {
        var now = _timeProvider.GetUtcNow();
        return now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMillisecond));
    }

    private sealed record StoredToken(
        Guid Id,
        Guid TenantId,
        Guid UserId,
        Guid FamilyId,
        DateTime ExpiresAt,
        DateTime? RevokedAt,
        Guid? ReplacedByTokenId);
}
