using System.Text.Json;
using System.Text.Json.Serialization;

namespace Repere.Api.Db.Schema;

public sealed record ModelJson
{
    [JsonPropertyName("version")]
    public int Version { get; init; }

    [JsonPropertyName("tables")]
    public List<TableDef> Tables { get; init; } = [];

    [JsonPropertyName("relations")]
    public List<RelationDef> Relations { get; init; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; init; }
}

public sealed record TableDef
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("renamedFrom")]
    public string? RenamedFrom { get; init; }

    [JsonPropertyName("columns")]
    public List<ColumnDef> Columns { get; init; } = [];

    [JsonPropertyName("indexes")]
    public List<IndexDef> Indexes { get; init; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; init; }
}

[JsonConverter(typeof(ColumnDefJsonConverter))]
public sealed record ColumnDef
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("type")]
    public ColumnType Type { get; init; }

    [JsonIgnore]
    public bool TypeSpecified { get; init; }

    [JsonPropertyName("nullable")]
    public bool Nullable { get; init; } = true;

    [JsonPropertyName("unique")]
    public bool Unique { get; init; }

    [JsonPropertyName("primaryKey")]
    public bool PrimaryKey { get; init; }

    [JsonPropertyName("default")]
    public JsonElement? DefaultValue { get; init; }

    [JsonIgnore]
    public string? InternalDefaultSql { get; init; }

    [JsonPropertyName("values")]
    public List<string> Values { get; init; } = [];

    [JsonPropertyName("references")]
    public string? References { get; init; }

    [JsonPropertyName("formula")]
    public string? Formula { get; init; }

    [JsonPropertyName("relationName")]
    public string? RelationName { get; init; }

    [JsonPropertyName("renamedFrom")]
    public string? RenamedFrom { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; init; }
}

[JsonConverter(typeof(RelationDefJsonConverter))]
public sealed record RelationDef
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("type")]
    public RelationType Type { get; init; }

    [JsonIgnore]
    public bool TypeSpecified { get; init; }

    [JsonPropertyName("from")]
    public string From { get; init; } = string.Empty;

    [JsonPropertyName("to")]
    public string To { get; init; } = string.Empty;

    [JsonPropertyName("fromAccessor")]
    public string? FromAccessor { get; init; }

    [JsonPropertyName("toAccessor")]
    public string? ToAccessor { get; init; }

    [JsonPropertyName("column")]
    public string? Column { get; init; }

    [JsonPropertyName("fromColumn")]
    public string? FromColumn { get; init; }

    [JsonPropertyName("toColumn")]
    public string? ToColumn { get; init; }

    [JsonPropertyName("junctionTable")]
    public string? JunctionTable { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; init; }
}

public sealed record IndexDef
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("columns")]
    public List<string> Columns { get; init; } = [];

    [JsonPropertyName("unique")]
    public bool Unique { get; init; }

    [JsonPropertyName("renamedFrom")]
    public string? RenamedFrom { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; init; }
}

public enum RelationType
{
    OneToMany,
    ManyToMany
}
