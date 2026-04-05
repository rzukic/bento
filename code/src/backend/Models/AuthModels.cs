using System.Text.Json.Serialization;

namespace Repere.Api.Models;

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
    [property: JsonPropertyName("refresh_cookie")] AuthRefreshCookieContractResponse RefreshCookie,
    [property: JsonPropertyName("deferred")] IReadOnlyList<string> Deferred);

public sealed record AuthRefreshCookieContractResponse(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("http_only")] bool HttpOnly,
    [property: JsonPropertyName("require_secure")] bool RequireSecure,
    [property: JsonPropertyName("same_site")] string SameSite,
    [property: JsonPropertyName("path")] string Path);

public sealed record RegisterUserRequest(
    [property: JsonPropertyName("username")] string? Username,
    [property: JsonPropertyName("password")] string? Password,
    [property: JsonPropertyName("role")] string? Role);

public sealed record RegisterUserResponse(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("bootstrap_admin")] bool BootstrapAdmin);
