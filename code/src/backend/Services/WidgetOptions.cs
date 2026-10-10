using System.Text.Json;
using Bento.Api.Exceptions;

namespace Bento.Api.Services;

/// <summary>
/// Checks a widget's display and data options when a dashboard is saved, so a
/// typo (<c>"sankey": "xyz"</c>) is an error that names the field instead of a
/// widget that quietly falls back. Only the options Bento knows are checked;
/// other keys are kept as they are. Queries are checked when they run.
/// The options are listed in McpGuide/dashboards.md.
/// </summary>
public static class WidgetOptions
{
    private static readonly string[] Formats = ["auto", "currency", "number", "integer", "percent"];
    private static readonly string[] MeasureFormats = ["currency", "number", "integer", "percent"];
    private static readonly string[] SankeyModes = ["levels", "cashflow"];
    private static readonly string[] Buckets = ["day", "week", "month", "quarter", "year"];
    private static readonly string[] TimeShifts = ["previous", "year"];
    private static readonly string[] Shows = ["change", "moving_average"];
    private static readonly string[] Colors = ["blue", "teal", "amber", "violet", "rose", "green", "red", "gray"];
    private static readonly string[] Flags =
        ["stacked", "compare", "higherIsBetter", "trend", "summary", "totalLine", "colorBySign", "annotations", "shares"];

    public static void Check(JsonElement widget, string path)
    {
        if (widget.TryGetProperty("display", out var display) && display.ValueKind != JsonValueKind.Null)
            CheckDisplay(display, $"{path}.display");
        if (widget.TryGetProperty("data", out var data) && data.ValueKind != JsonValueKind.Null)
            CheckData(data, $"{path}.data");
    }

    private static void CheckDisplay(JsonElement display, string path)
    {
        RequireObject(display, path);
        OneOf(display, "format", Formats, path);
        OneOf(display, "sankey", SankeyModes, path);
        foreach (var flag in Flags)
        {
            if (display.TryGetProperty(flag, out var value) && value.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null))
                throw Invalid($"{path}.{flag}", $"{flag} is true or false.");
        }
        if (display.TryGetProperty("goal", out var goal) && goal.ValueKind is not (JsonValueKind.Number or JsonValueKind.String or JsonValueKind.Null))
            throw Invalid($"{path}.goal", "goal is a number or the name of a dashboard variable.");
        if (display.TryGetProperty("intervals", out var intervals) && intervals.ValueKind is not (JsonValueKind.False or JsonValueKind.Null))
        {
            if (intervals.ValueKind != JsonValueKind.Array)
                throw Invalid($"{path}.intervals", "intervals is false or a list such as [\"month\", \"quarter\", \"year\"].");
            var i = 0;
            foreach (var bucket in intervals.EnumerateArray())
                Known(bucket, Buckets, $"{path}.intervals[{i++}]", "an interval");
        }
        if (display.TryGetProperty("referenceLines", out var lines) && lines.ValueKind != JsonValueKind.Null)
        {
            if (lines.ValueKind != JsonValueKind.Array)
                throw Invalid($"{path}.referenceLines", "referenceLines is a list of { value, label? }.");
            var i = 0;
            foreach (var line in lines.EnumerateArray())
            {
                var linePath = $"{path}.referenceLines[{i++}]";
                if (line.ValueKind != JsonValueKind.Object ||
                    !line.TryGetProperty("value", out var value) || value.ValueKind is not (JsonValueKind.Number or JsonValueKind.String))
                    throw Invalid(linePath, "A reference line is { value: a number or a variable's name, label? }.");
            }
        }
    }

    private static void CheckData(JsonElement data, string path)
    {
        RequireObject(data, path);
        OneOf(data, "timeShift", TimeShifts, path);

        if (data.TryGetProperty("budget", out var budget) && budget.ValueKind != JsonValueKind.Null)
        {
            var budgetPath = $"{path}.budget";
            RequireObject(budget, budgetPath);
            foreach (var field in new[] { "source", "amount", "date", "category", "limit" })
            {
                if (!budget.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
                    throw Invalid($"{budgetPath}.{field}", $"A budget needs {field}: {BudgetHelp}");
            }
            OneOf(budget, "expenses", ["negative", "positive"], budgetPath);
            OneOf(budget, "period", ["month", "range"], budgetPath);
        }

        if (data.TryGetProperty("rows", out var rows) && rows.ValueKind != JsonValueKind.Null)
        {
            var rowsPath = $"{path}.rows";
            RequireObject(rows, rowsPath);
            if (!rows.TryGetProperty("columns", out var columns) || columns.ValueKind != JsonValueKind.Array ||
                columns.EnumerateArray().Any(c => c.ValueKind != JsonValueKind.String))
                throw Invalid($"{rowsPath}.columns", "rows.columns is a list of column names.");
            if (rows.TryGetProperty("sort", out var sort) && sort.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                throw Invalid($"{rowsPath}.sort", "rows.sort is a column name, with - in front for descending: \"-date\".");
            if (rows.TryGetProperty("limit", out var limit) && limit.ValueKind != JsonValueKind.Null &&
                (!limit.TryGetInt32(out var count) || count < 1 || count > 1000))
                throw Invalid($"{rowsPath}.limit", "rows.limit is a whole number from 1 to 1000.");
        }

        if (data.TryGetProperty("query", out var query) && query.ValueKind == JsonValueKind.Object &&
            query.TryGetProperty("measures", out var measures) && measures.ValueKind == JsonValueKind.Array)
        {
            var i = 0;
            foreach (var measure in measures.EnumerateArray())
            {
                var measurePath = $"{path}.query.measures[{i++}]";
                if (measure.ValueKind != JsonValueKind.Object)
                    continue;
                OneOf(measure, "show", Shows, measurePath);
                OneOf(measure, "axis", ["right"], measurePath);
                OneOf(measure, "format", MeasureFormats, measurePath);
                OneOf(measure, "color", Colors, measurePath);
                if (measure.TryGetProperty("window", out var window) && window.ValueKind != JsonValueKind.Null &&
                    (!window.TryGetInt32(out var periods) || periods < 2 || periods > 120))
                    throw Invalid($"{measurePath}.window", "window is the number of periods of a moving average, 2 to 120.");
            }
        }
    }

    private const string BudgetHelp =
        "{ source, amount, date, category (the link column to the categories), limit (the categories' monthly limit column), expenses?: \"negative\"|\"positive\", period?: \"month\"|\"range\" }.";

    private static void OneOf(JsonElement element, string name, string[] allowed, string path)
    {
        if (element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null)
            Known(value, allowed, $"{path}.{name}", name);
    }

    private static void Known(JsonElement value, string[] allowed, string path, string what)
    {
        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (text is null || !allowed.Contains(text))
            throw Invalid(path, $"{char.ToUpperInvariant(what[0])}{what[1..]} is {Join(allowed)}, not {value.GetRawText()}.");
    }

    private static void RequireObject(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw Invalid(path, $"{path.Split('.')[^1]} must be an object.");
    }

    private static string Join(string[] values) =>
        values.Length == 1 ? $"\"{values[0]}\"" : string.Join(", ", values[..^1].Select(v => $"\"{v}\"")) + $" or \"{values[^1]}\"";

    private static ApiValidationException Invalid(string field, string message) =>
        new(message, new Dictionary<string, object?> { ["field"] = field });
}
