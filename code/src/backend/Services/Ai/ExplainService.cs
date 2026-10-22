using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bento.Api.Exceptions;
using Bento.Api.Services.Mcp;

namespace Bento.Api.Services.Ai;

/// <summary>
/// "Explain this" (Step 4.7): what a number or chart is made of, what changed
/// against the period before and which rows drive it — read-only, as the user.
/// </summary>
public sealed class ExplainService
{
    /// <summary>Only reading tools: explaining never changes anything.</summary>
    public static readonly IReadOnlyList<string> ToolNames =
        ["describe_schema", "list_rows", "get_row", "aggregate", "list_formula_functions", "get_dashboard", "list_saved_queries", "run_saved_query", "bento_guide"];

    private const int MaxContextLength = 60_000;
    private const int MaxResultRows = 60;

    private readonly AiAgent _agent;
    private readonly TemplateRegistry? _templates;

    public ExplainService(AiAgent agent, TemplateRegistry? templates = null)
    {
        _agent = agent;
        _templates = templates;
    }

    public sealed class ExplainRequest
    {
        /// <summary>The user's language, e.g. de-DE; the answer is written in it.</summary>
        public string? Language { get; init; }
        public long? DashboardId { get; init; }
        public string? Dashboard { get; init; }
        public string? Title { get; init; }
        public string? Type { get; init; }
        /// <summary>The query the widget ran, with the dashboard's range and filters already in its filter.</summary>
        public JsonElement? Query { get; init; }
        /// <summary>The widget's data settings (dateColumn, asOf, variable, …).</summary>
        public JsonElement? Data { get; init; }
        /// <summary>The rows the widget shows.</summary>
        public JsonElement? Result { get; init; }
        /// <summary>The dashboard's date range, e.g. { from, to } or a label.</summary>
        public JsonElement? Range { get; init; }
        public JsonElement? Filters { get; init; }
        /// <summary>What the user clicked: a bar, a slice, a point.</summary>
        public JsonElement? Point { get; init; }
        /// <summary>An optional question of the user's own.</summary>
        public string? Question { get; init; }
    }

    public sealed record ExplainResponse(string Answer, string Model, decimal CostUsd, int ToolCalls, int DurationMs);

    private static readonly string BasePrompt = string.Join("\n\n",
        """
        You explain a number or a chart on a Bento dashboard to its owner. Bento is their own data workspace; what its tables hold is up to them.

        You get the widget as JSON: its title and type, the query behind it with the dashboard's date range and filters already applied, what it shows now, and maybe what the user clicked or asked. Use the tools to find out what drives it — usually a few aggregate calls are enough: the same measure for the period before (and a year earlier when that helps), the biggest groups along the widget's natural breakdowns (a category, a project, a person, an account — whatever its table links to), and the largest single rows. Do not repeat what the widget already shows unless it is the answer.

        Then answer:
        - Start with the answer in one or two sentences: what the number means and the main reason it is what it is.
        - Then at most four short bullet points with concrete numbers: what it consists of, what changed against the period before and why, unusual rows (a one-off, a duplicate, something new that now recurs).
        - Amounts as the app shows them in the user's language (German: 1.234,56 €); percentages rounded.
        - No preamble, no headings, no description of your method, no advice unless asked. At most about 120 words.
        - Write in the language given as "language".
        - You only read; never say you changed anything. If the data cannot explain it (too few rows, no earlier period), say so briefly.
        """,
        McpTools.Instructions,
        "Reference — how queries work:\n" + McpTools.Guide("queries").Text);

    public async Task<ExplainResponse> ExplainAsync(McpCaller caller, ExplainRequest request, CancellationToken ct)
    {
        var context = new JsonObject
        {
            ["language"] = string.IsNullOrWhiteSpace(request.Language) ? "en" : request.Language.Trim(),
            ["today"] = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["dashboard"] = request.Dashboard,
            ["dashboardId"] = request.DashboardId,
            ["widget"] = new JsonObject
            {
                ["title"] = request.Title,
                ["type"] = request.Type,
                ["query"] = Node(request.Query),
                ["data"] = Node(request.Data),
                ["shows"] = Rows(request.Result),
            },
            ["dateRange"] = Node(request.Range),
            ["filters"] = Node(request.Filters),
            ["clicked"] = Node(request.Point),
            ["question"] = string.IsNullOrWhiteSpace(request.Question) ? null : request.Question.Trim(),
        };
        if (request.Query is null && request.Data is null && request.Result is null)
            throw new ApiValidationException("Send the widget's query, settings or result to explain.");

        var text = context.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        if (text.Length > MaxContextLength)
            throw new ApiValidationException("This widget is too large to explain in one go; narrow the date range or the filters.");

        var result = await _agent.RunAsync("explain", caller, await TemplatePrompts.WithTemplatesAsync(BasePrompt, _templates), "Explain this widget:\n" + text, ToolNames.ToList(), ct);
        return new ExplainResponse(result.Text, result.Model, result.CostUsd, result.ToolCalls, result.DurationMs);
    }

    private static JsonNode? Node(JsonElement? element) =>
        element is { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } value ? JsonNode.Parse(value.GetRawText()) : null;

    /// <summary>The first rows of what the widget shows, with a note when there are more.</summary>
    private static JsonNode? Rows(JsonElement? element)
    {
        if (element is not { ValueKind: JsonValueKind.Array } rows)
            return Node(element);
        var list = new JsonArray(rows.EnumerateArray().Take(MaxResultRows).Select(r => JsonNode.Parse(r.GetRawText())).ToArray());
        if (rows.GetArrayLength() > MaxResultRows)
            list.Add(JsonValue.Create($"… and {rows.GetArrayLength() - MaxResultRows} more rows"));
        return list;
    }
}
