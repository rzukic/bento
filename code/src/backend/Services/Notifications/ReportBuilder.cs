using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bento.Api.Db;
using Bento.Api.Db.Schema;
using Bento.Api.Exceptions;
using Bento.Api.Models;
using Bento.Api.Services.Ai;
using Bento.Api.Services.Mcp;
using Dapper;

namespace Bento.Api.Services.Notifications;

/// <summary>
/// Scheduled reports (Step 5.3): a message every day, week, month, quarter or
/// year, put together from blocks the user chooses — values written as
/// formulas, the biggest values of a group, the key figures of any dashboard,
/// the rules that hold, plain text — each for the period just over (or so far)
/// against the one before or a year earlier. Nothing in it knows what the data
/// is about; optionally the AI writes a few sentences about the numbers.
/// </summary>
public sealed class ReportBuilder
{
    public static readonly string[] Units = ["day", "week", "month", "quarter", "year"];
    public static readonly string[] BlockTypes = ["value", "top", "dashboard", "rules", "text"];
    public static readonly string[] Formats = ["auto", "currency", "number", "integer", "percent"];
    public const int MaxBlocks = 20;
    public const int MaxTop = 20;
    private const int MaxText = 2000;
    private const int MaxInstructions = 1000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ISystemPool _systemPool;
    private readonly SchemaCache _schemaCache;
    private readonly AggregationQueryService _aggregation;
    private readonly WorkspaceSettings _workspace;
    private readonly AiAgent? _ai;
    private readonly ILogger<ReportBuilder>? _logger;

    public ReportBuilder(
        ISystemPool systemPool,
        SchemaCache schemaCache,
        AggregationQueryService aggregation,
        WorkspaceSettings workspace,
        AiAgent? ai = null,
        ILogger<ReportBuilder>? logger = null)
    {
        _systemPool = systemPool;
        _schemaCache = schemaCache;
        _aggregation = aggregation;
        _workspace = workspace;
        _ai = ai;
        _logger = logger;
    }

    private bool Pg => _systemPool.Provider == DatabaseProvider.Postgres;
    private string Dashboards => Pg ? "_system.dashboards" : "dashboards";
    private string SavedQueries => Pg ? "_system.saved_queries" : "saved_queries";

    // ── Periods ──────────────────────────────────────────────────────────

    /// <summary>[From, To) with a label for people: "September 2026", "Week 41 (5–11 Oct 2026)", "October 2026 until 11 Oct".</summary>
    public sealed record ReportPeriod(DateOnly From, DateOnly To, string Label);

    public sealed record ReportPeriods(ReportPeriod Current, ReportPeriod? Compare);

    public static DateOnly UnitStart(string unit, DateOnly day) => unit switch
    {
        "day" => day,
        "week" => day.AddDays(-(((int)day.DayOfWeek + 6) % 7)),
        "month" => new DateOnly(day.Year, day.Month, 1),
        "quarter" => new DateOnly(day.Year, (day.Month - 1) / 3 * 3 + 1, 1),
        _ => new DateOnly(day.Year, 1, 1)
    };

    private static DateOnly Add(string unit, DateOnly day, int count) => unit switch
    {
        "day" => day.AddDays(count),
        "week" => day.AddDays(7 * count),
        "month" => day.AddMonths(count),
        "quarter" => day.AddMonths(3 * count),
        _ => day.AddYears(count)
    };

    /// <summary>
    /// The period a report sent on <paramref name="day"/> covers — the one just
    /// over, or the current one up to and including that day — and the one it
    /// is compared with: the period before (as far into it, for one so far) or
    /// the same days a year earlier.
    /// </summary>
    public static ReportPeriods Periods(string unit, string? range, string? compare, DateOnly day)
    {
        DateOnly from, to;
        if (range == "current")
        {
            from = UnitStart(unit, day);
            to = day.AddDays(1);
        }
        else
        {
            to = UnitStart(unit, day);
            from = Add(unit, to, -1);
        }
        var current = new ReportPeriod(from, to, Label(unit, from, to));
        ReportPeriod? before = null;
        if (compare == "year")
        {
            before = new ReportPeriod(from.AddYears(-1), to.AddYears(-1), Label(unit, from.AddYears(-1), to.AddYears(-1)));
        }
        else if (compare != "none")
        {
            // A whole period against the whole one before; one so far against as many days of it.
            var start = Add(unit, from, -1);
            var end = range == "current" ? start.AddDays(to.DayNumber - from.DayNumber) : from;
            if (end > from)
                end = from;
            before = new ReportPeriod(start, end, Label(unit, start, end));
        }
        return new ReportPeriods(current, before);
    }

