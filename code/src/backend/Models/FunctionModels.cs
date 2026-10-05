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
}

/// <summary>Runs code in the sandbox without saving it: the editor's test run.</summary>
public sealed class CustomFunctionTestRequest
{
    public List<CustomFunctionArgument>? Arguments { get; init; }
    public string? ResultType { get; init; }
    public string? Code { get; init; }
    /// <summary>One value per argument, as JSON (numbers, text, true/false, "yyyy-MM-dd", null).</summary>
    public List<JsonElement>? Values { get; init; }
}

public sealed record CustomFunctionTestResponse(object? Value, string ResultType, string? Error, IReadOnlyList<string> Logs, double DurationMs);

/// <summary>Where a function is used: computed columns (table.column) and dashboards.</summary>
public sealed record CustomFunctionUsage(string Kind, string Name, string? Table = null, string? Column = null, long? DashboardId = null);

public sealed record CustomFunctionResponse(
    string Name,
    string? Description,
    IReadOnlyList<CustomFunctionArgument> Arguments,
    string ResultType,
    string Code,
    int Version,
    string CreatedAt,
    string UpdatedAt,
    IReadOnlyList<CustomFunctionUsage> UsedBy);

public sealed record CustomFunctionVersionResponse(int Version, IReadOnlyList<CustomFunctionArgument> Arguments, string ResultType, string Code, string CreatedAt);
