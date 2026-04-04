using System.Text.Json;
using System.Text.Json.Serialization;

namespace Repere.Api.Db.Schema;

internal sealed class ColumnTypeJsonConverter : JsonConverter<ColumnType>
{
    public override ColumnType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();

        return value?.ToLowerInvariant() switch
        {
            "text" => ColumnType.Text,
            "integer" => ColumnType.Integer,
            "float" => ColumnType.Float,
            "boolean" => ColumnType.Boolean,
            "date" => ColumnType.Date,
            "datetime" => ColumnType.DateTime,
            "json" => ColumnType.Json,
            "reference" => ColumnType.Reference,
            "computed" => ColumnType.Computed,
            "currency" => ColumnType.Currency,
            "enum" => ColumnType.Enum,
            "file" => ColumnType.File,
            _ => throw new JsonException($"Unsupported column type '{value}'.")
        };
    }

    public override void Write(Utf8JsonWriter writer, ColumnType value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value switch
        {
            ColumnType.Text => "text",
            ColumnType.Integer => "integer",
            ColumnType.Float => "float",
            ColumnType.Boolean => "boolean",
            ColumnType.Date => "date",
            ColumnType.DateTime => "datetime",
            ColumnType.Json => "json",
            ColumnType.Reference => "reference",
            ColumnType.Computed => "computed",
            ColumnType.Currency => "currency",
            ColumnType.Enum => "enum",
            ColumnType.File => "file",
            _ => throw new JsonException($"Unsupported column type '{value}'.")
        });
    }
}

internal sealed class RelationTypeJsonConverter : JsonConverter<RelationType>
{
    public override RelationType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();

        return value switch
        {
            "1:N" => RelationType.OneToMany,
            "N:M" => RelationType.ManyToMany,
            _ => throw new JsonException($"Unsupported relation type '{value}'.")
        };
    }

    public override void Write(Utf8JsonWriter writer, RelationType value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value switch
        {
            RelationType.OneToMany => "1:N",
            RelationType.ManyToMany => "N:M",
            _ => throw new JsonException($"Unsupported relation type '{value}'.")
        });
    }
}
