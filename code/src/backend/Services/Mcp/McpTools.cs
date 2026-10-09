using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bento.Api.Services.Mcp;

/// <summary>Who is calling and what they may do (<see cref="McpPermissions"/>); the credentials are passed on to the REST API.</summary>
public sealed record McpCaller(long? UserId, string Via, string? Credential, IReadOnlyCollection<string> Permissions, BentoSelfClient.Credentials Credentials, long? GrantId = null)
{
    public bool Allows(string permission) => Permissions.Contains(permission);
}

public sealed record McpToolResult(string Text, bool IsError);

/// <param name="Permission">What the caller needs to see and use the tool (<see cref="McpPermissions"/>).</param>
/// <param name="Destructive">The tool can delete data (rows, a dashboard, columns).</param>
public sealed record McpTool(
    string Name,
    string Title,
    string Description,
    JsonObject InputSchema,
    bool ReadOnly,
    bool Idempotent,
    Func<McpCaller, JsonElement, CancellationToken, Task<McpToolResult>> Run,
    string Permission = McpPermissions.Read,
    bool Destructive = false);

/// <summary>
/// The tools Bento offers over MCP (Step 4.6). Each one is a call to the REST API
/// as the caller, so the caller's role and, for apps, the permissions ticked on the
/// consent page apply. Reads cover everything; writes cover rows, dashboards, saved
/// queries, forms, the schema and functions, each behind its own permission.
/// </summary>
public sealed partial class McpTools
{
    /// <summary>Tool output beyond this many characters is cut, with a hint to narrow the request.</summary>
    private const int MaxResultLength = 60_000;
    private const string Api = "/api/v1";

    private readonly BentoSelfClient _api;

    public McpTools(BentoSelfClient api)
    {
        _api = api;
        All = [.. Build(), .. BuildManagement()];
    }

    public IReadOnlyList<McpTool> All { get; }

    public McpTool? Find(string name) => All.FirstOrDefault(tool => tool.Name == name);

    public const string Instructions = """
        Bento is the user's own data workspace: tables they designed (for example accounts, transactions, categories, trades, prices), dashboards, forms, formulas and JavaScript functions.
        - bento_guide explains how Bento works and the exact JSON formats (schema, formulas, queries, dashboards, forms, functions, the finance template). Read the matching topic before building or changing something, and when the user asks how Bento works.
        - Start with describe_schema to learn the tables, their columns and links; call it with a table name for the details of one table.
        - For totals, averages and counts over time use aggregate: it filters, groups by columns, linked records or date buckets (month, quarter, year) and computes measures in one call. Use list_rows to look at individual rows.
        - A link column (type reference) stores the id of a row in another table. aggregate returns <column>_label with the linked record's name; list_rows can include the linked record. To filter by a linked record, find its id first (list_rows with search on the linked table).
        - In finance setups money going out is negative (expenses < 0) unless the schema says otherwise. Dates are ISO (YYYY-MM-DD).
        - Before adding a row, look up the ids for its link columns (account, category, …) and use today's date unless the user says otherwise. Confirm amounts and the target table with the user when anything is ambiguous.
        - Changing the schema can delete data: always preview_schema_change first, tell the user what it does, and only apply after they agree. Deleting rows, dashboards or forms also needs the user's go-ahead.
        - You see only the tools this connection may use; if the user wants more, they reconnect and tick it on Bento's sign-in page.
        """;

