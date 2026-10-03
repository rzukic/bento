namespace Repere.Api.Configuration;

public sealed class BackendRuntimeOptions
{
    public int Port { get; set; } = 5000;

    public string LogLevel { get; set; } = "Information";

    public string[] AllowedOrigins { get; set; } = [];

    /// <summary>
    /// IP addresses or CIDR ranges whose <c>X-Forwarded-*</c> headers are trusted.
    /// Empty (the default) means forwarded headers are ignored entirely.
    /// </summary>
    public string[] TrustedProxies { get; set; } = [];

    public bool HasTrustedProxies => TrustedProxies.Any(entry => !string.IsNullOrWhiteSpace(entry));

    public string MaxBodySize { get; set; } = "10mb";

    public BackendAuthMode AuthMode { get; set; } = BackendAuthMode.Standard;

    public JwtRuntimeOptions Jwt { get; set; } = new();

    public AuthCookieRuntimeOptions AuthCookie { get; set; } = new();

    public int AuthRateLimitPerMinute { get; set; } = 5;

    public string DataDir { get; set; } = "./data";

    public string? DatabaseUrl { get; set; }

    public string PersistentConfigPath { get; set; } = "./data/config.json";

    public bool IsOpenAuthMode => AuthMode == BackendAuthMode.Open;

    public bool IsStandardAuthMode => AuthMode == BackendAuthMode.Standard;

    public long GetMaxBodySizeBytes()
    {
        return BackendRuntimeConfiguration.ParseByteSize(MaxBodySize);
    }
}
