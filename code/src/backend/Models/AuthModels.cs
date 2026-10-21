using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Bento.Api.Models;

public sealed record AuthRuntimeContractResponse(
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("auth_required")] bool AuthRequired,
    [property: JsonPropertyName("registration_bootstrap_open")] bool RegistrationBootstrapOpen,
    [property: JsonPropertyName("jwt_configured")] bool JwtConfigured,
    [property: JsonPropertyName("jwt_issuer")] string JwtIssuer,
    [property: JsonPropertyName("jwt_audience")] string JwtAudience,
    [property: JsonPropertyName("access_token_lifetime_minutes")] int AccessTokenLifetimeMinutes,
    [property: JsonPropertyName("refresh_token_lifetime_days")] int RefreshTokenLifetimeDays,
    [property: JsonPropertyName("auth_rate_limit_per_minute")] int AuthRateLimitPerMinute,
    [property: JsonPropertyName("session_rate_limit_per_minute")] int SessionRateLimitPerMinute,
    [property: JsonPropertyName("refresh_cookie")] AuthRefreshCookieContractResponse RefreshCookie,
    [property: JsonPropertyName("deferred")] IReadOnlyList<string> Deferred);

public sealed record AuthRefreshCookieContractResponse(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("http_only")] bool HttpOnly,
    [property: JsonPropertyName("require_secure")] bool RequireSecure,
    [property: JsonPropertyName("same_site")] string SameSite,
    [property: JsonPropertyName("path")] string Path);

public sealed class RegisterUserRequest
{
    public RegisterUserRequest()
    {
    }

    public RegisterUserRequest(string? username, string? password, string? role)
    {
        Username = username;
        Password = password;
        Role = role;
    }

    [Required(ErrorMessage = "Username is required.")]
    [StringLength(255, ErrorMessage = "Username must be 255 characters or fewer.")]
    [JsonPropertyName("username")]
    public string? Username { get; init; }

    [Required(ErrorMessage = "Password is required.")]
    [MinLength(8, ErrorMessage = "Password must be at least 8 characters long.")]
    [JsonPropertyName("password")]
    public string? Password { get; init; }

    [RegularExpression("^(admin|editor|viewer|api)?$", ErrorMessage = "Role must be one of: admin, editor, viewer, api.")]
    [JsonPropertyName("role")]
    public string? Role { get; init; }
}

public sealed record RegisterUserResponse(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("bootstrap_admin")] bool BootstrapAdmin);

public sealed class LoginRequest
{
    public LoginRequest()
    {
    }

    public LoginRequest(string? username, string? password)
    {
        Username = username;
        Password = password;
    }

    [Required(ErrorMessage = "Username is required.")]
    [JsonPropertyName("username")]
    public string? Username { get; init; }

    [Required(ErrorMessage = "Password is required.")]
    [JsonPropertyName("password")]
    public string? Password { get; init; }
}

public sealed record RefreshAccessTokenRequest();

/// <param name="Role">The role in the workspace of the request (Step 5.5): what the app may show and do there.</param>
/// <param name="InstallationRole">The user's own role; <c>admin</c> runs the installation (users, workspaces, vault).</param>
/// <param name="WorkspaceRole">The role in the workspace of the request; null when they are not a member of it.</param>
public sealed record AuthenticatedUserResponse(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("installation_role")] string? InstallationRole = null,
    [property: JsonPropertyName("workspace_role")] string? WorkspaceRole = null);

public sealed record AuthTokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("token_type")] string TokenType,
    [property: JsonPropertyName("expires_at")] string ExpiresAt,
    [property: JsonPropertyName("user")] AuthenticatedUserResponse User);