    private static string Label(string unit, DateOnly from, DateOnly to)
    {
        var last = to.AddDays(-1);
        if (UnitStart(unit, from) != from)
            return Span(from, last);
        var whole = Add(unit, from, 1);
        var name = unit switch
        {
            "day" => from.ToString("ddd, d MMM yyyy", CultureInfo.InvariantCulture),
            "week" => $"Week {ISOWeek.GetWeekOfYear(from.ToDateTime(TimeOnly.MinValue))} ({Span(from, whole.AddDays(-1))})",
            "month" => from.ToString("MMMM yyyy", CultureInfo.InvariantCulture),
            "quarter" => $"Q{(from.Month - 1) / 3 + 1} {from.Year}",
            _ => from.Year.ToString(CultureInfo.InvariantCulture)
        };
        if (to == whole)
            return name;
        if (unit == "week")
            name = $"Week {ISOWeek.GetWeekOfYear(from.ToDateTime(TimeOnly.MinValue))}";
        return $"{name} until {last.ToString("d MMM", CultureInfo.InvariantCulture)}";
    }

    /// <summary>5–11 Oct 2026, 28 Sep – 4 Oct 2026, 29 Dec 2025 – 4 Jan 2026.</summary>
    private static string Span(DateOnly first, DateOnly last)
    {
        string Text(DateOnly day, string format) => day.ToString(format, CultureInfo.InvariantCulture);
        if (first == last)
            return Text(first, "d MMM yyyy");
        if (first.Year != last.Year)
            return $"{Text(first, "d MMM yyyy")} – {Text(last, "d MMM yyyy")}";
        if (first.Month != last.Month)
            return $"{Text(first, "d MMM")} – {Text(last, "d MMM yyyy")}";
        return $"{first.Day}–{Text(last, "d MMM yyyy")}";
    }

    /// <summary>The last time the report was due (at or before <paramref name="now"/>), in the given time zone.</summary>
    public static DateTimeOffset LastOccurrence(NotificationRuleConfig config, DateTimeOffset now, TimeZoneInfo zone)
    {
        var unit = config.Every ?? "month";
        var at = TimeOnly.ParseExact(config.At ?? "08:00", "HH:mm", CultureInfo.InvariantCulture);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        DateOnly Scheduled(DateOnly start) => unit == "day" ? start : start.AddDays((config.Day ?? 1) - 1);
        var when = At(Scheduled(UnitStart(unit, today)), at, zone);
        return when <= now ? when : At(Scheduled(Add(unit, UnitStart(unit, today), -1)), at, zone);
    }

