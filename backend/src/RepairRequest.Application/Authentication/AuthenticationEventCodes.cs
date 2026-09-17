namespace RepairRequest.Application.Authentication;

/// <summary>
/// Structured security event codes written to audit_history.action_code (entity_type USER)
/// and to structured logs. Values never include credentials or token material.
/// </summary>
public static class AuthenticationEventCodes
{
    public const string AuditEntityType = "USER";

    public const string LoginSucceeded = "AUTH_LOGIN_SUCCEEDED";
    public const string LoginFailed = "AUTH_LOGIN_FAILED";
    public const string AccountLockedOut = "AUTH_ACCOUNT_LOCKED_OUT";
    public const string LoginRejectedLockedOut = "AUTH_LOGIN_REJECTED_LOCKED_OUT";
    public const string TokenRefreshed = "AUTH_TOKEN_REFRESHED";
    public const string RefreshRejected = "AUTH_REFRESH_REJECTED";
    public const string RefreshTokenReuseDetected = "AUTH_REFRESH_TOKEN_REUSE_DETECTED";
    public const string SessionRevoked = "AUTH_SESSION_REVOKED";
}