    private List<McpTool> Build() =>
    [
        new("describe_schema", "Describe the data model",
            "The tables of this Bento with their columns (name, type, required, enum values, links, computed formulas) and relations. Without arguments: an overview of every table. With table: everything about that table.",
            Schema(new JsonObject
            {
                ["table"] = Prop("string", "A table name for its full definition; leave out for the overview.")
            }),
            ReadOnly: true, Idempotent: true, DescribeSchemaAsync),

        new("list_rows", "List rows of a table",
            "Rows of a table, filtered, searched, sorted and paginated (default 25, at most 100 per page). Use it to look at individual records or find ids; for sums and counts use aggregate.",
            Schema(new JsonObject
            {
                ["table"] = Prop("string", "Table name."),
                ["filter"] = new JsonObject
                {
                    ["type"] = "object",
                    ["description"] = "Column filters. A plain value means equals ({\"status\": \"paid\"}); an object picks the operator: {\"amount\": {\"lt\": 0}, \"booked_on\": {\"gte\": \"2026-01-01\", \"lte\": \"2026-03-31\"}}. Operators: eq, neq, gt, gte, lt, lte, like (with % wildcards), in (array), null, notnull (value true).",
                    ["additionalProperties"] = true
                },
                ["search"] = Prop("string", "Case-insensitive text search across text columns and the names of linked records; several words must all match."),
                ["sort"] = Prop("string", "Comma-separated columns; prefix with - for descending, e.g. \"-booked_on,id\"."),
                ["fields"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Only these columns." },
                ["include"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Linked records to embed, by link name (e.g. the reference column without _id, or a relation name)." },
                ["page"] = Prop("integer", "Page number, from 1."),
                ["per_page"] = Prop("integer", "Rows per page, 1–100 (default 25).")
            }, "table"),
            ReadOnly: true, Idempotent: true, ListRowsAsync),

        new("get_row", "Get one row",
            "One row of a table by its id.",
            Schema(new JsonObject
            {
                ["table"] = Prop("string", "Table name."),
                ["id"] = Prop("string", "The row's id.")
            }, "table", "id"),
            ReadOnly: true, Idempotent: true, GetRowAsync),

        new("aggregate", "Aggregate a table",
            """
            Sums, counts and averages over a table without SQL (Bento's query API). Example — spending per category and month in 2026:
            {"source":"transactions","filter":{"booked_on":{"gte":"2026-01-01"},"amount":{"lt":0}},"groupBy":[{"column":"category_id"},{"column":"booked_on","bucket":"month","as":"month"}],"measures":[{"name":"spent","fn":"sum","column":"amount"},{"name":"bookings","fn":"count"}],"sort":["month","spent"]}
            - filter: same operators as list_rows (a plain value means eq).
            - groupBy: columns; dates with bucket day|week|month|quarter|year (value = first day of the bucket); a link column also returns <name>_label.
            - measures: {name, fn, column} with fn count|sum|avg|min|max|count_distinct|median|percentile ("percentile": 90); "running": true for a running total ("carryIn": true starts from the rows before the date range). Or {name, formula} with a formula over the rows of each group, e.g. "SUM(amount WHERE amount > 0) + SUM(amount WHERE amount < 0)" or "RUNNING_SUM(SUM(amount))".
            - top values go on a groupBy entry: {"column":"category_id","top":5} keeps the 5 values ranked first by the first measure ("rank": "asc" for smallest first, e.g. spending; "abs" for the largest of either sign; "other": true adds the rest as one group).
            - asOf: {"bucket":"month","from":"2025-01-01","to":"2026-09-30","as":"month"} gives one row per period; formulas then use BUCKET_START()/BUCKET_END(), e.g. "SUM(amount WHERE booked_on <= BUCKET_END())" for a balance.
            - variables: {"name": value} usable in formulas.
            list_formula_functions lists every formula function.
            """,
            Schema(new JsonObject
            {
                ["source"] = Prop("string", "The table to aggregate."),
                ["filter"] = new JsonObject { ["type"] = "object", ["additionalProperties"] = true },
                ["groupBy"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "object" }, ["description"] = "{column, bucket?, as?, top?, rank?, other?} per group." },
                ["measures"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "object" } },
                ["sort"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Output names; prefix - for descending." },
                ["asOf"] = new JsonObject { ["type"] = "object" },
                ["variables"] = new JsonObject { ["type"] = "object", ["additionalProperties"] = true }
            }, "source"),
            ReadOnly: true, Idempotent: true, AggregateAsync),

        new("list_formula_functions", "List formula functions",
            "Every function usable in aggregate formulas and computed columns, with signature, description and example.",
            Schema(new JsonObject()),
            ReadOnly: true, Idempotent: true, (caller, _, ct) => PassAsync(caller, HttpMethod.Get, $"{Api}/formulas/functions", null, ct)),

        new("list_saved_queries", "List saved queries",
            "The user's saved queries and the shared ones: id, name and the query they run.",
            Schema(new JsonObject()),
            ReadOnly: true, Idempotent: true, (caller, _, ct) => PassAsync(caller, HttpMethod.Get, $"{Api}/queries", null, ct)),

        new("run_saved_query", "Run a saved query",
            "Runs a saved query by id, optionally narrowed by an extra filter (same operators as list_rows).",
            Schema(new JsonObject
            {
                ["id"] = Prop("integer", "The saved query's id."),
                ["filter"] = new JsonObject { ["type"] = "object", ["additionalProperties"] = true }
            }, "id"),
            ReadOnly: true, Idempotent: true, RunSavedQueryAsync),

        new("list_dashboards", "List dashboards",
            "The dashboards the user can open: id, name and description.",
            Schema(new JsonObject()),
            ReadOnly: true, Idempotent: true, ListDashboardsAsync),

        new("get_dashboard", "Get a dashboard",
            "A dashboard's definition: its widgets with the queries, formulas and variables behind them. Run a widget's query with aggregate to get its numbers.",
            Schema(new JsonObject { ["id"] = Prop("integer", "The dashboard's id.") }, "id"),
            ReadOnly: true, Idempotent: true, (caller, args, ct) => PassAsync(caller, HttpMethod.Get, $"{Api}/dashboards/{Int(args, "id")}", null, ct)),

        new("list_forms", "List forms",
            "The entry forms (for example \"Add transaction\"): which table they write to, their fields, defaults and sign conventions. Useful to add a row the way the user does.",
            Schema(new JsonObject()),
            ReadOnly: true, Idempotent: true, (caller, _, ct) => PassAsync(caller, HttpMethod.Get, $"{Api}/forms", null, ct)),

        new("create_row", "Add a row",
            "Adds a row to a table and returns it. values maps column names to values; link columns take the linked row's id; leave out the id and computed columns.",
            Schema(new JsonObject
            {
                ["table"] = Prop("string", "Table name."),
                ["values"] = new JsonObject { ["type"] = "object", ["additionalProperties"] = true, ["description"] = "Column values, e.g. {\"booked_on\": \"2026-10-09\", \"amount\": -12.40, \"category_id\": 3}." }
            }, "table", "values"),
            ReadOnly: false, Idempotent: false, CreateRowAsync, Permission: McpPermissions.WriteRows),

        new("update_row", "Change a row",
            "Changes some columns of one row (the others stay) and returns the row.",
            Schema(new JsonObject
            {
                ["table"] = Prop("string", "Table name."),
                ["id"] = Prop("string", "The row's id."),
                ["values"] = new JsonObject { ["type"] = "object", ["additionalProperties"] = true, ["description"] = "The columns to change." }
            }, "table", "id", "values"),
            ReadOnly: false, Idempotent: true, UpdateRowAsync, Permission: McpPermissions.WriteRows)
    ];

    // --- Tools ---------------------------------------------------------------------

    private async Task<McpToolResult> DescribeSchemaAsync(McpCaller caller, JsonElement args, CancellationToken ct)
    {
        var response = await _api.SendAsync(HttpMethod.Get, $"{Api}/admin/schema/model", caller.Credentials, null, ct);
        if (!response.IsSuccess || response.Body is not { } body)
            return Failure(response);

        var model = JsonNode.Parse(body.GetRawText())!.AsObject();
        var tables = model["tables"] as JsonArray ?? [];
        var relations = model["relations"] as JsonArray ?? [];
        var tableName = Str(args, "table");

        if (tableName is not null)
        {
            var table = tables.OfType<JsonObject>().FirstOrDefault(t => string.Equals((string?)t["name"], tableName, StringComparison.OrdinalIgnoreCase));
            if (table is null)
                return new McpToolResult($"There is no table '{tableName}'. Tables: {string.Join(", ", tables.OfType<JsonObject>().Select(t => (string?)t["name"]))}.", true);
            var name = (string?)table["name"];
            var related = new JsonArray(relations.OfType<JsonObject>()
                .Where(r => string.Equals((string?)r["from"], name, StringComparison.OrdinalIgnoreCase) || string.Equals((string?)r["to"], name, StringComparison.OrdinalIgnoreCase))
                .Select(r => (JsonNode)r.DeepClone()).ToArray());
            return Success(new JsonObject { ["table"] = table.DeepClone(), ["relations"] = related });
        }

        var overview = new JsonArray(tables.OfType<JsonObject>().Select(t =>
        {
            var entry = new JsonObject { ["name"] = (string?)t["name"] };
            if (t["displayName"] is JsonNode display) entry["displayName"] = display.DeepClone();
            if (t["description"] is JsonNode description) entry["description"] = description.DeepClone();
            entry["columns"] = new JsonArray((t["columns"] as JsonArray ?? []).OfType<JsonObject>().Select(c => (JsonNode)DescribeColumn(c)).ToArray());
            return (JsonNode)entry;
        }).ToArray());
        var relationLines = new JsonArray(relations.OfType<JsonObject>()
            .Select(r => (JsonNode)$"{r["name"]}: {r["from"]} → {r["to"]} ({r["type"]}{(r["column"] is JsonNode column ? $", via {column}" : "")})").ToArray());
        return Success(new JsonObject { ["tables"] = overview, ["relations"] = relationLines });
    }

    /// <summary>"amount: currency, required", "category_id: reference → categories", "kind: enum [income|expense]".</summary>
    private static string DescribeColumn(JsonObject column)
    {
        var text = new StringBuilder($"{column["name"]}: {column["type"]}");
        if ((string?)column["type"] == "reference" && column["references"] is JsonNode target) text.Append($" → {target}");
        if (column["values"] is JsonArray values) text.Append($" [{string.Join("|", values.Select(v => v?.ToString()))}]");
        if (column["formula"] is JsonNode formula) text.Append($" = {formula}");
        if (column["primaryKey"] is JsonNode pk && pk.GetValueKind() == JsonValueKind.True) text.Append(", id");
        else if (column["nullable"] is JsonNode nullable && nullable.GetValueKind() == JsonValueKind.False) text.Append(", required");
        if (column["unique"] is JsonNode unique && unique.GetValueKind() == JsonValueKind.True) text.Append(", unique");
        return text.ToString();
    }

    private Task<McpToolResult> ListRowsAsync(McpCaller caller, JsonElement args, CancellationToken ct)
    {
        var table = Required(args, "table");
        var query = new List<string>();
        if (args.TryGetProperty("filter", out var filter) && filter.ValueKind == JsonValueKind.Object)
        {
            foreach (var column in filter.EnumerateObject())
            {
                if (column.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var op in column.Value.EnumerateObject())
                        query.Add($"filter[{Uri.EscapeDataString(column.Name)}][{Uri.EscapeDataString(op.Name)}]={Uri.EscapeDataString(FilterValue(op.Value))}");
                }
                else
                {
                    query.Add($"filter[{Uri.EscapeDataString(column.Name)}]={Uri.EscapeDataString(FilterValue(column.Value))}");
                }
            }
        }
        if (Str(args, "search") is { } search) query.Add("search=" + Uri.EscapeDataString(search));
        if (Str(args, "sort") is { } sort) query.Add("sort=" + Uri.EscapeDataString(sort));
        if (StrList(args, "fields") is { Count: > 0 } fields) query.Add("fields=" + Uri.EscapeDataString(string.Join(",", fields)));
        if (StrList(args, "include") is { Count: > 0 } include) query.Add("include=" + Uri.EscapeDataString(string.Join(",", include)));
        if (OptionalInt(args, "page") is { } page) query.Add($"page={Math.Max(1, page)}");
        query.Add($"per_page={Math.Clamp(OptionalInt(args, "per_page") ?? 25, 1, 100)}");

        var path = $"{Api}/data/{Uri.EscapeDataString(table)}?{string.Join("&", query)}";
        return PassAsync(caller, HttpMethod.Get, path, null, ct);
    }

