using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bento.Api.Models;

/// <summary>
/// Represents an optional integer JSON property that must distinguish between:
/// omitted (HasValue = false), explicit null (HasValue = true, Value = null),
/// and a concrete integer (HasValue = true, Value = n).
/// </summary>
[JsonConverter(typeof(OptionalNullableInt32JsonConverter))]
public readonly struct OptionalNullableInt32
{
    public bool HasValue { get; }
    public int? Value { get; }

    public OptionalNullableInt32(bool hasValue, int? value)
    {
        HasValue = hasValue;
        Value = value;
    }
}

public sealed class OptionalNullableInt32JsonConverter : JsonConverter<OptionalNullableInt32>
{
    public override OptionalNullableInt32 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return new OptionalNullableInt32(hasValue: true, value: null);
        }

        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var value))
        {
            return new OptionalNullableInt32(hasValue: true, value: value);
        }

        throw new JsonException("Expected an integer or null.");
    }

    public override void Write(Utf8JsonWriter writer, OptionalNullableInt32 value, JsonSerializerOptions options)
    {
        if (!value.HasValue)
        {
            writer.WriteNullValue();
            return;
        }

        if (value.Value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteNumberValue(value.Value.Value);
    }
}

/// <summary>
/// DB row model for user-specific Data Explorer preferences per table.
/// </summary>
public sealed class UserTableExplorerPreferences
{
    public int UserId { get; init; }
    public string TableName { get; init; } = string.Empty;
    public int? DefaultViewId { get; init; }
    /// <summary>SQLite stores as INTEGER (0/1); Dapper maps to bool automatically.</summary>
    public bool AutoSave { get; init; }
    public string CreatedAt { get; init; } = string.Empty;
    public string UpdatedAt { get; init; } = string.Empty;
}

/// <summary>
/// PATCH-like update payload for per-table Data Explorer preferences.
/// Properties may be omitted to leave them unchanged.
/// </summary>
public sealed class UpdateExplorerTablePreferencesRequest
{
    /// <summary>
    /// When present: may be an integer id or null to clear.
    /// When omitted: default view is unchanged.
    /// </summary>
    [JsonPropertyName("default_view_id")]
    public OptionalNullableInt32 DefaultViewId { get; init; }

    /// <summary>
    /// When present: enables/disables auto-save.
    /// When omitted: auto-save is unchanged.
    /// </summary>
    [JsonPropertyName("auto_save")]
    public bool? AutoSave { get; init; }
}
