using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bento.Api.Db.Schema;

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
            "uuid" => ColumnType.Uuid,
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
            ColumnType.Uuid => "uuid",
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

internal sealed class ColumnDefJsonConverter : JsonConverter<ColumnDef>
{
    public override ColumnDef Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        var additionalData = new Dictionary<string, JsonElement>();
        var typeConverter = new ColumnTypeJsonConverter();

        var name = string.Empty;
        string? displayName = null;
        string? description = null;
        ColumnType type = default;
        var typeSpecified = false;
        var nullable = true;
        var unique = false;
        var primaryKey = false;
        JsonElement? defaultValue = null;
        List<string> values = [];
        string? references = null;
        string? formula = null;
        string? resultType = null;
        string? relationName = null;
        string? renamedFrom = null;

        foreach (var property in root.EnumerateObject())
        {
            switch (property.Name)
            {
                case "name":
                    name = property.Value.GetString() ?? string.Empty;
                    break;
                case "displayName":
                    displayName = property.Value.ValueKind == JsonValueKind.Null ? null : property.Value.GetString();
                    break;
                case "description":
                    description = property.Value.ValueKind == JsonValueKind.Null ? null : property.Value.GetString();
                    break;
                case "type":
                    type = JsonSerializer.Deserialize<ColumnType>(property.Value.GetRawText(), options);
                    typeSpecified = true;
                    break;
                case "nullable":
                    nullable = property.Value.GetBoolean();
                    break;
                case "unique":
                    unique = property.Value.GetBoolean();
                    break;
                case "primaryKey":
                    primaryKey = property.Value.GetBoolean();
                    break;
                case "default":
                    defaultValue = property.Value.Clone();
                    break;
                case "values":
                    values = property.Value.Deserialize<List<string>>(options) ?? [];
                    break;
                case "references":
                    references = property.Value.ValueKind == JsonValueKind.Null ? null : property.Value.GetString();
                    break;
                case "formula":
                    formula = property.Value.ValueKind == JsonValueKind.Null ? null : property.Value.GetString();
                    break;
                case "resultType":
                    resultType = property.Value.ValueKind == JsonValueKind.Null ? null : property.Value.GetString();
                    break;
                case "relationName":
                    relationName = property.Value.ValueKind == JsonValueKind.Null ? null : property.Value.GetString();
                    break;
                case "renamedFrom":
                    renamedFrom = property.Value.ValueKind == JsonValueKind.Null ? null : property.Value.GetString();
                    break;
                default:
                    additionalData[property.Name] = property.Value.Clone();
                    break;
            }
        }

        return new ColumnDef
        {
            Name = name,
            DisplayName = displayName,
            Description = description,
            Type = type,
            TypeSpecified = typeSpecified,
            Nullable = nullable,
            Unique = unique,
            PrimaryKey = primaryKey,
            DefaultValue = defaultValue,
            Values = values,
            References = references,
            Formula = formula,
            ResultType = resultType,
            RelationName = relationName,
            RenamedFrom = renamedFrom,
            AdditionalData = additionalData.Count > 0 ? additionalData : null
        };
    }

    public override void Write(Utf8JsonWriter writer, ColumnDef value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("name", value.Name);

        if (value.DisplayName is not null)
            writer.WriteString("displayName", value.DisplayName);
        if (value.Description is not null)
            writer.WriteString("description", value.Description);

        writer.WritePropertyName("type");
        JsonSerializer.Serialize(writer, value.Type, options);

        if (!value.Nullable)
            writer.WriteBoolean("nullable", value.Nullable);
        if (value.Unique)
            writer.WriteBoolean("unique", value.Unique);
        if (value.PrimaryKey)
            writer.WriteBoolean("primaryKey", value.PrimaryKey);
        if (value.DefaultValue.HasValue)
        {
            writer.WritePropertyName("default");
            value.DefaultValue.Value.WriteTo(writer);
        }
        if (value.Values.Count > 0)
        {
            writer.WritePropertyName("values");
            JsonSerializer.Serialize(writer, value.Values, options);
        }
        if (value.References is not null)
            writer.WriteString("references", value.References);
        if (value.Formula is not null)
            writer.WriteString("formula", value.Formula);
        if (value.ResultType is not null)
            writer.WriteString("resultType", value.ResultType);
        if (value.RelationName is not null)
            writer.WriteString("relationName", value.RelationName);
        if (value.RenamedFrom is not null)
            writer.WriteString("renamedFrom", value.RenamedFrom);
        if (value.AdditionalData is not null)
        {
            foreach (var extra in value.AdditionalData)
            {
                writer.WritePropertyName(extra.Key);
                extra.Value.WriteTo(writer);
            }
        }

        writer.WriteEndObject();
    }
}

