using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bento.Api.Controllers;
using Bento.Api.Services.Mcp;

namespace Bento.Api.Services.Ai;

/// <summary>
/// "Describe instead of click" (Step 4.9): a sentence becomes a widget in the
/// Lens editor or a formula in a formula field. The AI looks at the schema and
/// tries its query or formula with read-only tools, then hands in the result
/// through a submit tool that Bento checks — a widget's query must run, a
/// formula must validate — so what reaches the editor works. Nothing is saved:
/// the user sees it live and saves (or not) as usual.
/// </summary>
public sealed class DescribeService
{
    public static readonly IReadOnlyList<string> ToolNames =
        ["describe_schema", "list_rows", "aggregate", "list_formula_functions", "check_formula", "list_saved_queries", "get_dashboard", "bento_guide"];

    private const string Api = "/" + ApiRoutes.Prefix;
    private const int MaxContextLength = 40_000;

    private readonly AiAgent _agent;
    private readonly BentoSelfClient _self;

    public DescribeService(AiAgent agent, BentoSelfClient self)
    {
        _agent = agent;
        _self = self;
    }

    public sealed record DescribeResponse(JsonElement? Widget, string? Formula, string? Note, string Model, decimal CostUsd, int ToolCalls, int DurationMs);

    // ── Widgets ──────────────────────────────────────────────────────────

    public sealed class WidgetRequest
    {
        public string? Language { get; init; }
        public string? Sentence { get; init; }
        public string? Dashboard { get; init; }
        /// <summary>The widget as it is in the editor now (id and position are kept).</summary>
        public JsonElement? Widget { get; init; }
        /// <summary>The dashboard's date range, { from, to } with <c>to</c> exclusive.</summary>
        public DateRange? Range { get; init; }
        /// <summary>The dashboard's variables with their current values.</summary>
        public JsonElement? Variables { get; init; }
        /// <summary>The titles of the other widgets, for context.</summary>
        public IReadOnlyList<string>? OtherWidgets { get; init; }
    }

    public sealed record DateRange(string? From, string? To);

    private static readonly string WidgetPrompt = string.Join("\n\n",
        """
        You build one widget of a Bento dashboard from the user's sentence, in the Lens editor. The user sees your widget live and saves it themselves.

        You get the widget as it is now (keep its id and position; change everything else as the sentence asks — it may be a new, empty widget or a change to an existing one), the dashboard's date range and variables, and the sentence.

        Work like this:
        1. Look at the schema (describe_schema, one table in full when you need its columns) — never guess table or column names. Look up the values you filter on (e.g. the category "Rent" and its id) with list_rows or aggregate.
        2. Write the widget in Dashboard JSON (reference below). Prefer what the editor makes: a "query" with groupBy/measures; "dateColumn" for the date the dashboard's range filters; a date groupBy with "bucket" for time series; "top" on a groupBy for "the biggest five"; filters for "without …".
        3. Try the query with aggregate when unsure.
        4. Call submit_widget with the complete widget. Bento runs its query for the dashboard's range; if that fails or returns nothing you get the reason — fix it and submit again.

        Choose the type that fits the sentence: kpi for one number, line/area for a development over time, bar for comparisons or periods, donut for shares of a whole, table for lists, sankey for flows. Give the widget a short title in the user's language. Never invent data. If the sentence cannot be done with the data, do not submit; answer in one or two sentences why, in the user's language.
        """,
        McpTools.Instructions,
        "Reference — Dashboard JSON:\n" + McpTools.Guide("dashboards").Text,
        "Reference — queries:\n" + McpTools.Guide("queries").Text,
        "Reference — the finance template's conventions (when the tables look like it):\n" + McpTools.Guide("finance").Text);

