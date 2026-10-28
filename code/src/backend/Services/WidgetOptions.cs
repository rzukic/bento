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
    private static readonly string[] SankeyModes = ["levels", "balance"];
    private static readonly string[] Buckets = ["day", "week", "month", "quarter", "year"];
    private static readonly string[] TimeShifts = ["previous", "year"];
    private static readonly string[] Shows = ["change", "moving_average"];
    private static readonly string[] Colors = ["blue", "teal", "amber", "violet", "rose", "green", "red", "gray"];
    private static readonly string[] EmptyPeriods = ["zero", "gap", "connect"];
    private static readonly string[] Presets =
    [
        "last-7-days", "this-week", "last-week", "this-month", "last-month", "this-quarter", "last-quarter", "last-3-months", "last-6-months", "last-12-months", "this-year", "last-year",
        "last-3-years", "last-5-years", "next-12-months", "next-5-years", "next-10-years", "next-20-years", "all"
    ];
    private static readonly string[] Flags =
        ["stacked", "compare", "higherIsBetter", "trend", "summary", "totalLine", "colorBySign", "annotations", "shares"];

    /// <summary>Dashboard variables viewers change: <c>input</c> is { kind: number | slider | select, min?, max?, step?, options? }.</summary>
    public static void CheckVariables(JsonElement dashboard)
    {
        if (!dashboard.TryGetProperty("variables", out var variables) || variables.ValueKind != JsonValueKind.Array)
            return;
        var i = 0;
        foreach (var variable in variables.EnumerateArray())
        {
            var path = $"dashboard.variables[{i++}]";
            if (variable.ValueKind != JsonValueKind.Object || !variable.TryGetProperty("input", out var input) || input.ValueKind == JsonValueKind.Null)
                continue;
            RequireObject(input, $"{path}.input");
            if (!input.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String)
                throw Invalid($"{path}.input.kind", "input.kind is \"number\", \"slider\" or \"select\".");
            OneOf(input, "kind", ["number", "slider", "select"], $"{path}.input");
            foreach (var bound in new[] { "min", "max", "step" })
            {
                if (input.TryGetProperty(bound, out var value) && value.ValueKind is not (JsonValueKind.Number or JsonValueKind.Null))
                    throw Invalid($"{path}.input.{bound}", $"input.{bound} is a number.");
            }
            if (kind.GetString() == "select" &&
                (!input.TryGetProperty("options", out var options) || options.ValueKind != JsonValueKind.Array || options.GetArrayLength() == 0 ||
                 options.EnumerateArray().Any(o => o.ValueKind is not (JsonValueKind.String or JsonValueKind.Number))))
                throw Invalid($"{path}.input.options", "A choice needs options: a list of texts or numbers.");
        }
    }

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
        if (display.TryGetProperty("sankeyLabels", out var labels) && labels.ValueKind != JsonValueKind.Null)
        {
            RequireObject(labels, $"{path}.sankeyLabels");
            foreach (var label in labels.EnumerateObject())
            {
                if (label.Name is not ("center" or "rest" or "shortfall") || label.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                    throw Invalid($"{path}.sankeyLabels.{label.Name}", "sankeyLabels has center, rest and shortfall, each a text.");
            }
        }
        OneOf(display, "emptyPeriods", EmptyPeriods, path);
        if (display.TryGetProperty("yAxis", out var yAxis) && yAxis.ValueKind != JsonValueKind.Null)
        {
            RequireObject(yAxis, $"{path}.yAxis");
            if (yAxis.TryGetProperty("min", out var min) && min.ValueKind is not (JsonValueKind.Number or JsonValueKind.Null) &&
                !(min.ValueKind == JsonValueKind.String && min.GetString() is "auto" or "zero"))
                throw Invalid($"{path}.yAxis.min", "yAxis.min is \"auto\" (near the data), \"zero\" or a number.");
        }
        if (display.TryGetProperty("link", out var link) && link.ValueKind != JsonValueKind.Null)
        {
            if (link.ValueKind != JsonValueKind.Object || !link.TryGetProperty("dashboard", out var target) || !target.TryGetInt64(out _) ||
                !link.TryGetProperty("column", out var by) || by.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(by.GetString()))
                throw Invalid($"{path}.link", "link is { dashboard: its id, column: the row's column, filter?: the filter there }.");
            if (link.TryGetProperty("filter", out var filter) && filter.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                throw Invalid($"{path}.link.filter", "link.filter is a column name.");
        }
        if (display.TryGetProperty("columns", out var columns) && columns.ValueKind != JsonValueKind.Null)
        {
            RequireObject(columns, $"{path}.columns");
            foreach (var column in columns.EnumerateObject())
            {
                var columnPath = $"{path}.columns.{column.Name}";
                RequireObject(column.Value, columnPath);
                OneOf(column.Value, "format", Formats, columnPath);
                foreach (var flag in new[] { "signed", "colorBySign" })
                {
                    if (column.Value.TryGetProperty(flag, out var value) && value.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null))
                        throw Invalid($"{columnPath}.{flag}", $"{flag} is true or false.");
                }
            }
        }
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
        if (data.TryGetProperty("range", out var range) && range.ValueKind != JsonValueKind.Null)
        {
            RequireObject(range, $"{path}.range");
            if (range.TryGetProperty("preset", out _))
                OneOf(range, "preset", Presets, $"{path}.range");
            else if (!IsDay(range, "from") || !IsDay(range, "to"))
                throw Invalid($"{path}.range", "range is { preset } or { from, to } with days like \"2026-01-01\".");
        }

        if (data.TryGetProperty("target", out var target) && target.ValueKind != JsonValueKind.Null)
        {
            var targetPath = $"{path}.target";
            RequireObject(target, targetPath);
            foreach (var field in new[] { "source", "value", "date", "group", "target" })
            {
                if (!target.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
                    throw Invalid($"{targetPath}.{field}", $"A target widget needs {field}: {TargetHelp}");
            }
            OneOf(target, "per", ["week", "month", "quarter", "year"], targetPath);
            OneOf(target, "period", ["current", "range"], targetPath);
            OneOf(target, "kind", ["limit", "goal"], targetPath);
            OneOf(target, "format", Formats, targetPath);
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

    private const string TargetHelp =
        "{ source, value (a formula over its rows, e.g. SUM(hours)), date, group (a link column), target (the linked table's number column with each one's target), " +
        "per?: \"week\"|\"month\"|\"quarter\"|\"year\", period?: \"current\"|\"range\", kind?: \"limit\"|\"goal\", format? }.";

    private static bool IsDay(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        DateOnly.TryParseExact(value.GetString(), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _);

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
