namespace RepairRequest.Api.Contracts.Authentication;

/// <summary>
/// Login/refresh response. The refresh token is never part of the body; it is delivered only
/// in an HttpOnly, Secure, SameSite=Strict cookie (decision M2). The web client keeps the access
/// token in memory.
/// </summary>
public sealed record AccessTokenResponse(string AccessToken, string TokenType, DateTimeOffset ExpiresAt)
{
    public override string ToString() => $"AccessTokenResponse {{ TokenType = {TokenType}, ExpiresAt = {ExpiresAt:O} }}";
}