    private static DateTimeOffset At(DateOnly day, TimeOnly time, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(time);
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    /// <summary>"Monthly on the 1st at 08:00 · the last month against the month before · 3 blocks".</summary>
    public static string Summary(NotificationRuleConfig config)
    {
        var day = config.Day ?? 1;
        var when = config.Every switch
        {
            "day" => "Daily",
            "week" => $"Weekly on {(DayOfWeek)(day % 7)}",
            "month" => $"Monthly on the {Ordinal(day)}",
            "quarter" => $"Quarterly on the {Ordinal(day)}",
            _ => $"Yearly on {Ordinal(day)} January"
        };
        var unit = config.Every switch { "day" => "day", "week" => "week", "quarter" => "quarter", "year" => "year", _ => "month" };
        var range = config.Range == "current" ? $"this {unit} so far" : unit == "day" ? "yesterday" : $"the last {unit}";
        var compare = config.Compare switch { "none" => "", "year" => " against a year earlier", _ => $" against the {unit} before" };
        var count = config.Blocks?.Count ?? 0;
        return $"{when} at {config.At} · {range}{compare} · {count} {(count == 1 ? "block" : "blocks")}{(config.Ai == true ? " · AI text" : "")}";
    }

    private static string Ordinal(int day) => day + (day % 10 == 1 && day != 11 ? "st" : day % 10 == 2 && day != 12 ? "nd" : day % 10 == 3 && day != 13 ? "rd" : "th");

    // ── Validation ───────────────────────────────────────────────────────

    public async Task<NotificationRuleConfig> ValidateAsync(NotificationRuleConfig config)
    {
        var unit = string.IsNullOrWhiteSpace(config.Every) ? "month" : config.Every.Trim().ToLowerInvariant();
        if (!Units.Contains(unit))
            throw Invalid("config.every", "A report goes out every day, week, month, quarter or year.");
        var at = string.IsNullOrWhiteSpace(config.At) ? "08:00" : config.At.Trim();
        if (!TimeOnly.TryParseExact(at, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            throw Invalid("config.at", "Give the time of day as HH:mm, e.g. 08:00.");
        int? day = null;
        if (unit == "week")
        {
            day = config.Day ?? 1;
            if (day is < 1 or > 7)
                throw Invalid("config.day", "The weekday is 1 (Monday) to 7 (Sunday).");
        }
        else if (unit != "day")
        {
            day = config.Day ?? 1;
            if (day is < 1 or > 28)
                throw Invalid("config.day", "The day of the month is 1 to 28, so that every month has it.");
        }
        var range = string.IsNullOrWhiteSpace(config.Range) ? "last" : config.Range.Trim().ToLowerInvariant();
        if (range is not ("last" or "current"))
            throw Invalid("config.range", "The report covers the last period (last) or the current one so far (current).");
        var compare = string.IsNullOrWhiteSpace(config.Compare) ? "previous" : config.Compare.Trim().ToLowerInvariant();
        if (compare is not ("previous" or "year" or "none"))
            throw Invalid("config.compare", "Compare with the period before (previous), a year earlier (year) or not at all (none).");
        var intro = Optional(config.Intro, MaxText, "config.intro", "The text at the top");
        var instructions = Optional(config.AiInstructions, MaxInstructions, "config.aiInstructions", "The instructions for the AI");

        var blocks = config.Blocks ?? [];
        if (blocks.Count == 0)
            throw Invalid("config.blocks", "Add at least one block: a value, a top list, a dashboard's key figures, the rules or text.");
        if (blocks.Count > MaxBlocks)
            throw Invalid("config.blocks", $"A report has at most {MaxBlocks} blocks.");
        var checkedBlocks = new List<ReportBlock>();
        for (var i = 0; i < blocks.Count; i++)
            checkedBlocks.Add(await ValidateBlockAsync(blocks[i], $"config.blocks[{i}]"));

        return new NotificationRuleConfig
        {
            Every = unit,
            At = at,
            Day = day,
            Range = range,
            Compare = compare,
            Intro = intro,
            Ai = config.Ai == true,
            AiInstructions = instructions,
            Blocks = checkedBlocks
        };
    }

    private async Task<ReportBlock> ValidateBlockAsync(ReportBlock block, string field)
    {
        var type = block.Type?.Trim().ToLowerInvariant() ?? "";
        if (!BlockTypes.Contains(type))
            throw Invalid($"{field}.type", "A block is a value, top, dashboard, rules or text.");
        var label = Optional(block.Label, 200, $"{field}.label", "The label");
        switch (type)
        {
            case "value":
            case "top":
            {
                var table = _schemaCache.GetTable(block.Source?.Trim() ?? "")
                    ?? throw Invalid($"{field}.source", "Choose the table the value comes from.");
                if (string.IsNullOrWhiteSpace(block.Formula))
                    throw Invalid($"{field}.formula", "Write the value as a formula, e.g. SUM(amount) or COUNT().");
                string? dateColumn = null;
                if (!string.IsNullOrWhiteSpace(block.DateColumn))
                {
                    var column = table.Columns.FirstOrDefault(c => string.Equals(c.Name, block.DateColumn.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (column is null || column.Type is not (ColumnType.Date or ColumnType.DateTime))
                        throw Invalid($"{field}.dateColumn", $"Choose a date column of {table.DisplayName ?? table.Name}, or none for all rows.");
                    dateColumn = column.Name;
                }
                var format = string.IsNullOrWhiteSpace(block.Format) ? "auto" : block.Format.Trim().ToLowerInvariant();
                if (!Formats.Contains(format))
                    throw Invalid($"{field}.format", "The format is auto, currency, number, integer or percent.");
                var checkedBlock = new ReportBlock
                {
                    Type = type,
                    Label = label,
                    Source = table.Name,
                    Formula = block.Formula.Trim(),
                    DateColumn = dateColumn,
                    Format = format,
                    HigherIsBetter = block.HigherIsBetter ?? true
                };
                if (type == "value")
                {
                    // The formula runs once now: a typo is said at once, not when the report goes out.
                    var trial = (await _aggregation.EvaluateVariablesAsync(new QueryVariablesRequest
                    {
                        Variables = [new QueryVariable { Name = "value", Source = table.Name, Formula = checkedBlock.Formula }]
                    })).Single();
                    if (trial.Error is not null)
                        throw Invalid($"{field}.formula", trial.Error);
                    return checkedBlock;
                }
                var group = table.Columns.FirstOrDefault(c => string.Equals(c.Name, block.GroupBy?.Trim(), StringComparison.OrdinalIgnoreCase))
                    ?? throw Invalid($"{field}.groupBy", $"Choose the column of {table.DisplayName ?? table.Name} to list the values by.");
                var limit = block.Limit ?? 5;
                if (limit is < 1 or > MaxTop)
                    throw Invalid($"{field}.limit", $"List 1 to {MaxTop} values.");
                var order = string.IsNullOrWhiteSpace(block.Order) ? "desc" : block.Order.Trim().ToLowerInvariant();
                if (order is not ("desc" or "asc"))
                    throw Invalid($"{field}.order", "Order by the biggest (desc) or the smallest (asc) values.");
                checkedBlock = checkedBlock with { GroupBy = group.Name, Limit = limit, Order = order };
                try
                {
                    await _aggregation.RunAsync(TopQuery(checkedBlock, null, 1));
                }
                catch (ApiException e)
                {
                    throw Invalid($"{field}.formula", e.Message);
                }
                return checkedBlock;
            }
            case "dashboard":
            {
                if (block.DashboardId is not { } id)
                    throw Invalid($"{field}.dashboardId", "Choose the dashboard whose key figures go in the report.");
                var dashboard = await LoadDashboardAsync(id) ?? throw Invalid($"{field}.dashboardId", $"There is no dashboard {id}.");
                var kpis = KpiWidgets(dashboard.Document, null).Select(w => w["id"]?.GetValue<string>()).ToHashSet();
                if (kpis.Count == 0)
                    throw Invalid($"{field}.dashboardId", $"The dashboard “{dashboard.Name}” has no key figures (KPI widgets) for a period.");
                var widgets = (block.Widgets ?? []).Select(w => w.Trim()).Where(w => w.Length > 0).Distinct().ToList();
                var unknown = widgets.FirstOrDefault(w => !kpis.Contains(w));
                if (unknown is not null)
                    throw Invalid($"{field}.widgets", $"The dashboard “{dashboard.Name}” has no key figure {unknown}.");
                return new ReportBlock { Type = type, Label = label, DashboardId = id, Widgets = widgets.Count > 0 ? widgets : null };
            }
            case "rules":
                return new ReportBlock { Type = type, Label = label };
            default:
            {
                var text = Optional(block.Text, MaxText, $"{field}.text", "The text")
                    ?? throw Invalid($"{field}.text", "Write the text of the block.");
                return new ReportBlock { Type = type, Label = label, Text = text };
            }
        }
    }

    private static string? Optional(string? text, int max, string field, string what)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return null;
        if (trimmed.Length > max)
            throw Invalid(field, $"{what} is longer than {max} characters.");
        return trimmed;
    }

    // ── Building ─────────────────────────────────────────────────────────

    /// <param name="Summary">One line for the rule's state: the period and what was left out.</param>
    public sealed record ReportResult(Notification Notification, string Summary);

    /// <param name="day">The day the report is sent on (its periods follow from it).</param>
    /// <param name="activeRules">The rules that hold now, as facts, for a rules block.</param>
    /// <param name="useAi">Ask the AI for its sentences (when the report wants them).</param>
    public async Task<ReportResult> BuildAsync(
        string name, NotificationRuleConfig config, DateOnly day, DateOnly today, IReadOnlyList<NotificationFact> activeRules, bool useAi, Func<string, string?> link, CancellationToken ct)
    {
        var workspace = await _workspace.GetAsync();
        var periods = Periods(config.Every ?? "month", config.Range, config.Compare, day);
        var sections = new List<NotificationSection>();
        List<NotificationFact>? values = null;
        long? firstDashboard = null;

        foreach (var block in config.Blocks ?? [])
        {
            ct.ThrowIfCancellationRequested();
            if (block.Type == "value")
            {
                if (values is null)
                {
                    values = [];
                    sections.Add(new NotificationSection(null, null, values));
                }
                values.Add(await ValueAsync(block, periods, workspace));
                continue;
            }
            values = null;
            switch (block.Type)
            {
                case "top":
                    sections.Add(await TopAsync(block, periods, workspace));
                    break;
                case "dashboard":
                    firstDashboard ??= block.DashboardId;
                    sections.Add(await DashboardAsync(block, periods, today, workspace));
                    break;
                case "rules":
                    sections.Add(activeRules.Count == 0
                        ? new NotificationSection(block.Label ?? "Rules", "No rule holds right now.")
                        : new NotificationSection(block.Label ?? "Rules that hold", null, activeRules));
                    break;
                default:
                    sections.Add(new NotificationSection(block.Label, block.Text));
                    break;
            }
        }

        var heading = periods.Compare is { } compare ? $"{periods.Current.Label}, compared with {compare.Label}." : $"{periods.Current.Label}.";
        var text = new StringBuilder();
        if (config.Intro is { } intro)
            text.Append(intro).Append("\n\n");
        var summary = periods.Current.Label;
        if (useAi && config.Ai == true)
        {
            var (aiText, aiError) = await AiTextAsync(name, heading, sections, config.AiInstructions, workspace, ct);
            if (aiText is not null)
                text.Append(aiText);
            else
            {
                text.Append(heading);
                summary += $" · without AI text: {aiError}";
            }
        }
        else
        {
            text.Append(heading);
        }

        var notification = new Notification(
            "report",
            $"{name} · {periods.Current.Label}",
            text.ToString().Trim(),
            link(firstDashboard is { } dashboardId ? $"/dashboards/{dashboardId}" : "/"),
            null,
            sections);
        return new ReportResult(notification, summary);
    }

    private static JsonElement? DateFilter(string? column, ReportPeriod period) =>
        column is null
            ? null
            : JsonSerializer.SerializeToElement(new Dictionary<string, object>
            {
                [column] = new Dictionary<string, string>
                {
                    ["gte"] = period.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    ["lt"] = period.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                }
            });

    private static decimal? Number(object? value) =>
        value is not null && decimal.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;

    /// <summary>A value with its change against the period compared with: "+€134.00 (+12%) vs. August 2026".</summary>
    private static NotificationFact Fact(
        string label, decimal? value, string? type, bool compared, decimal? previous, ReportPeriod? compare, bool higherIsBetter, WorkspaceSettings.Values workspace)
    {
        var shown = MessageFormat.Format(value, type, workspace);
        if (!compared || compare is null)
            return new NotificationFact(label, shown, Number: value);
        if (previous is null || value is null)
            return new NotificationFact(label, shown, $"{compare.Label}: {MessageFormat.Format(previous, type, workspace)}", Number: value, Previous: previous);
        var delta = value.Value - previous.Value;
        if (delta == 0)
            return new NotificationFact(label, shown, $"Same as {compare.Label}", Number: value, Previous: previous);
        var change = (delta > 0 ? "+" : "") + MessageFormat.Format(delta, type, workspace);
        if (previous.Value != 0 && type != "percent")
            change += $" ({(delta > 0 ? "+" : "")}{MessageFormat.Format(delta / Math.Abs(previous.Value), "percent", workspace)})";
        return new NotificationFact(label, shown, $"{change} vs. {compare.Label}", delta > 0 == higherIsBetter ? "good" : "bad", value, previous);
    }

    private static string? FormatOf(string? format, string? type) => format is null or "auto" ? type : format;

    private async Task<NotificationFact> ValueAsync(ReportBlock block, ReportPeriods periods, WorkspaceSettings.Values workspace)
    {
        var label = block.Label ?? block.Formula!;
        var compared = block.DateColumn is not null && periods.Compare is not null;
        var variables = new List<QueryVariable> { new() { Name = "current", Source = block.Source, Formula = block.Formula, Filter = DateFilter(block.DateColumn, periods.Current) } };
        if (compared)
            variables.Add(new QueryVariable { Name = "compare", Source = block.Source, Formula = block.Formula, Filter = DateFilter(block.DateColumn, periods.Compare!) });
        IReadOnlyList<QueryVariableResult> results;
        try
        {
            results = await _aggregation.EvaluateVariablesAsync(new QueryVariablesRequest { Variables = variables });
        }
        catch (ApiException e)
        {
            return new NotificationFact(label, "—", e.Message);
        }
        var current = results[0];
        if (current.Error is not null)
            return new NotificationFact(label, "—", current.Error);
        var number = Number(current.Value);
        if (number is null && current.Value is not null)
            return new NotificationFact(label, Convert.ToString(current.Value, CultureInfo.InvariantCulture) ?? "—");
        // No rows in the period: 0, as on a dashboard.
        return Fact(label, number ?? 0, FormatOf(block.Format, current.Type), compared, compared ? Number(results[1].Value) ?? 0 : null, periods.Compare, block.HigherIsBetter ?? true, workspace);
    }

    private static AggregationQueryRequest TopQuery(ReportBlock block, ReportPeriod? period, int? limit) => new()
    {
        Source = block.Source,
        Filter = period is null ? null : DateFilter(block.DateColumn, period),
        GroupBy = [new QueryGroupBy { Column = block.GroupBy }],
        Measures = [new QueryMeasure { Name = "value", Formula = block.Formula }],
        Sort = limit is null ? null : [block.Order == "asc" ? "value" : "-value"],
        Limit = limit ?? AggregationQueryService.MaxLimit
    };

    private async Task<NotificationSection> TopAsync(ReportBlock block, ReportPeriods periods, WorkspaceSettings.Values workspace)
    {
        var title = block.Label ?? $"{block.Formula} by {block.GroupBy}";
        try
        {
            // Sorted on the server, the zeros left out here, then the first few.
            var result = await _aggregation.RunAsync(TopQuery(block, periods.Current, 1000));
            var group = result.Meta.Columns.FirstOrDefault(c => c.Role == "group")?.Name ?? block.GroupBy!;
            var labelColumn = result.Meta.Columns.FirstOrDefault(c => c.Role == "label" && c.LabelOf == group)?.Name;
            var type = FormatOf(block.Format, result.Meta.Columns.FirstOrDefault(c => c.Role == "measure")?.Type);
            var compared = block.DateColumn is not null && periods.Compare is not null;
            var before = new Dictionary<string, decimal?>(StringComparer.Ordinal);
            if (compared)
            {
                foreach (var row in (await _aggregation.RunAsync(TopQuery(block, periods.Compare!, null))).Data)
                    before[Key(row, group)] = Number(row.TryGetValue("value", out var v) ? v : null);
            }
            // A group with nothing in the period (0, e.g. income in a list of spending) is no news.
            var rows = result.Data.Where(row => Number(row.TryGetValue("value", out var v) ? v : null) is { } n && n != 0).Take(block.Limit ?? 5).ToList();
            if (rows.Count == 0)
                return new NotificationSection(title, "Nothing in this period.");
            var facts = rows.Select(row =>
            {
                var label = labelColumn is not null && row.TryGetValue(labelColumn, out var l) && l is not null ? Convert.ToString(l, CultureInfo.InvariantCulture)! : Key(row, group);
                return Fact(label.Length == 0 ? "(none)" : label, Number(row.TryGetValue("value", out var v) ? v : null) ?? 0, type, compared,
                    compared ? before.GetValueOrDefault(Key(row, group)) ?? 0 : null, periods.Compare, block.HigherIsBetter ?? true, workspace);
            }).ToList();
            return new NotificationSection(title, null, facts);
        }
        catch (ApiException e)
        {
            return new NotificationSection(title, $"Could not be computed: {e.Message}");
        }
    }

    private static string Key(IDictionary<string, object?> row, string column) =>
        Convert.ToString(row.TryGetValue(column, out var value) ? value : null, CultureInfo.InvariantCulture) ?? "";

    // ── Dashboards ───────────────────────────────────────────────────────

    private sealed record DashboardDocument(string Name, JsonObject Document);

    private async Task<DashboardDocument?> LoadDashboardAsync(long id)
    {
        using var conn = await _systemPool.GetConnectionAsync();
        var row = await conn.QuerySingleOrDefaultAsync<(string Name, string Json)?>(
            $"SELECT name, CAST(dashboard_json AS TEXT) FROM {Dashboards} WHERE id = @Id", new { Id = id });
        return row is { } found && JsonNode.Parse(found.Json) is JsonObject document ? new DashboardDocument(found.Name, document) : null;
    }

    /// <summary>Key figures that follow the dashboard's range: KPI widgets without a range of their own (a forecast keeps its range).</summary>
    private static IEnumerable<JsonObject> KpiWidgets(JsonObject document, IReadOnlyList<string>? ids) =>
        (document["widgets"] as JsonArray ?? [])
        .OfType<JsonObject>()
        .Where(w => w["type"]?.GetValue<string>() == "kpi" && w["data"] is JsonObject data && data["range"] is null &&
                    (data["query"] is JsonObject || data["savedQueryId"] is not null || data["variable"] is not null))
        .Where(w => ids is null || ids.Contains(w["id"]?.GetValue<string>() ?? ""))
        .OrderBy(w => w["position"]?["y"]?.GetValue<double>() ?? 0)
        .ThenBy(w => w["position"]?["x"]?.GetValue<double>() ?? 0);

    private async Task<NotificationSection> DashboardAsync(ReportBlock block, ReportPeriods periods, DateOnly today, WorkspaceSettings.Values workspace)
    {
        var dashboard = await LoadDashboardAsync(block.DashboardId!.Value);
        if (dashboard is null)
            return new NotificationSection(block.Label ?? "Dashboard", $"The dashboard {block.DashboardId} no longer exists.");
        var widgets = KpiWidgets(dashboard.Document, block.Widgets).ToList();
        var variables = dashboard.Document["variables"] as JsonArray;
        var current = await VariablesAsync(variables, periods.Current);
        var before = periods.Compare is { } compare ? await VariablesAsync(variables, compare) : null;

        var facts = new List<NotificationFact>();
        foreach (var widget in widgets)
        {
            var display = widget["display"] as JsonObject;
            var title = widget["title"]?.GetValue<string>() is { Length: > 0 } t ? t : widget["id"]?.GetValue<string>() ?? "Value";
            var higherIsBetter = display?["higherIsBetter"]?.GetValue<bool>() ?? true;
            var format = display?["format"]?.GetValue<string>();
            var now = await KpiAsync(widget, periods.Current, current, today);
            if (now.Error is not null)
            {
                facts.Add(new NotificationFact(title, "—", now.Error));
                continue;
            }
            var then = before is null ? null : await KpiAsync(widget, periods.Compare!, before, today);
            facts.Add(Fact(title, now.Value, FormatOf(format, now.Type), then is not null && then.Error is null, then?.Value, periods.Compare, higherIsBetter, workspace));
        }
        return new NotificationSection(block.Label ?? dashboard.Name, facts.Count == 0 ? "No key figures." : null, facts);
    }

    private async Task<Dictionary<string, QueryVariableResult>> VariablesAsync(JsonArray? variables, ReportPeriod period)
    {
        var requests = (variables ?? [])
            .OfType<JsonObject>()
            .Where(v => v["name"]?.GetValue<string>() is { Length: > 0 } && v["formula"] is not null)
            .Select(v =>
            {
                var source = v["source"]?.GetValue<string>();
                var dateColumn = v["dateColumn"]?.GetValue<string>();
                return new QueryVariable
                {
                    Name = v["name"]!.GetValue<string>(),
                    Source = source,
                    Formula = v["formula"]!.ToString(),
                    Filter = source is not null && dateColumn is not null ? DateFilter(dateColumn, period) : null
                };
            })
            .ToList();
        if (requests.Count == 0)
            return [];
        try
        {
            return (await _aggregation.EvaluateVariablesAsync(new QueryVariablesRequest { Variables = requests })).ToDictionary(r => r.Name);
        }
        catch (ApiException e)
        {
            return requests.ToDictionary(r => r.Name!, r => new QueryVariableResult(r.Name!, null, null, e.Message));
        }
    }

    private sealed record KpiValue(decimal? Value, string? Type, string? Error);

    /// <summary>
    /// A KPI widget's value for one period, as the dashboard computes it for
    /// that range: its query filtered on its date column (or at the end of the
    /// period, for values as of a date), the first measure of the last row.
    /// </summary>
    private async Task<KpiValue> KpiAsync(JsonObject widget, ReportPeriod period, Dictionary<string, QueryVariableResult> variables, DateOnly today)
    {
        var data = (JsonObject)widget["data"]!;
        if (data["variable"]?.GetValue<string>() is { } name)
        {
            if (!variables.TryGetValue(name, out var variable))
                return new KpiValue(null, null, $"The dashboard has no variable {name}.");
            return variable.Error is not null ? new KpiValue(null, null, variable.Error) : new KpiValue(Number(variable.Value), variable.Type, null);
        }

        try
        {
            AggregationQueryRequest? query;
            if (data["query"] is JsonObject inline)
            {
                query = inline.Deserialize<AggregationQueryRequest>(Json);
            }
            else
            {
                var id = data["savedQueryId"]!.GetValue<long>();
                using var conn = await _systemPool.GetConnectionAsync();
                var config = await conn.ExecuteScalarAsync<string?>($"SELECT CAST(config AS TEXT) FROM {SavedQueries} WHERE id = @Id", new { Id = id });
                if (config is null)
                    return new KpiValue(null, null, $"The saved query {id} no longer exists.");
                query = JsonSerializer.Deserialize<AggregationQueryRequest>(config, Json);
            }
            if (query is null)
                return new KpiValue(null, null, "The widget has no query.");

            var values = variables.Values.Where(v => v.Error is null).ToDictionary(v => v.Name, v => JsonSerializer.SerializeToElement(v.Value));
            if (values.Count > 0 && (query.Measures ?? []).Any(m => m.Formula is not null))
                query = query with { Variables = values };

            var dateGroup = query.GroupBy?.FirstOrDefault(g => g.Bucket is not null);
            AggregationQueryResponse result;
            if (data["asOf"]?.GetValue<bool>() == true && dateGroup is not null)
            {
                var last = period.To.AddDays(-1);
                if (last > today)
                    last = today < period.From ? period.From : today;
                result = await _aggregation.RunAsync(query with
                {
                    GroupBy = query.GroupBy!.Where(g => g != dateGroup).ToList(),
                    AsOf = new QueryAsOf
                    {
                        Bucket = dateGroup.Bucket,
                        From = period.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        To = last.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        As = dateGroup.As ?? dateGroup.Column
                    }
                });
            }
            else
            {
                var dateColumn = data.ContainsKey("dateColumn") ? data["dateColumn"]?.GetValue<string>() : dateGroup?.Column;
                result = await _aggregation.RunAsync(query, DateFilter(dateColumn, period));
            }

            var measure = result.Meta.Columns.FirstOrDefault(c => c.Role == "measure");
            if (measure is null || result.Data.Count == 0)
                return new KpiValue(null, measure?.Type, null);
            var group = result.Meta.Columns.FirstOrDefault(c => c.Role == "group");
            var row = group is null
                ? result.Data[^1]
                : result.Data.OrderBy(r => Key(r, group.Name), StringComparer.Ordinal).Last();
            return new KpiValue(Number(row.TryGetValue(measure.Name, out var v) ? v : null), measure.Type, null);
        }
        catch (Exception e) when (e is ApiException or JsonException or InvalidOperationException)
        {
            return new KpiValue(null, null, e.Message);
        }
    }

    // ── AI ───────────────────────────────────────────────────────────────

    private const string AiSystem = """
        You write the opening lines of a scheduled report from Bento, a workspace where people keep their own data in tables. The report itself follows below your text; you get its figures.

        Write two to four sentences about what stands out: the biggest changes against the period compared with, values that are unusual, what moved in a top list. Use only the figures given — never invent numbers, causes or advice. Plain text: no markdown, no greeting, no headline, no sign-off. Write in the language the instructions ask for; without one, in the language of the workspace locale, else English.
        """;

    private async Task<(string? Text, string? Error)> AiTextAsync(
        string name, string heading, IReadOnlyList<NotificationSection> sections, string? instructions, WorkspaceSettings.Values workspace, CancellationToken ct)
    {
        if (_ai is null)
            return (null, "AI is not available here.");
        var message = new StringBuilder()
            .Append("Report: ").AppendLine(name)
            .AppendLine(heading)
            .Append("Workspace locale: ").AppendLine(workspace.Locale ?? "not set");
        if (instructions is not null)
            message.Append("Instructions from the person who set up the report: ").AppendLine(instructions);
        message.AppendLine().AppendLine("The figures:").Append(NotificationSender.SectionsText(sections));
        // No tools and no user: the AI sees what the report says and nothing else.
        var caller = new McpCaller(null, "report", null, Array.Empty<string>(), new BentoSelfClient.Credentials(null, null));
        try
        {
            var result = await _ai.RunAsync("report", caller, AiSystem, message.ToString(), [], ct);
            return (result.Text.Trim(), null);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger?.LogInformation("The report {Report} goes out without AI text: {Reason}", name, e.Message);
            return (null, e.Message);
        }
    }

    private static ApiValidationException Invalid(string field, string message) =>
        new(message, new Dictionary<string, object?> { ["field"] = field });
}
