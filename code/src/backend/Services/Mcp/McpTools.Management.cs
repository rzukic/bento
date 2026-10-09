using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bento.Api.Services.Mcp;

/// <summary>
/// The rest of Bento over MCP: the guide, the full model, and changing dashboards,
/// saved queries, forms, the schema and functions — each behind its permission.
/// </summary>
public sealed partial class McpTools
{
    /// <summary>Guide topics, embedded from <c>McpGuide/*.md</c> (the explanation files of Part 12 R12.3).</summary>
    public static readonly string[] GuideTopics = ["overview", "schema", "formulas", "queries", "dashboards", "forms", "finance", "functions", "api"];

    private static readonly HashSet<string> CallApiMethods = new(StringComparer.OrdinalIgnoreCase) { "GET", "POST", "PUT", "PATCH", "DELETE" };

    private List<McpTool> BuildManagement() =>
    [
        new("bento_guide", "Read the Bento guide",
            "How Bento works and its exact JSON formats. Topics: overview (start here), schema (Model JSON), formulas, queries (aggregate), dashboards (Dashboard JSON), forms (Form JSON), finance (the Wealth & cash-flow template), functions (JavaScript, price sources), api (endpoints for call_api). Read the topic before building or changing something.",
            Schema(new JsonObject
            {
                ["topic"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray(GuideTopics.Select(t => (JsonNode)t).ToArray()),
                    ["description"] = "The topic; leave out for the overview."
                }
            }),
            ReadOnly: true, Idempotent: true, (_, args, _) => Task.FromResult(Guide(Str(args, "topic") ?? "overview"))),

        new("get_model", "Get the full schema (Model JSON)",
            "The complete Model JSON of this Bento — every table, column and relation with all options. Start from it when changing the schema: change it and send the whole document to preview_schema_change.",
            Schema(new JsonObject()),
            ReadOnly: true, Idempotent: true, (caller, _, ct) => PassAsync(caller, HttpMethod.Get, $"{Api}/admin/schema/model", null, ct)),

        new("check_formula", "Check a formula",
            "Checks a formula against a table: its result type, the columns it uses, errors with positions. With preview: true also computes it on the first rows. Use it before saving a computed column or a widget formula.",
            Schema(new JsonObject
            {
                ["table"] = Prop("string", "The table the formula belongs to."),
                ["formula"] = Prop("string", "The formula, e.g. \"IF(amount < 0, -amount, 0)\"."),
                ["resultType"] = Prop("string", "Optional: the type it must give (text, integer, float, currency, boolean, date, datetime)."),
                ["preview"] = Prop("boolean", "Also compute it on the first rows."),
                ["limit"] = Prop("integer", "Preview rows, 1–100 (default 20).")
            }, "table", "formula"),
            ReadOnly: true, Idempotent: true, CheckFormulaAsync),

        new("list_functions", "List JavaScript functions",
            "The user's own JavaScript functions: name, arguments, result type, mode, where they are used.",
            Schema(new JsonObject()),
            ReadOnly: true, Idempotent: true, (caller, _, ct) => PassAsync(caller, HttpMethod.Get, $"{Api}/functions", null, ct)),

        new("get_function", "Get a function",
            "One function with its code and its uses.",
            Schema(new JsonObject { ["name"] = Prop("string", "The function's name.") }, "name"),
            ReadOnly: true, Idempotent: true, (caller, args, ct) => PassAsync(caller, HttpMethod.Get, $"{Api}/functions/{Uri.EscapeDataString(Required(args, "name"))}", null, ct)),

        new("list_price_sources", "List price sources",
            "The price sources: their function, target table, securities with the latest price, and status.",
            Schema(new JsonObject()),
            ReadOnly: true, Idempotent: true, (caller, _, ct) => PassAsync(caller, HttpMethod.Get, $"{Api}/price-sources", null, ct)),

        new("call_api", "Call the Bento API",
            "Any other Bento REST call, with this connection's rights (bento_guide topic api lists the endpoints). Use the dedicated tools where one exists.",
            Schema(new JsonObject
            {
                ["method"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("GET", "POST", "PUT", "PATCH", "DELETE") },
                ["path"] = Prop("string", "Starting with /api/v1/, with the query string if any, e.g. /api/v1/functions/templates."),
                ["body"] = new JsonObject { ["type"] = "object", ["description"] = "JSON body for POST, PUT and PATCH.", ["additionalProperties"] = true }
            }, "method", "path"),
            ReadOnly: false, Idempotent: false, CallApiAsync),

        new("delete_row", "Delete a row",
            "Deletes one row. Ask the user first.",
            Schema(new JsonObject
            {
                ["table"] = Prop("string", "Table name."),
                ["id"] = Prop("string", "The row's id.")
            }, "table", "id"),
            ReadOnly: false, Idempotent: true,
            (caller, args, ct) => PassAsync(caller, HttpMethod.Delete, $"{Api}/data/{Uri.EscapeDataString(Required(args, "table"))}/{Uri.EscapeDataString(Required(args, "id"))}", null, ct),
            Permission: McpPermissions.DeleteRows, Destructive: true),

        new("create_dashboard", "Create a dashboard",
            "Creates a dashboard from a Dashboard JSON document (bento_guide topic dashboards).",
            Schema(new JsonObject
            {
                ["name"] = Prop("string", "The dashboard's name."),
                ["dashboard"] = new JsonObject { ["type"] = "object", ["description"] = "Dashboard JSON: { widgets: [...], dateRange?, variables?, filters?, annotations?, quickAdd? }.", ["additionalProperties"] = true },
                ["isShared"] = Prop("boolean", "Visible to everyone (default false).")
            }, "name", "dashboard"),
            ReadOnly: false, Idempotent: false,
            (caller, args, ct) => PassAsync(caller, HttpMethod.Post, $"{Api}/dashboards", Pick(args, "name", "dashboard", "isShared"), ct),
            Permission: McpPermissions.Dashboards),

        new("update_dashboard", "Change a dashboard",
            "Changes a dashboard's name, sharing or document. The document replaces the old one: read it with get_dashboard, change it, send it whole.",
            Schema(new JsonObject
            {
                ["id"] = Prop("integer", "The dashboard's id."),
                ["name"] = Prop("string", "New name."),
                ["dashboard"] = new JsonObject { ["type"] = "object", ["additionalProperties"] = true },
                ["isShared"] = Prop("boolean", "Visible to everyone.")
            }, "id"),
            ReadOnly: false, Idempotent: true,
            (caller, args, ct) => PassAsync(caller, HttpMethod.Put, $"{Api}/dashboards/{Int(args, "id")}", Pick(args, "name", "dashboard", "isShared"), ct),
            Permission: McpPermissions.Dashboards),

        new("delete_dashboard", "Delete a dashboard",
            "Deletes a dashboard. Ask the user first.",
            Schema(new JsonObject { ["id"] = Prop("integer", "The dashboard's id.") }, "id"),
            ReadOnly: false, Idempotent: true,
            (caller, args, ct) => PassAsync(caller, HttpMethod.Delete, $"{Api}/dashboards/{Int(args, "id")}", null, ct),
            Permission: McpPermissions.Dashboards, Destructive: true),

        new("save_query", "Save a query",
            "Saves an aggregation query (bento_guide topic queries) to run again or use in dashboard widgets (savedQueryId). With id: changes that saved query.",
            Schema(new JsonObject
            {
                ["id"] = Prop("integer", "Change this saved query instead of creating one."),
                ["name"] = Prop("string", "Its name."),
                ["query"] = new JsonObject { ["type"] = "object", ["additionalProperties"] = true },
                ["isShared"] = Prop("boolean", "Visible to everyone.")
            }, "name", "query"),
            ReadOnly: false, Idempotent: false, SaveQueryAsync,
            Permission: McpPermissions.Dashboards),

        new("delete_saved_query", "Delete a saved query",
            "Deletes a saved query. Widgets using it stop working; ask the user first.",
            Schema(new JsonObject { ["id"] = Prop("integer", "The saved query's id.") }, "id"),
            ReadOnly: false, Idempotent: true,
            (caller, args, ct) => PassAsync(caller, HttpMethod.Delete, $"{Api}/queries/{Int(args, "id")}", null, ct),
            Permission: McpPermissions.Dashboards, Destructive: true),

        new("create_form", "Create a form",
            "Creates an entry form from Form JSON (bento_guide topic forms).",
            Schema(new JsonObject
            {
                ["name"] = Prop("string", "The form's name, e.g. \"Add transaction\"."),
                ["form"] = new JsonObject { ["type"] = "object", ["description"] = "Form JSON: { table, fields: [...], pinned? }.", ["additionalProperties"] = true },
                ["isShared"] = Prop("boolean", "Visible to everyone (default false).")
            }, "name", "form"),
            ReadOnly: false, Idempotent: false,
            (caller, args, ct) => PassAsync(caller, HttpMethod.Post, $"{Api}/forms", Pick(args, "name", "form", "isShared"), ct),
            Permission: McpPermissions.Forms),

        new("update_form", "Change a form",
            "Changes a form's name, sharing or definition (the definition replaces the old one).",
            Schema(new JsonObject
            {
                ["id"] = Prop("integer", "The form's id."),
                ["name"] = Prop("string", "New name."),
                ["form"] = new JsonObject { ["type"] = "object", ["additionalProperties"] = true },
                ["isShared"] = Prop("boolean", "Visible to everyone.")
            }, "id"),
            ReadOnly: false, Idempotent: true,
            (caller, args, ct) => PassAsync(caller, HttpMethod.Put, $"{Api}/forms/{Int(args, "id")}", Pick(args, "name", "form", "isShared"), ct),
            Permission: McpPermissions.Forms),

        new("delete_form", "Delete a form",
            "Deletes a form. Ask the user first.",
            Schema(new JsonObject { ["id"] = Prop("integer", "The form's id.") }, "id"),
            ReadOnly: false, Idempotent: true,
            (caller, args, ct) => PassAsync(caller, HttpMethod.Delete, $"{Api}/forms/{Int(args, "id")}", null, ct),
            Permission: McpPermissions.Forms, Destructive: true),

        new("preview_schema_change", "Preview a schema change",
            "What applying this Model JSON would change: tables, columns and relations added, removed, renamed or converted, destructive steps and validation errors. Nothing changes. Send the whole model (get_model, then edit).",
            Schema(new JsonObject { ["model"] = new JsonObject { ["type"] = "object", ["description"] = "The complete Model JSON.", ["additionalProperties"] = true } }, "model"),
            ReadOnly: true, Idempotent: true,
            (caller, args, ct) => PassAsync(caller, HttpMethod.Post, $"{Api}/admin/schema/preview", Object(args, "model"), ct),
            Permission: McpPermissions.Schema),

        new("apply_schema_change", "Apply a schema change",
            "Applies a Model JSON: migrates the database. Preview first and get the user's go-ahead; destructive changes (dropped tables or columns, lossy conversions) also need confirm_destructive: true.",
            Schema(new JsonObject
            {
                ["model"] = new JsonObject { ["type"] = "object", ["description"] = "The complete Model JSON.", ["additionalProperties"] = true },
                ["confirm_destructive"] = Prop("boolean", "Confirm changes that delete data.")
            }, "model"),
            ReadOnly: false, Idempotent: false, ApplySchemaAsync,
            Permission: McpPermissions.Schema, Destructive: true),

        new("save_function", "Create or change a function",
            "Creates a JavaScript function, or saves a new version of an existing one (bento_guide topic functions). Test it with test_function first.",
            Schema(new JsonObject
            {
                ["name"] = Prop("string", "Small letters, digits and _; cannot change later."),
                ["description"] = Prop("string", "What it does."),
                ["arguments"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "object" }, ["description"] = "[{ name, type }] with type float, integer, currency, text, boolean, date, datetime or any." },
                ["resultType"] = Prop("string", "float, integer, currency, text, boolean, date, datetime or any."),
                ["code"] = Prop("string", "The body of a JavaScript function whose parameters are the arguments."),
                ["mode"] = Prop("string", "row (default) or rows (CUSTOM_ROWS: lists in, a list out)."),
                ["allowedHosts"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
                ["credentials"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } }
            }, "name", "resultType", "code"),
            ReadOnly: false, Idempotent: false, SaveFunctionAsync,
            Permission: McpPermissions.Functions),