    private Task<McpToolResult> GetRowAsync(McpCaller caller, JsonElement args, CancellationToken ct) =>
        PassAsync(caller, HttpMethod.Get, $"{Api}/data/{Uri.EscapeDataString(Required(args, "table"))}/{Uri.EscapeDataString(Required(args, "id"))}", null, ct);

    private Task<McpToolResult> AggregateAsync(McpCaller caller, JsonElement args, CancellationToken ct)
    {
        Required(args, "source");
        return PassAsync(caller, HttpMethod.Post, $"{Api}/query", args, ct);
    }

    private Task<McpToolResult> RunSavedQueryAsync(McpCaller caller, JsonElement args, CancellationToken ct)
    {
        var body = args.TryGetProperty("filter", out var filter) && filter.ValueKind == JsonValueKind.Object
            ? (object)new { filter }
            : new { };
        return PassAsync(caller, HttpMethod.Post, $"{Api}/queries/{Int(args, "id")}/run", body, ct);
    }

    private async Task<McpToolResult> ListDashboardsAsync(McpCaller caller, JsonElement args, CancellationToken ct)
    {
        var response = await _api.SendAsync(HttpMethod.Get, $"{Api}/dashboards", caller.Credentials, null, ct);
        if (!response.IsSuccess || response.Body is not { } body)
            return Failure(response);
        // The list carries whole documents; the overview only needs what to pick from.
        var items = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("data", out var data) ? data : body;
        var overview = new JsonArray();
        if (items.ValueKind == JsonValueKind.Array)
        {
            foreach (var dashboard in items.EnumerateArray())
            {
                var entry = new JsonObject();
                foreach (var key in new[] { "id", "name", "description", "isShared", "updatedAt" })
                {
                    if (dashboard.TryGetProperty(key, out var value))
                        entry[key] = JsonNode.Parse(value.GetRawText());
                }
                overview.Add(entry);
            }
        }
        return Success(new JsonObject { ["dashboards"] = overview });
    }