    public async Task<DescribeResponse> DescribeWidgetAsync(McpCaller caller, WidgetRequest request, CancellationToken ct)
    {
        var sentence = RequireSentence(request.Sentence);
        var current = request.Widget is { ValueKind: JsonValueKind.Object } w ? JsonNode.Parse(w.GetRawText())!.AsObject() : new JsonObject();
        var id = current["id"]?.GetValue<string>() ?? "w" + Guid.NewGuid().ToString("N")[..8];
        var position = current["position"]?.DeepClone() ?? new JsonObject { ["x"] = 0, ["y"] = 0, ["w"] = 6, ["h"] = 4 };
        var variables = request.Variables is { ValueKind: JsonValueKind.Array } v ? v : JsonDocument.Parse("[]").RootElement;
        var confirmedEmpty = false;

        var context = new JsonObject
        {
            ["language"] = Language(request.Language),
            ["today"] = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["dashboard"] = request.Dashboard,
            ["range"] = request.Range is null ? null : new JsonObject { ["from"] = request.Range.From, ["to (exclusive)"] = request.Range.To },
            ["variables"] = JsonNode.Parse(variables.GetRawText()),
            ["otherWidgets"] = request.OtherWidgets is null ? null : new JsonArray(request.OtherWidgets.Take(30).Select(t => (JsonNode)JsonValue.Create(t)!).ToArray()),
            ["widget"] = current.DeepClone(),
            ["sentence"] = sentence
        };

        var submit = new AiAgent.AiLocalTool(
            "submit_widget",
            "Hand in the finished widget. Bento runs its query for the dashboard's range: you get the problem back to fix, or the widget goes to the user's editor.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["widget"] = new JsonObject { ["type"] = "object", ["description"] = "The complete widget: type, title, data, display (id and position are kept)." },
                    ["note"] = new JsonObject { ["type"] = "string", ["description"] = "One short sentence for the user in their language: what the widget shows and any assumption you made." },
                    ["confirm_empty"] = new JsonObject { ["type"] = "boolean", ["description"] = "true when you checked that no rows in the range is right." }
                },
                ["required"] = new JsonArray("widget")
            },
            async (input, token) =>
            {
                if (!input.TryGetProperty("widget", out var submitted) || submitted.ValueKind != JsonValueKind.Object)
                    return new AiAgent.AiLocalResult("widget must be an object.", true);
                var widget = JsonNode.Parse(submitted.GetRawText())!.AsObject();
                widget["id"] = id;
                widget["position"] = position.DeepClone();
                if (input.TryGetProperty("confirm_empty", out var ce) && ce.ValueKind == JsonValueKind.True)
                    confirmedEmpty = true;

                var problem = await CheckWidgetAsync(caller, widget, request.Range, variables, confirmedEmpty, token);
                if (problem is not null)
                    return new AiAgent.AiLocalResult(problem, true);
                var note = input.TryGetProperty("note", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                var final = new JsonObject { ["widget"] = widget, ["note"] = note };
                return new AiAgent.AiLocalResult("Accepted: the widget is in the user's editor.", false, JsonDocument.Parse(final.ToJsonString()).RootElement.Clone());
            });