        new("test_function", "Test a function",
            "Runs code in the sandbox with test values, without saving: the result, console.log output and the time it took.",
            Schema(new JsonObject
            {
                ["arguments"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "object" } },
                ["resultType"] = Prop("string", "The type it must return."),
                ["code"] = Prop("string", "The function body."),
                ["mode"] = Prop("string", "row or rows."),
                ["values"] = new JsonObject { ["type"] = "array", ["description"] = "One value per argument (in rows mode a list per argument)." },
                ["allowedHosts"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
                ["credentials"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } }
            }, "resultType", "code"),
            ReadOnly: true, Idempotent: true,
            (caller, args, ct) => PassAsync(caller, HttpMethod.Post, $"{Api}/functions/test", args, ct),
            Permission: McpPermissions.Functions),

        new("delete_function", "Delete a function",
            "Deletes a function. Refused while a computed column uses it; ask the user first.",
            Schema(new JsonObject { ["name"] = Prop("string", "The function's name.") }, "name"),
            ReadOnly: false, Idempotent: true,
            (caller, args, ct) => PassAsync(caller, HttpMethod.Delete, $"{Api}/functions/{Uri.EscapeDataString(Required(args, "name"))}", null, ct),
            Permission: McpPermissions.Functions, Destructive: true),

        new("run_price_source", "Fetch prices now",
            "Runs a price source now: latest (today's prices), history (back to its start date) or daily (closing prices).",
            Schema(new JsonObject
            {
                ["id"] = Prop("integer", "The price source's id."),
                ["kind"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("latest", "history", "daily") }
            }, "id"),
            ReadOnly: false, Idempotent: true,
            (caller, args, ct) => PassAsync(caller, HttpMethod.Post, $"{Api}/price-sources/{Int(args, "id")}/run?kind={Uri.EscapeDataString(Str(args, "kind") ?? "latest")}", null, ct),
            Permission: McpPermissions.Functions)
    ];

    // --- Tools ---------------------------------------------------------------------

    public static McpToolResult Guide(string topic)
    {
        var name = topic.Trim().ToLowerInvariant();
        if (!GuideTopics.Contains(name))
            return new McpToolResult($"There is no topic '{topic}'. Topics: {string.Join(", ", GuideTopics)}.", true);
        using var stream = typeof(McpTools).Assembly.GetManifestResourceStream($"Bento.Api.McpGuide.{name}.md")
            ?? throw new InvalidOperationException($"Guide topic {name} is missing from the build.");
        using var reader = new StreamReader(stream);
        return new McpToolResult(reader.ReadToEnd(), false);
    }

    private Task<McpToolResult> CheckFormulaAsync(McpCaller caller, JsonElement args, CancellationToken ct)
    {
        var preview = args.TryGetProperty("preview", out var p) && p.ValueKind == JsonValueKind.True;
        var body = new JsonObject
        {
            ["table"] = Required(args, "table"),
            ["formula"] = Required(args, "formula")
        };
        if (Str(args, "resultType") is { } resultType) body["resultType"] = resultType;
        if (preview) body["limit"] = Math.Clamp(OptionalInt(args, "limit") ?? 20, 1, 100);
        return PassAsync(caller, HttpMethod.Post, $"{Api}/formulas/{(preview ? "preview" : "validate")}", JsonDocument.Parse(body.ToJsonString()).RootElement, ct);
    }

    private Task<McpToolResult> CallApiAsync(McpCaller caller, JsonElement args, CancellationToken ct)
    {
        var method = Required(args, "method").ToUpperInvariant();
        var path = Required(args, "path");
        if (!CallApiMethods.Contains(method))
            throw new McpArgumentException("method must be GET, POST, PUT, PATCH or DELETE.");
        if (!path.StartsWith(Api + "/", StringComparison.Ordinal) || path.Contains("..", StringComparison.Ordinal) || path.Contains("://", StringComparison.Ordinal))
            throw new McpArgumentException("path must start with /api/v1/.");
        object? body = args.TryGetProperty("body", out var b) && b.ValueKind is JsonValueKind.Object or JsonValueKind.Array ? b : null;
        return PassAsync(caller, new HttpMethod(method), path, method is "POST" or "PUT" or "PATCH" ? body : null, ct);
    }

    private Task<McpToolResult> SaveQueryAsync(McpCaller caller, JsonElement args, CancellationToken ct)
    {
        var body = Pick(args, "name", "query", "isShared");
        return OptionalInt(args, "id") is { } id
            ? PassAsync(caller, HttpMethod.Put, $"{Api}/queries/{id}", body, ct)
            : PassAsync(caller, HttpMethod.Post, $"{Api}/queries", body, ct);
    }

    private Task<McpToolResult> ApplySchemaAsync(McpCaller caller, JsonElement args, CancellationToken ct)
    {
        var confirm = args.TryGetProperty("confirm_destructive", out var c) && c.ValueKind == JsonValueKind.True;
        return PassAsync(caller, HttpMethod.Put, $"{Api}/admin/schema{(confirm ? "?confirm_destructive=true" : "")}", Object(args, "model"), ct);
    }

    /// <summary>A new function is created; an existing one gets a new version.</summary>
    private async Task<McpToolResult> SaveFunctionAsync(McpCaller caller, JsonElement args, CancellationToken ct)
    {
        var name = Required(args, "name");
        var existing = await _api.SendAsync(HttpMethod.Get, $"{Api}/functions/{Uri.EscapeDataString(name)}", caller.Credentials, null, ct);
        var body = Pick(args, "name", "description", "arguments", "resultType", "code", "mode", "allowedHosts", "credentials");
        return existing.IsSuccess
            ? await PassAsync(caller, HttpMethod.Put, $"{Api}/functions/{Uri.EscapeDataString(name)}", body, ct)
            : await PassAsync(caller, HttpMethod.Post, $"{Api}/functions", body, ct);
    }

    /// <summary>The named arguments that were given, as a JSON body.</summary>
    private static JsonElement Pick(JsonElement args, params string[] names)
    {
        var body = new JsonObject();
        if (args.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in names)
            {
                if (args.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null)
                    body[name] = JsonNode.Parse(value.GetRawText());
            }
        }
        return JsonDocument.Parse(body.ToJsonString()).RootElement.Clone();
    }
}
