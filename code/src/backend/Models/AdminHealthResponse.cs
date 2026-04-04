namespace Repere.Api.Models;

public sealed record AdminHealthResponse(
    string Status,
    string Database,
    string SystemPool,
    string DataPool,
    string? Error = null);

public sealed record AdminHealthResult(
    bool IsHealthy,
    AdminHealthResponse Response);