        var result = await _agent.RunAsync("describe_widget", caller, WidgetPrompt, Cap(context.ToJsonString()), ToolNames, ct, [submit]);
        if (result.Final is { } final)
            return new DescribeResponse(final.GetProperty("widget").Clone(), null, Text(final, "note") ?? NullIfEmpty(result.Text), result.Model, result.CostUsd, result.ToolCalls, result.DurationMs);
        // No widget: the model explains why (e.g. there is no such data).
        return new DescribeResponse(null, null, result.Text, result.Model, result.CostUsd, result.ToolCalls, result.DurationMs);
    }

    /// <summary>Null when the widget is fine; otherwise what to fix, for the model.</summary>
    private async Task<string?> CheckWidgetAsync(McpCaller caller, JsonObject widget, DateRange? range, JsonElement variables, bool confirmedEmpty, CancellationToken ct)
    {
        var type = widget["type"]?.GetValue<string>();
        if (type is null || !DashboardsController.WidgetTypes.Contains(type))
            return $"type must be one of: {string.Join(", ", DashboardsController.WidgetTypes)}.";
        if (string.IsNullOrWhiteSpace(widget["title"]?.GetValue<string>()))
            return "Give the widget a title.";
        if (type == "text")
            return widget["display"]?["text"] is null ? "A text widget needs display.text." : null;

        var data = widget["data"] as JsonObject;
        if (data is null)
            return "The widget needs data: a query, a savedQueryId or a variable.";
        if (data["variable"]?.GetValue<string>() is { } variable)
        {
            var known = variables.EnumerateArray().Any(v => v.TryGetProperty("name", out var name) && name.GetString() == variable);
            return known ? null : $"The dashboard has no variable '{variable}'.";
        }

        BentoSelfClient.ApiResponse response;
        if (data["savedQueryId"] is JsonValue savedId)
        {
            response = await _self.SendAsync(HttpMethod.Post, $"{Api}/queries/{savedId.GetValue<long>()}/run", caller.Credentials, new { }, ct);
        }
        else if (data["query"] is JsonObject query)
        {
            if (type == "target")
                return null;
            var runnable = RunnableQuery(query, data, range, variables);
            response = await _self.SendAsync(HttpMethod.Post, $"{Api}/query", caller.Credentials, JsonDocument.Parse(runnable.ToJsonString()).RootElement, ct);
        }
        else
        {
            return type == "target" ? null : "data needs a query (or savedQueryId, or variable).";
        }

        if (!response.IsSuccess)
            return $"The query failed ({response.Status}): {Short(response.RawBody)}";
        var rows = response.Body is { } body && body.TryGetProperty("data", out var list) && list.ValueKind == JsonValueKind.Array ? list.GetArrayLength() : 0;
        if (rows == 0 && !confirmedEmpty)
            return "The query ran but returned no rows for the dashboard's range. Check the filters and values (look them up); if no rows is right, submit again with confirm_empty: true.";
        return null;
    }

    /// <summary>
    /// The query as the dashboard runs it (mirrors the editor's runnableQuery): the
    /// range as a filter on the date column — or, for values at each period end,
    /// as the calendar of periods — the variables for formulas, no chart options.
    /// </summary>
    internal static JsonObject RunnableQuery(JsonObject query, JsonObject data, DateRange? range, JsonElement variables)
    {
        var runnable = (JsonObject)query.DeepClone();
        if (runnable["measures"] is JsonArray measures)
            foreach (var measure in measures.OfType<JsonObject>())
                foreach (var key in new[] { "show", "window", "color", "axis", "format" })
                    measure.Remove(key);

        var groupBy = runnable["groupBy"] as JsonArray;
        var dateGroup = groupBy?.OfType<JsonObject>().FirstOrDefault(g => g["bucket"] is not null);
        var asOf = data["asOf"] is JsonValue a && a.GetValueKind() == JsonValueKind.True;

        if (asOf && dateGroup is not null)
        {
            groupBy!.Remove(dateGroup);
            var today = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var to = range?.To is { } end && DateTime.TryParse(end, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
                ? parsed.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : today;
            if (string.CompareOrdinal(to, today) > 0) to = today;
            var from = range?.From ?? $"{int.Parse(to[..4], CultureInfo.InvariantCulture) - 1}{to[4..8]}01";
            runnable["asOf"] = new JsonObject
            {
                ["bucket"] = dateGroup["bucket"]!.DeepClone(),
                ["from"] = string.CompareOrdinal(from, to) <= 0 ? from : to,
                ["to"] = to,
                ["as"] = (dateGroup["as"] ?? dateGroup["column"])!.DeepClone()
            };
        }
        else
        {
            var dateColumn = data.ContainsKey("dateColumn") ? data["dateColumn"]?.GetValue<string>() : dateGroup?["column"]?.GetValue<string>();
            if (dateColumn is not null && range is not null && (range.From is not null || range.To is not null))
            {
                var filter = runnable["filter"] as JsonObject ?? new JsonObject();
                var condition = filter[dateColumn] as JsonObject ?? (filter[dateColumn] is null ? new JsonObject() : new JsonObject { ["eq"] = filter[dateColumn]!.DeepClone() });
                if (range.From is not null && condition["gte"] is null) condition["gte"] = range.From;
                if (range.To is not null && condition["lt"] is null) condition["lt"] = range.To;
                filter[dateColumn] = condition;
                runnable["filter"] = filter;
            }
        }

        var hasFormula = (runnable["measures"] as JsonArray)?.OfType<JsonObject>().Any(m => m["formula"] is not null) ?? false;
        if (hasFormula && runnable["variables"] is null && variables.ValueKind == JsonValueKind.Array)
        {
            var values = new JsonObject();
            foreach (var variable in variables.EnumerateArray())
                if (variable.TryGetProperty("name", out var name) && name.GetString() is { } key && variable.TryGetProperty("value", out var value) && value.ValueKind is JsonValueKind.Number or JsonValueKind.String)
                    values[key] = JsonNode.Parse(value.GetRawText());
            if (values.Count > 0) runnable["variables"] = values;
        }
        return runnable;
    }

    // ── Formulas ─────────────────────────────────────────────────────────

    public sealed class FormulaRequest
    {
        public string? Language { get; init; }
        public string? Sentence { get; init; }
        public string? Table { get; init; }
        /// <summary>"column" (a computed column: one value per row) or "measure" (a widget measure over the rows of each group).</summary>
        public string? Kind { get; init; }
        public string? Current { get; init; }
        /// <summary>Columns: the type it must give, when the column has one.</summary>
        public string? ResultType { get; init; }
    }

    private static readonly string FormulaPrompt = string.Join("\n\n",
        """
        You write one Bento formula from the user's sentence. It goes into a formula field the user is editing; they see it checked and previewed and save it themselves.

        A "column" formula is a computed column: one value per row of the table, from that row's columns and its links (category_id.name, SUM(transactions.amount) over linked rows, LOOKUP…). A "measure" formula is a widget measure: one value over the rows of each group, with aggregations such as SUM(amount WHERE amount < 0) or AVG(price).

        Look at the table's columns (describe_schema with the table) and the functions you need (list_formula_functions) — never guess names. Try it with check_formula (columns) or aggregate with a formula measure (measures). Then call submit_formula; Bento checks it and you get the problem back to fix, or it goes to the user's field. Keep it as simple as the sentence allows. If it cannot be done with the data, do not submit; answer in one or two sentences why, in the user's language.
        """,
        McpTools.Instructions,
        "Reference — formulas:\n" + McpTools.Guide("formulas").Text);

    public async Task<DescribeResponse> DescribeFormulaAsync(McpCaller caller, FormulaRequest request, CancellationToken ct)
    {
        var sentence = RequireSentence(request.Sentence);
        var table = string.IsNullOrWhiteSpace(request.Table)
            ? throw new Exceptions.ApiValidationException("Which table is the formula for?", new Dictionary<string, object?> { ["field"] = "table" })
            : request.Table.Trim();
        var kind = request.Kind == "measure" ? "measure" : "column";

        var context = new JsonObject
        {
            ["language"] = Language(request.Language),
            ["table"] = table,
            ["kind"] = kind,
            ["resultType"] = request.ResultType,
            ["current"] = request.Current,
            ["sentence"] = sentence
        };

        var submit = new AiAgent.AiLocalTool(
            "submit_formula",
            "Hand in the finished formula. Bento checks it: you get the problem back to fix, or it goes to the user's field.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["formula"] = new JsonObject { ["type"] = "string" },
                    ["note"] = new JsonObject { ["type"] = "string", ["description"] = "One short sentence for the user in their language: what it computes." }
                },
                ["required"] = new JsonArray("formula")
            },
            async (input, token) =>
            {
                var formula = input.TryGetProperty("formula", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString()?.Trim() : null;
                if (string.IsNullOrEmpty(formula))
                    return new AiAgent.AiLocalResult("formula must be a non-empty string.", true);
                var problem = kind == "column"
                    ? await CheckColumnFormulaAsync(caller, table, formula, request.ResultType, token)
                    : await CheckMeasureFormulaAsync(caller, table, formula, token);
                if (problem is not null)
                    return new AiAgent.AiLocalResult(problem, true);
                var final = new JsonObject { ["formula"] = formula, ["note"] = Text(input, "note") };
                return new AiAgent.AiLocalResult("Accepted: the formula is in the user's field.", false, JsonDocument.Parse(final.ToJsonString()).RootElement.Clone());
            });

        var result = await _agent.RunAsync("describe_formula", caller, FormulaPrompt, context.ToJsonString(), ToolNames, ct, [submit]);
        if (result.Final is { } final)
            return new DescribeResponse(null, Text(final, "formula"), Text(final, "note") ?? NullIfEmpty(result.Text), result.Model, result.CostUsd, result.ToolCalls, result.DurationMs);
        return new DescribeResponse(null, null, result.Text, result.Model, result.CostUsd, result.ToolCalls, result.DurationMs);
    }

    private async Task<string?> CheckColumnFormulaAsync(McpCaller caller, string table, string formula, string? resultType, CancellationToken ct)
    {
        var body = new JsonObject { ["table"] = table, ["formula"] = formula };
        if (!string.IsNullOrWhiteSpace(resultType)) body["resultType"] = resultType;
        var response = await _self.SendAsync(HttpMethod.Post, $"{Api}/formulas/validate", caller.Credentials, JsonDocument.Parse(body.ToJsonString()).RootElement, ct);
        if (!response.IsSuccess)
            return $"The check failed ({response.Status}): {Short(response.RawBody)}";
        var data = response.Body?.GetProperty("data");
        return data is { } d && d.TryGetProperty("valid", out var valid) && valid.ValueKind == JsonValueKind.False
            ? "The formula is not valid: " + Short(d.GetProperty("errors").GetRawText())
            : null;
    }

    private async Task<string?> CheckMeasureFormulaAsync(McpCaller caller, string table, string formula, CancellationToken ct)
    {
        var query = new JsonObject { ["source"] = table, ["measures"] = new JsonArray(new JsonObject { ["name"] = "value", ["formula"] = formula }) };
        var response = await _self.SendAsync(HttpMethod.Post, $"{Api}/query", caller.Credentials, JsonDocument.Parse(query.ToJsonString()).RootElement, ct);
        return response.IsSuccess ? null : $"The formula failed as a measure ({response.Status}): {Short(response.RawBody)}";
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static string RequireSentence(string? sentence)
    {
        var text = sentence?.Trim();
        if (string.IsNullOrEmpty(text))
            throw new Exceptions.ApiValidationException("Describe what you want.", new Dictionary<string, object?> { ["field"] = "sentence" });
        return text.Length > 1000 ? text[..1000] : text;
    }

    private static string Language(string? language) => string.IsNullOrWhiteSpace(language) ? "en" : language.Trim();

    private static string Cap(string text) => text.Length > MaxContextLength ? text[..MaxContextLength] : text;

    private static string Short(string text) => text.Length > 1500 ? text[..1500] + "…" : text;

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? NullIfEmpty(value.GetString()) : null;

    private static string? NullIfEmpty(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
