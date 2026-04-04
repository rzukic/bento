namespace Repere.Api.Configuration;

public sealed class BackendRuntimeOptions
{
    public int Port { get; set; } = 5000;

    public string LogLevel { get; set; } = "Information";

    public string[] AllowedOrigins { get; set; } = [];

    public string MaxBodySize { get; set; } = "10mb";

    public string AuthMode { get; set; } = "standard";

    public string? JwtSecret { get; set; }

    public string DataDir { get; set; } = "./data";

    public string? DatabaseUrl { get; set; }

    public long GetMaxBodySizeBytes()
    {
        return BackendRuntimeConfiguration.ParseByteSize(MaxBodySize);
    }
}
