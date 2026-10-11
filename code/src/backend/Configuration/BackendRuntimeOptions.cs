namespace Bento.Api.Configuration;

public sealed class BackendRuntimeOptions
{
    public int Port { get; set; } = 5000;

    public string LogLevel { get; set; } = "Information";

    public string[] AllowedOrigins { get; set; } = [];

    /// <summary>
    /// The address users reach Bento at (e.g. <c>https://bento.example.com</c>), for links
    /// the backend hands out: the MCP and OAuth metadata. Empty means "the address of the
    /// request", which is right unless a proxy in front hides the scheme or host.
    /// </summary>
    public string? PublicUrl { get; set; }

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

    /// <summary>
    /// Per-client limit for credential-bearing auth routes: login, register and the
    /// setup POSTs. Kept low because these are the brute-force targets.
    /// </summary>
    public int AuthRateLimitPerMinute { get; set; } = 5;

    /// <summary>
    /// Per-client limit for session routes (<c>POST /auth/refresh</c>, <c>POST /auth/logout</c>).
    /// The SPA refreshes on every page load, and a refresh needs a valid 256-bit
    /// refresh token, so these are not a guessing vector; the limit only caps
    /// abusive loops.
    /// </summary>
    public int AuthSessionRateLimitPerMinute { get; set; } = 120;

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