    private Task<McpToolResult> CreateRowAsync(McpCaller caller, JsonElement args, CancellationToken ct)
    {
        var values = Object(args, "values");
        return PassAsync(caller, HttpMethod.Post, $"{Api}/data/{Uri.EscapeDataString(Required(args, "table"))}", values, ct);
    }

    private Task<McpToolResult> UpdateRowAsync(McpCaller caller, JsonElement args, CancellationToken ct)
    {
        var values = Object(args, "values");
        return PassAsync(caller, HttpMethod.Patch, $"{Api}/data/{Uri.EscapeDataString(Required(args, "table"))}/{Uri.EscapeDataString(Required(args, "id"))}", values, ct);
    }

    // --- Helpers -------------------------------------------------------------------

    private async Task<McpToolResult> PassAsync(McpCaller caller, HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var response = await _api.SendAsync(method, path, caller.Credentials, body, ct);
        if (!response.IsSuccess) return Failure(response);
        return response.Body is { } json ? Success(JsonNode.Parse(json.GetRawText())) : new McpToolResult(response.RawBody, false);
    }

    /// <summary>A tool the connection was not allowed: what to tick when connecting again.</summary>
    public static McpToolResult Refusal(string permission) =>
        new($"This connection may not do that (it needs \"{permission}\"). Reconnect to Bento and tick it on the sign-in page, or use an API key or role that allows it.", true);

