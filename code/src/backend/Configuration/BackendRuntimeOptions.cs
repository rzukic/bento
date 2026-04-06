namespace Repere.Api.Configuration;

public sealed class BackendRuntimeOptions
{
    public int Port { get; set; } = 5000;

    public string LogLevel { get; set; } = "Information";

    public string[] AllowedOrigins { get; set; } = [];

    public string MaxBodySize { get; set; } = "10mb";

    public BackendAuthMode AuthMode { get; set; } = BackendAuthMode.Standard;

    public JwtRuntimeOptions Jwt { get; set; } = new();

    public AuthCookieRuntimeOptions AuthCookie { get; set; } = new();

    public int AuthRateLimitPerMinute { get; set; } = 5;

    public string DataDir { get; set; } = "./data";

    public string? DatabaseUrl { get; set; }

    public bool IsOpenAuthMode => AuthMode == BackendAuthMode.Open;

    public bool IsStandardAuthMode => AuthMode == BackendAuthMode.Standard;

    public long GetMaxBodySizeBytes()
    {
        return BackendRuntimeConfiguration.ParseByteSize(MaxBodySize);
    }
}
