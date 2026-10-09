using System.Text.Json;
using Bento.Api.Formula;

namespace Bento.Api.Models;

/// <summary>Create or change a custom function (Step 4.1). The name cannot change once created.</summary>
public sealed class CustomFunctionRequest
{
    public string? Name { get; init; }
    public string? Description { get; init; }
    public List<CustomFunctionArgument>? Arguments { get; init; }
    public string? ResultType { get; init; }
    public string? Code { get; init; }
    /// <summary>Step 4.2: hosts fetch may call (empty: no network). Such functions run as price sources, not in formulas.</summary>
    public List<string>? AllowedHosts { get; init; }
    /// <summary>Step 4.2: stored credentials the code reads as credentials.name.</summary>
    public List<string>? Credentials { get; init; }
    /// <summary>Step 4.3b: "row" (default) or "rows" — one call per group of rows, arguments as lists (CUSTOM_ROWS).</summary>
    public string? Mode { get; init; }
}

/// <summary>Runs code in the sandbox without saving it: the editor's test run.</summary>
public sealed class CustomFunctionTestRequest
{
    public List<CustomFunctionArgument>? Arguments { get; init; }
    public string? ResultType { get; init; }
    public string? Code { get; init; }
    public List<string>? AllowedHosts { get; init; }
    public List<string>? Credentials { get; init; }
    public string? Mode { get; init; }
    /// <summary>One value per argument (rows mode: a list per argument), as JSON (numbers, text, true/false, "yyyy-MM-dd", null).</summary>
    public List<JsonElement>? Values { get; init; }
}

public sealed record CustomFunctionTestResponse(object? Value, string ResultType, string? Error, IReadOnlyList<string> Logs, double DurationMs, int Requests = 0);

/// <summary>Where a function is used: computed columns (table.column), dashboards and price sources (kind "priceSource").</summary>
public sealed record CustomFunctionUsage(string Kind, string Name, string? Table = null, string? Column = null, long? DashboardId = null, long? SourceId = null);

public sealed record CustomFunctionResponse(
    string Name,
    string? Description,
    IReadOnlyList<CustomFunctionArgument> Arguments,
    string ResultType,
    string Code,
    int Version,
    string CreatedAt,
    string UpdatedAt,
    IReadOnlyList<CustomFunctionUsage> UsedBy,
    IReadOnlyList<string> AllowedHosts,
    IReadOnlyList<string> Credentials,
    bool UsableInFormulas,
    string Mode = "row");

public sealed record CustomFunctionVersionResponse(int Version, IReadOnlyList<CustomFunctionArgument> Arguments, string ResultType, string Code, string CreatedAt,
    IReadOnlyList<string> AllowedHosts, IReadOnlyList<string> Credentials);