    private static McpToolResult Success(JsonNode? node)
    {
        var text = node?.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) ?? "null";
        if (text.Length > MaxResultLength)
            text = text[..MaxResultLength] + "\n… (cut: the result is too long; narrow it with filter, fields, per_page or top)";
        return new McpToolResult(text, false);
    }

    /// <summary>The API's error message and details, for the model to correct its call.</summary>
    private static McpToolResult Failure(BentoSelfClient.ApiResponse response)
    {
        if (response.Body is { } body && body.ValueKind == JsonValueKind.Object && body.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            var message = error.TryGetProperty("message", out var m) ? m.GetString() : null;
            var details = error.TryGetProperty("details", out var d) && d.ValueKind == JsonValueKind.Object
                ? new JsonObject(d.EnumerateObject().Where(p => p.Name != "requestId").Select(p => KeyValuePair.Create(p.Name, JsonNode.Parse(p.Value.GetRawText()))))
                : null;
            var text = $"Error {response.Status}: {message}";
            if (details is { Count: > 0 }) text += " " + details.ToJsonString();
            return new McpToolResult(text, true);
        }
        return new McpToolResult($"Error {response.Status}: {(string.IsNullOrWhiteSpace(response.RawBody) ? "no details" : response.RawBody[..Math.Min(500, response.RawBody.Length)])}", true);
    }

    private static string FilterValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Array => string.Join(",", value.EnumerateArray().Select(FilterValue)),
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "",
        _ => value.GetRawText()
    };

    private static JsonObject Schema(JsonObject properties, params string[] required)
    {
        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (required.Length > 0) schema["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray());
        return schema;
    }

    private static JsonObject Prop(string type, string description) => new() { ["type"] = type, ["description"] = description };

    private static string? Str(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Number
            ? (value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText()) is { Length: > 0 } text ? text : null
            : null;

    private static List<string>? StrList(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.String) return value.GetString()!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (value.ValueKind != JsonValueKind.Array) return null;
        return value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList();
    }

    private static string Required(JsonElement args, string name) =>
        Str(args, name) ?? throw new McpArgumentException($"'{name}' is required.");

    private static int? OptionalInt(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)) return number;
        return null;
    }

    private static long Int(JsonElement args, string name)
    {
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value))
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
            if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)) return number;
        }
        throw new McpArgumentException($"'{name}' must be a number.");
    }

    private static JsonElement Object(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : throw new McpArgumentException($"'{name}' must be an object of column values.");
}

/// <summary>Arguments that do not fit the tool's input schema.</summary>
public sealed class McpArgumentException(string message) : Exception(message);
