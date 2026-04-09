using System.Text.Json.Serialization;

namespace Repere.Api.Models;

public sealed record AdminHealthDatabaseStatus(
    [property: JsonPropertyName("system_pool")] string SystemPool,
    [property: JsonPropertyName("data_pool")] string DataPool);

public sealed record AdminHealthRuntimeStatus(
    [property: JsonPropertyName("database_provider")] string DatabaseProvider,
    [property: JsonPropertyName("auth_mode")] string AuthMode,
    [property: JsonPropertyName("log_level")] string LogLevel,
    [property: JsonPropertyName("data_dir")] string DataDir,
    [property: JsonPropertyName("config_path")] string ConfigPath,
    [property: JsonPropertyName("jwt_configured")] bool JwtConfigured);

public sealed record AdminHealthResponse(
    string Status,
    AdminHealthDatabaseStatus Database,
    AdminHealthRuntimeStatus Runtime,
    string Uptime,
    string Version,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Error = null);

public sealed record AdminHealthResult(
    bool IsHealthy,
    AdminHealthResponse Response);
