using System.Text.Json;
using System.Text.Json.Nodes;
using Bento.Api.Exceptions;

namespace Bento.Api.Db.Schema;

/// <summary>
/// Schema changes as operations on the current model, instead of the whole
/// Model JSON each time (beta feedback: ~30 KB per new column, from MCP apps).
/// The operations edit the current model's JSON; the result goes through the
/// same validation, preview and migration as a full model, so a patch can do
/// nothing a full model could not.
/// <code>
/// { "operations": [
///   { "op": "addColumn", "table": "trades", "column": { "name": "note", "type": "text" }, "after": "price" },
///   { "op": "changeColumn", "table": "trades", "column": "fees", "set": { "displayName": "Spesen" } },
///   { "op": "renameColumn", "table": "trades", "column": "fees", "to": "costs" } ] }
/// </code>
/// </summary>
public static class SchemaPatch
{
    public static readonly string[] Operations =
    [
        "addTable", "changeTable", "renameTable", "removeTable",
        "addColumn", "changeColumn", "renameColumn", "removeColumn",
        "addRelation", "changeRelation", "removeRelation",
        "addIndex", "removeIndex"
    ];

    /// <summary>The model after the operations, in order. Errors name the operation (<c>operations[2]</c>).</summary>
    public static ModelJson Apply(ModelJson current, JsonElement body)
    {
        var operations = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("operations", out var list) ? list : body;
        if (operations.ValueKind != JsonValueKind.Array || operations.GetArrayLength() == 0)
            throw Invalid("operations", "Send { \"operations\": [ { \"op\": \"addColumn\", … } ] } with at least one operation.");

        var model = JsonNode.Parse(ModelJsonSerializer.Serialize(current))!.AsObject();
        var tables = model["tables"] as JsonArray ?? (JsonArray)(model["tables"] = new JsonArray());
        var relations = model["relations"] as JsonArray ?? (JsonArray)(model["relations"] = new JsonArray());

        var index = 0;
        foreach (var operation in operations.EnumerateArray())
        {
            var path = $"operations[{index++}]";
            if (operation.ValueKind != JsonValueKind.Object)
                throw Invalid(path, "Each operation is an object with \"op\".");
            var op = Text(operation, "op", path);
            switch (op)
            {
                case "addTable":
                {
                    var table = Object(operation, "table", path);
                    var name = Name(table, path + ".table");
                    if (Find(tables, name) is not null)
                        throw Invalid(path, $"The table '{name}' already exists.");
                    table["columns"] ??= new JsonArray();
                    tables.Add(table);
                    break;
                }
                case "changeTable":
                    Merge(Table(tables, operation, path), Object(operation, "set", path), path, "columns", "indexes");
                    break;
                case "renameTable":
                {
                    var table = Table(tables, operation, path);
                    var from = table["name"]!.GetValue<string>();
                    var to = Text(operation, "to", path);
                    if (Find(tables, to) is not null)
                        throw Invalid(path, $"The table '{to}' already exists.");
                    table["name"] = to;
                    table["renamedFrom"] = from;
                    break;
                }
                case "removeTable":
                {
                    var table = Table(tables, operation, path);
                    tables.Remove(table);
                    break;
                }
                case "addColumn":
                {
                    var columns = Columns(Table(tables, operation, path));
                    var column = Object(operation, "column", path);
                    var name = Name(column, path + ".column");
                    if (Find(columns, name) is not null)
                        throw Invalid(path, $"The column '{name}' already exists.");
                    var after = operation.TryGetProperty("after", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
                    if (after is null)
                    {
                        columns.Add(column);
                    }
                    else
                    {
                        var previous = Find(columns, after) ?? throw Invalid($"{path}.after", $"There is no column '{after}' to add it after.");
                        columns.Insert(columns.IndexOf(previous) + 1, column);
                    }
                    break;
                }
                case "changeColumn":
                    Merge(Column(tables, operation, path), Object(operation, "set", path), path, "name");
                    break;
                case "renameColumn":
                {
                    var columns = Columns(Table(tables, operation, path));
                    var column = Column(tables, operation, path);
                    var from = column["name"]!.GetValue<string>();
                    var to = Text(operation, "to", path);
                    if (Find(columns, to) is not null)
                        throw Invalid(path, $"The column '{to}' already exists.");
                    column["name"] = to;
                    column["renamedFrom"] = from;
                    break;
                }
                case "removeColumn":
                {
                    var columns = Columns(Table(tables, operation, path));
                    columns.Remove(Column(tables, operation, path));
                    break;
                }
                case "addRelation":
                {
                    var relation = Object(operation, "relation", path);
                    var name = Name(relation, path + ".relation");
                    if (Find(relations, name) is not null)
                        throw Invalid(path, $"The relation '{name}' already exists.");
                    relations.Add(relation);
                    break;
                }
                case "changeRelation":
                    Merge(Relation(relations, operation, path), Object(operation, "set", path), path, "name");
                    break;
                case "removeRelation":
                    relations.Remove(Relation(relations, operation, path));
                    break;
                case "addIndex":
                {
                    var table = Table(tables, operation, path);
                    var indexes = table["indexes"] as JsonArray ?? (JsonArray)(table["indexes"] = new JsonArray());
                    var definition = Object(operation, "index", path);
                    var name = Name(definition, path + ".index");
                    if (Find(indexes, name) is not null)
                        throw Invalid(path, $"The index '{name}' already exists.");
                    indexes.Add(definition);
                    break;
                }
                case "removeIndex":
                {
                    var table = Table(tables, operation, path);
                    var indexes = table["indexes"] as JsonArray;
                    var name = Text(operation, "index", path);
                    var definition = (indexes is null ? null : Find(indexes, name))
                        ?? throw Invalid($"{path}.index", $"The table has no index '{name}'.");
                    indexes!.Remove(definition);
                    break;
                }
                default:
                    throw Invalid($"{path}.op", $"Unknown operation '{op}'; use {string.Join(", ", Operations)}.");
            }
        }

        // The version is the current one: the change is made against what is there now.
        model["version"] = current.Version;
        try
        {
            return ModelJsonSerializer.Parse(model.ToJsonString());
        }
        catch (JsonException e)
        {
            throw Invalid("operations", $"The changed model cannot be read: {e.Message}");
        }
    }

    private static JsonObject Table(JsonArray tables, JsonElement operation, string path)
    {
        var name = Text(operation, "table", path);
        return Find(tables, name) ?? throw Invalid($"{path}.table", $"There is no table '{name}'.");
    }

    private static JsonArray Columns(JsonObject table) =>
        table["columns"] as JsonArray ?? (JsonArray)(table["columns"] = new JsonArray());

    private static JsonObject Column(JsonArray tables, JsonElement operation, string path)
    {
        var table = Table(tables, operation, path);
        var name = Text(operation, "column", path);
        return Find(Columns(table), name)
            ?? throw Invalid($"{path}.column", $"The table '{table["name"]}' has no column '{name}'.");
    }

    private static JsonObject Relation(JsonArray relations, JsonElement operation, string path)
    {
        var name = Text(operation, "relation", path);
        return Find(relations, name) ?? throw Invalid($"{path}.relation", $"There is no relation '{name}'.");
    }

    /// <summary>Sets the given keys; null removes one. Some keys have their own operation.</summary>
    private static void Merge(JsonObject target, JsonObject set, string path, params string[] reserved)
    {
        foreach (var (key, value) in set.ToList())
        {
            if (reserved.Contains(key, StringComparer.OrdinalIgnoreCase))
                throw Invalid($"{path}.set.{key}", key == "name"
                    ? "Rename with renameTable, renameColumn or a new relation, not with set."
                    : $"Change {key} with their own operations (addColumn, addIndex, …).");
            set.Remove(key);
            if (value is null)
                target.Remove(key);
            else
                target[key] = value;
        }
    }

    private static JsonObject? Find(JsonArray items, string name) =>
        items.OfType<JsonObject>().FirstOrDefault(item =>
            item["name"] is JsonValue value && value.TryGetValue<string>(out var text) &&
            string.Equals(text, name, StringComparison.OrdinalIgnoreCase));

    private static string Name(JsonObject item, string path) =>
        item["name"] is JsonValue value && value.TryGetValue<string>(out var name) && !string.IsNullOrWhiteSpace(name)
            ? name
            : throw Invalid($"{path}.name", "It needs a name.");

    private static string Text(JsonElement operation, string property, string path) =>
        operation.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : throw Invalid($"{path}.{property}", $"{property} is missing.");

    private static JsonObject Object(JsonElement operation, string property, string path) =>
        operation.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(value.GetRawText())!.AsObject()
            : throw Invalid($"{path}.{property}", $"{property} must be an object.");

    private static ApiValidationException Invalid(string field, string message) =>
        new(message, new Dictionary<string, object?> { ["field"] = field });
}
