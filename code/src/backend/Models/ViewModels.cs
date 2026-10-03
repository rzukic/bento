using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Repere.Api.Models;

/// <summary>
/// View type determines the renderer used in the Data Explorer.
/// Serialized as lowercase strings ("table" | "kanban").
/// </summary>
[JsonConverter(typeof(ViewTypeJsonConverter))]
public enum ViewType
{
    Table,
    Kanban
}

public sealed class ViewTypeJsonConverter : JsonConverter<ViewType>
{
    public override ViewType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var str = reader.GetString();
        return str switch
        {
            "table"  => ViewType.Table,
            "kanban" => ViewType.Kanban,
            _        => throw new JsonException($"Unknown view type: '{str}'. Expected 'table' or 'kanban'.")
        };
    }

    public override void Write(Utf8JsonWriter writer, ViewType value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value switch
        {
            ViewType.Table  => "table",
            ViewType.Kanban => "kanban",
            _               => throw new JsonException($"Unknown ViewType value: {value}")
        });
    }
}

/// <summary>
/// DB row model for the system_views table.
/// Queries must alias snake_case columns to PascalCase names (e.g. table_name AS TableName).
/// </summary>
public sealed class SystemView
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string TableName { get; init; } = string.Empty;
    public int OwnerUserId { get; init; }
    /// <summary>Raw type string from DB ("table" | "kanban"). Parse via ViewType enum when needed.</summary>
    public string Type { get; init; } = "table";
    /// <summary>JSON blob. Deserialize to the appropriate config type based on Type.</summary>
    public string Config { get; init; } = "{}";
    /// <summary>SQLite stores as INTEGER (0/1); Dapper maps to bool automatically.</summary>
    public bool IsShared { get; init; }
    public string CreatedAt { get; init; } = string.Empty;
    public string UpdatedAt { get; init; } = string.Empty;

    public ViewType ViewType => Type switch
    {
        "kanban" => ViewType.Kanban,
        _        => ViewType.Table
    };
}

// ── Request / Response DTOs ────────────────────────────────────────────────

public sealed class CreateViewRequest
{
    [Required(ErrorMessage = "name is required.")]
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [Required(ErrorMessage = "table_name is required.")]
    [JsonPropertyName("table_name")]
    public string? TableName { get; init; }

    [Required(ErrorMessage = "type is required.")]
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    /// <summary>Must be a valid JSON object. Stored opaque; content not inspected by the backend.</summary>
    [Required(ErrorMessage = "config is required.")]
    [JsonPropertyName("config")]
    public JsonElement? Config { get; init; }

    [JsonPropertyName("is_shared")]
    public bool IsShared { get; init; }
}

/// <summary>All fields are optional — only supplied fields are updated.</summary>
public sealed class UpdateViewRequest
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("config")]
    public JsonElement? Config { get; init; }

    [JsonPropertyName("is_shared")]
    public bool? IsShared { get; init; }
}
