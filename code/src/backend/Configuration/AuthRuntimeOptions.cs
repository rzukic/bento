namespace Bento.Api.Configuration;

public enum BackendAuthMode
{
    Open,
    Standard,
    Vault
}

public sealed class JwtRuntimeOptions
{
    public string? Secret { get; set; }

    public string Issuer { get; set; } = "bento-api";

    public string Audience { get; set; } = "bento-clients";

    public int AccessTokenLifetimeMinutes { get; set; } = 15;

    public int RefreshTokenLifetimeDays { get; set; } = 7;
}

public sealed class AuthCookieRuntimeOptions
{
    public string RefreshCookieName { get; set; } = "bento_refresh";

    public bool HttpOnly { get; set; } = true;

    public bool? RequireSecure { get; set; }

    public bool EffectiveRequireSecure { get; set; }

    public string SameSite { get; set; } = "Strict";

    public string Path { get; set; } = "/" + ApiRoutes.Auth;
}
