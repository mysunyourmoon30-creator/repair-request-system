using System.ComponentModel.DataAnnotations;

namespace RepairRequest.Api.Contracts.Authentication;

/// <summary>POST /api/v1/auth/login request. Email is the login identifier (decision M3).</summary>
public sealed class LoginRequest
{
    /// <summary>Matches the ASP.NET Core Identity email column length.</summary>
    [Required]
    [EmailAddress]
    [MaxLength(256)]
    public string Email { get; init; } = string.Empty;

    [Required]
    public string Password { get; init; } = string.Empty;
}
