using System.Text.Json.Serialization;

namespace Repere.Api.Models;

public sealed record AdminHealthDatabaseStatus(
    [property: JsonPropertyName("system_pool")] string SystemPool,
    [property: JsonPropertyName("data_pool")] string DataPool);

public sealed record AdminHealthResponse(
    string Status,
    AdminHealthDatabaseStatus Database,
    string Uptime,
    string Version,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Error = null);

public sealed record AdminHealthResult(
    bool IsHealthy,
    AdminHealthResponse Response);