internal sealed class RelationDefJsonConverter : JsonConverter<RelationDef>
{
    private static readonly HashSet<string> RuntimeOnlyMetadataFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "resolvedFromAccessor",
        "resolvedToAccessor",
        "resolvedJunctionTableName"
    };

    public override RelationDef Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        var additionalData = new Dictionary<string, JsonElement>();
        var typeConverter = new RelationTypeJsonConverter();

        var name = string.Empty;
        RelationType type = default;
        var typeSpecified = false;
        var from = string.Empty;
        var to = string.Empty;
        string? fromAccessor = null;
        string? toAccessor = null;
        string? column = null;
        string? fromColumn = null;
        string? toColumn = null;
        string? junctionTable = null;

        foreach (var property in root.EnumerateObject())
        {
            switch (property.Name)
            {
                case "name":
                    name = property.Value.GetString() ?? string.Empty;
                    break;
                case "type":
                    type = JsonSerializer.Deserialize<RelationType>(property.Value.GetRawText(), options);
                    typeSpecified = true;
                    break;
                case "from":
                    from = property.Value.GetString() ?? string.Empty;
                    break;
                case "to":
                    to = property.Value.GetString() ?? string.Empty;
                    break;
                case "fromAccessor":
                    fromAccessor = property.Value.ValueKind == JsonValueKind.Null ? null : property.Value.GetString();
                    break;
                case "toAccessor":
                    toAccessor = property.Value.ValueKind == JsonValueKind.Null ? null : property.Value.GetString();
                    break;
                case "column":
                    column = property.Value.ValueKind == JsonValueKind.Null ? null : property.Value.GetString();
                    break;
                case "fromColumn":
                    fromColumn = property.Value.ValueKind == JsonValueKind.Null ? null : property.Value.GetString();
                    break;
                case "toColumn":
                    toColumn = property.Value.ValueKind == JsonValueKind.Null ? null : property.Value.GetString();
                    break;
                case "junctionTable":
                    junctionTable = property.Value.ValueKind == JsonValueKind.Null ? null : property.Value.GetString();
                    break;
                default:
                    if (!RuntimeOnlyMetadataFields.Contains(property.Name))
                    {
                        additionalData[property.Name] = property.Value.Clone();
                    }
                    break;
            }
        }

        return new RelationDef
        {
            Name = name,
            Type = type,
            TypeSpecified = typeSpecified,
            From = from,
            To = to,
            FromAccessor = fromAccessor,
            ToAccessor = toAccessor,
            Column = column,
            FromColumn = fromColumn,
            ToColumn = toColumn,
            JunctionTable = junctionTable,
            AdditionalData = additionalData.Count > 0 ? additionalData : null
        };
    }

    public override void Write(Utf8JsonWriter writer, RelationDef value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("name", value.Name);
        writer.WritePropertyName("type");
        JsonSerializer.Serialize(writer, value.Type, options);
        writer.WriteString("from", value.From);
        writer.WriteString("to", value.To);
        if (value.FromAccessor is not null)
            writer.WriteString("fromAccessor", value.FromAccessor);
        if (value.ToAccessor is not null)
            writer.WriteString("toAccessor", value.ToAccessor);

        if (value.Column is not null)
            writer.WriteString("column", value.Column);
        if (value.FromColumn is not null)
            writer.WriteString("fromColumn", value.FromColumn);
        if (value.ToColumn is not null)
            writer.WriteString("toColumn", value.ToColumn);
        if (value.JunctionTable is not null)
            writer.WriteString("junctionTable", value.JunctionTable);
        if (value.AdditionalData is not null)
        {
            foreach (var extra in value.AdditionalData)
            {
                writer.WritePropertyName(extra.Key);
                extra.Value.WriteTo(writer);
            }
        }

        writer.WriteEndObject();
    }
}
