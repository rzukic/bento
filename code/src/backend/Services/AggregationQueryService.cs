using System.Globalization;
using System.Text;
using System.Text.Json;
using Dapper;
using Bento.Api.Db;
using Bento.Api.Db.Schema;
using Bento.Api.Exceptions;
using Bento.Api.Formula;
using Bento.Api.Models;

namespace Bento.Api.Services;

/// <summary>
/// Runs aggregation queries (Part 6 R6.9): filters with the list endpoint's
/// semantics, groups by columns, referenced records or date buckets, and
/// measures — including running totals over the time bucket. The SQL is
/// generated per provider; results come back in one shape on SQLite and
/// Postgres (dates as <c>yyyy-MM-dd</c>, numbers without float noise).
/// </summary>
public sealed class AggregationQueryService
{
    public const int DefaultLimit = 1000;
    public const int MaxLimit = 10000;

    private static readonly HashSet<string> Buckets = new(StringComparer.Ordinal) { "day", "week", "month", "quarter", "year" };
    private static readonly HashSet<string> Functions = new(StringComparer.Ordinal) { "count", "sum", "avg", "min", "max", "count_distinct", "median", "percentile" };
    private static readonly HashSet<string> FilterOperators = new(StringComparer.Ordinal)
        { "eq", "neq", "gt", "gte", "lt", "lte", "like", "in", "null", "notnull" };

    private readonly SchemaCache _schemaCache;
    private readonly QueryService _queryService;
    private readonly DatabaseConfig _dbConfig;
    private readonly ComputedColumns _computed;
    private readonly QueryIndexes _indexes;
    private readonly ProblemLog _problems;
    private readonly IHttpContextAccessor _http;

    private static readonly JsonSerializerOptions ProblemJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public AggregationQueryService(SchemaCache schemaCache, QueryService queryService, DatabaseConfig dbConfig, ComputedColumns computed, QueryIndexes indexes,
        ProblemLog problems, IHttpContextAccessor http)
    {
        _indexes = indexes;
        _problems = problems;
        _http = http;
        _schemaCache = schemaCache;
        _queryService = queryService;
        _dbConfig = dbConfig;
        _computed = computed;
    }

    private bool IsPostgres => _dbConfig.Provider == DatabaseProvider.Postgres;

    // ── Resolved query ───────────────────────────────────────────────────

    /// <param name="Top">Top values: only the first N values by the first measure (<paramref name="Ascending"/> for smallest first, <paramref name="ByAmount"/> for the largest amounts of either sign).</param>
    /// <param name="OtherOutput">Top values with "Other": the flag column marking the remaining values' group.</param>
    private sealed record Group(
        ColumnDef Column, string? Bucket, string Output, string Expression, string? LabelOutput, string? LabelExpression, string? Join,
        int? Top = null, bool Ascending = false, string? OtherOutput = null, bool ByAmount = false);

    public const int MaxTop = 1000;

    /// <param name="Fn">count, sum, … or "formula" (then <paramref name="Type"/> is the formula's result).</param>
    /// <param name="Custom">A CUSTOM(…) formula (Step 4.1): its arguments are selected as hidden columns and the function runs on each result row.</param>
    /// <param name="Series">XIRR/IRR/TWR/REBASE (Step 4.3): the parts are selected as hidden columns and the return computed afterwards.</param>
    private sealed record Measure(string Output, string Fn, ColumnDef? Column, bool Running, bool CarryIn, string Expression, FormulaType? Type = null, CustomCall? Custom = null, SeriesCall? Series = null)
    {
        /// <summary>ROLLING_SUM / ROLLING_AVG: a window over the last periods instead of all before (with Running).</summary>
        public RollingWindow? Rolling { get; init; }
        public IReadOnlyList<IndexTarget> Indexes { get; init; } = [];
        public IReadOnlyList<DateColumn> PeriodDates { get; init; } = [];
    }

    /// <summary>One row per period: label (yyyy-MM-dd), first and last day.</summary>
    /// <param name="Base">The first period is only a base for TWR/REBASE (the end of the period before the range) and is dropped from the result.</param>
    private sealed record Calendar(string Bucket, string Output, IReadOnlyList<(DateOnly Start, DateOnly End)> Periods, bool Base = false)
    {
        /// <summary>Set for "all time" (no asOf.from): the last day; the first comes from the data before the query runs.</summary>
        public DateOnly? OpenUntil { get; init; }
    }

    private sealed record Plan(
        TableDef Table,
        IReadOnlyDictionary<string, ComputedColumn> Computed,
        Calendar? Calendar,
        List<FilterClause> Filters,
        List<Group> Groups,
        List<Measure> Measures,
        List<(string Output, bool Descending)> Sort,
        int Limit);

    /// <summary>Checks a query against the schema without running it (saved queries are validated on save).</summary>
    public void Validate(AggregationQueryRequest request) => Resolve(request, null, null);

    public async Task<AggregationQueryResponse> RunAsync(AggregationQueryRequest request, JsonElement? extraFilter = null)
        => await RunAsync(request, extraFilter, null);

    private async Task<AggregationQueryResponse> RunAsync(
        AggregationQueryRequest request,
        JsonElement? extraFilter,
        IReadOnlyDictionary<string, FormulaValue>? variables)
    {
        var plan = Resolve(request, extraFilter, variables);
        // For the problem log: the query this request ran, if it fails or times out.
        var json = ProblemQueryJson(request, extraFilter, variables);
        if (_http.HttpContext is { } context)
            context.Items[ProblemLog.QueryItem] = json;
        await _indexes.EnsureAsync(plan.Measures.SelectMany(m => m.Indexes).Concat(plan.Computed.Values.SelectMany(c => c.Indexes)));
        await using var scope = await _queryService.UseDataAsync();
        using var limit = SqliteQueryLimit.Watch(scope.Db.Connection, Aborted, TimeSpan.FromSeconds(_dbConfig.QueryTimeoutSeconds));
        try
        {
            return await RunInScopeAsync(plan, json, scope);
        }
        catch (Exception e) when (limit.Translate(e) is { } translated)
        {
            throw translated;
        }
    }

    private async Task<AggregationQueryResponse> RunInScopeAsync(Plan plan, string json, QueryScope scope)
    {
        var startWarning = default(string);
        if (plan.Calendar is { OpenUntil: not null })
            (plan, startWarning) = await WithFirstPeriodAsync(plan, scope);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var (sql, parameters) = BuildSql(plan, scope);
        var rows = (await scope.Db.Connection.QueryAsync(new CommandDefinition(sql, parameters, cancellationToken: Aborted)))
            .Cast<IDictionary<string, object?>>()
            .ToList();
        if (watch.ElapsedMilliseconds > _problems.SlowMs)
            await _problems.RecordAsync("slow", $"Query on {plan.Table.Name} took {watch.ElapsedMilliseconds / 1000.0:0.0} s ({rows.Count} rows).",
                $"{json}\n\nSQL:\n{sql}", watch.ElapsedMilliseconds);

        var truncated = rows.Count > plan.Limit;
        if (truncated)
            rows = rows.Take(plan.Limit).ToList();

        var carryIns = plan.Measures.Where(m => m.CarryIn).ToList();
        Dictionary<string, Dictionary<string, decimal>>? offsets = null;
        if (carryIns.Count > 0)
            offsets = await LoadCarryInAsync(plan, carryIns, scope);

        var warnings = RunCustomMeasures(plan, rows);
        warnings.AddRange(RunSeriesMeasures(plan, rows));
        if (startWarning is not null)
            warnings.Add(startWarning);
        if (plan.Calendar is { Base: true } calendar)
        {
            var label = calendar.Periods[0].Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            rows = rows.Where(r => Convert.ToString(r[calendar.Output], CultureInfo.InvariantCulture) != label).ToList();
        }
        var data = rows.Select(row => Shape(plan, row, offsets)).ToList();
        if (plan.Sort.Any(s => plan.Measures.Any(m => (m.Custom is not null || m.Series is not null) && m.Output == s.Output)))
            data = SortInMemory(plan, data);
        return new AggregationQueryResponse(data, new QueryResultMeta(Columns(plan), truncated, warnings.Count > 0 ? warnings : null));
    }

    /// <summary>Stops the database work when the browser no longer waits for the answer.</summary>
    private CancellationToken Aborted => _http.HttpContext?.RequestAborted ?? CancellationToken.None;

    private static string ProblemQueryJson(AggregationQueryRequest request, JsonElement? extraFilter, IReadOnlyDictionary<string, FormulaValue>? variables) =>
        JsonSerializer.Serialize(new { query = request, extraFilter, variables = variables?.ToDictionary(v => v.Key, v => v.Value.Value) }, ProblemJson);

    private Plan Resolve(AggregationQueryRequest request, JsonElement? extraFilter, IReadOnlyDictionary<string, FormulaValue>? variables)
    {
        if (string.IsNullOrWhiteSpace(request.Source))
            throw Invalid("source", "Choose a table to query.");
        var table = _schemaCache.GetTable(request.Source.Trim())
            ?? throw new ApiNotFoundException($"Table '{request.Source}' does not exist.",
                new Dictionary<string, object?> { ["field"] = "source", ["table"] = request.Source });

        // Computed columns take part like stored ones, typed by their result.
        var source = table;
        var computed = _computed.For(table);
        table = table with
        {
            Columns = table.Columns
                .Select(c => computed.TryGetValue(c.Name, out var formula) ? c with { Type = formula.ValueType } : c)
                .ToList()
        };

        var filters = ParseFilter(request.Filter, table, "filter");
        filters.AddRange(ParseFilter(extraFilter, table, "filter"));

        var outputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var groups = new List<Group>();
        var calendar = request.AsOf is null ? null : ResolveCalendar(request.AsOf, outputs);
        if (calendar is not null)
        {
            var period = new ColumnDef { Name = calendar.Output, Type = ColumnType.Date, TypeSpecified = true };
            groups.Add(new Group(period, calendar.Bucket, calendar.Output, $"b.{Q("label")}", null, null, null));
        }
        var context = new FormulaContext
        {
            BucketAlias = calendar is null ? null : "b",
            Variables = variables ?? ResolveVariables(request.Variables),
            Functions = _computed.Functions
        };
        var joinIndex = 0;
        foreach (var (spec, index) in (request.GroupBy ?? []).Select((g, i) => (g, i)))
        {
            var path = $"groupBy[{index}]";
            var column = RequireColumn(table, spec.Column, $"{path}.column");
            var bucket = spec.Bucket?.Trim().ToLowerInvariant();
            if (!string.IsNullOrEmpty(bucket))
            {
                if (!Buckets.Contains(bucket))
                    throw Invalid($"{path}.bucket", $"Unknown bucket '{spec.Bucket}'; use day, week, month, quarter or year.");
                if (column.Type is not (ColumnType.Date or ColumnType.DateTime))
                    throw Invalid($"{path}.bucket", $"'{column.Name}' is not a date column; only dates can be grouped by {bucket}.");
            }
            else
            {
                bucket = null;
            }

            var output = Output(spec.As ?? column.Name, outputs, $"{path}.as");
            string? labelOutput = null, labelExpression = null, join = null;
            if (bucket is null && column.Type == ColumnType.Reference && column.References is not null &&
                _schemaCache.GetTable(column.References) is { } target &&
                target.Columns.FirstOrDefault(c => c.PrimaryKey) is { } targetKey &&
                CrudService.GetRecordLabelColumns(target).FirstOrDefault() is { } labelColumn)
            {
                var alias = $"r{joinIndex++}";
                labelOutput = Output($"{output}_label", outputs, $"{path}.as");
                labelExpression = $"{alias}.{Q(labelColumn)}";
                join = $"LEFT JOIN {Q(target.Name)} {alias} ON {alias}.{Q(targetKey.Name)} = t.{Q(column.Name)}";
            }

            var expression = bucket is null ? $"t.{Q(column.Name)}" : BucketExpression(column, bucket, computed.ContainsKey(column.Name));

            int? top = null;
            string? otherOutput = null;
            var ascending = false;
            var byAmount = false;
            if (spec.Top is { } n)
            {
                if (bucket is not null)
                    throw Invalid($"{path}.top", "Top values work on columns, not on date periods.");
                if (calendar is not null)
                    throw Invalid($"{path}.top", "Top values cannot be combined with values at the end of each period.");
                if (n < 1 || n > MaxTop)
                    throw Invalid($"{path}.top", $"top must be between 1 and {MaxTop}.");
                var rank = spec.Rank?.Trim().ToLowerInvariant();
                if (rank is not (null or "" or "asc" or "desc" or "abs"))
                    throw Invalid($"{path}.rank", "rank is \"desc\" (largest first), \"asc\" (smallest first) or \"abs\" (largest amount first, income and expenses alike).");
                top = n;
                ascending = rank == "asc";
                byAmount = rank == "abs";
                if (spec.Other)
                    otherOutput = Output($"{output}_other", outputs, $"{path}.as");
            }
            else if (spec.Other)
            {
                throw Invalid($"{path}.other", "\"other\" needs top: the values after the top ones form the other group.");
            }
            groups.Add(new Group(column, bucket, output, expression, labelOutput, labelExpression, join, top, ascending, otherOutput, byAmount));
        }

        var measures = new List<Measure>();
        foreach (var (spec, index) in (request.Measures ?? []).Select((m, i) => (m, i)))
        {
            var path = $"measures[{index}]";
            if (!string.IsNullOrWhiteSpace(spec.Formula))
            {
                measures.Add(FormulaMeasure(spec, path, source, context, groups, outputs));
                continue;
            }
            if (calendar is not null)
                throw Invalid($"{path}.formula", "Values at the end of each period need a formula, e.g. SUM(amount WHERE booked_on <= BUCKET_END()).");

            var fn = spec.Fn?.Trim().ToLowerInvariant() ?? "";
            if (!Functions.Contains(fn))
                throw Invalid($"{path}.fn", $"Unknown function '{spec.Fn}'; use count, sum, avg, min, max, count_distinct, median or percentile, or a formula.");

            ColumnDef? column = null;
            if (fn != "count" || !string.IsNullOrWhiteSpace(spec.Column))
                column = RequireColumn(table, spec.Column, $"{path}.column");
            if (fn is "sum" or "avg" or "median" or "percentile" && column!.Type is not (ColumnType.Integer or ColumnType.Float or ColumnType.Currency))
                throw Invalid($"{path}.column", $"'{column.Name}' is not a number; {fn} needs a number column.");
            if (fn == "percentile" && spec.Percentile is not (>= 0 and <= 100))
                throw Invalid($"{path}.percentile", "percentile needs a number from 0 to 100, e.g. 90.");
            if (fn != "percentile" && spec.Percentile is not null)
                throw Invalid($"{path}.percentile", "percentile only applies to fn percentile.");
            if (fn is "min" or "max" && column!.Type is ColumnType.Json or ColumnType.Boolean)
                throw Invalid($"{path}.column", $"'{column.Name}' has no order; {fn} needs numbers, dates or text.");

            if (spec.Running)
            {
                if (fn is not ("sum" or "count"))
                    throw Invalid($"{path}.running", "Running totals work with sum and count.");
                if (groups.Count(g => g.Bucket is not null) != 1)
                    throw Invalid($"{path}.running", "Running totals need exactly one date group (day, week, month, quarter or year).");
            }
            if (spec.CarryIn && !spec.Running)
                throw Invalid($"{path}.carryIn", "carryIn only applies to running totals.");

            var output = Output(spec.Name ?? $"{(fn == "percentile" ? $"p{spec.Percentile:0.##}" : fn)}_{column?.Name ?? "rows"}", outputs, $"{path}.name");
            var expression = fn switch
            {
                "count" => column is null ? "COUNT(*)" : $"COUNT(t.{Q(column.Name)})",
                "count_distinct" => $"COUNT(DISTINCT t.{Q(column!.Name)})",
                "median" => FormulaCompiler.PercentileSql($"t.{Q(column!.Name)}", 0.5, IsPostgres),
                "percentile" => FormulaCompiler.PercentileSql($"t.{Q(column!.Name)}", spec.Percentile!.Value / 100, IsPostgres),
                _ => $"{fn.ToUpperInvariant()}(t.{Q(column!.Name)})"
            };
            measures.Add(new Measure(output, fn, column, spec.Running, spec.CarryIn, expression));
        }

        if (groups.Count == 0 && measures.Count == 0)
            throw Invalid("measures", "Add a measure or a group.");
        if (groups.Any(g => g.Top is not null) && measures.Any(m => m.CarryIn))
            throw Invalid("measures", "Running totals that include earlier rows cannot be combined with top values.");
        if (groups.Any(g => g.Top is not null) && measures.FirstOrDefault() is { } ranked && (ranked.Custom is not null || ranked.Series is not null))
            throw Invalid("measures[0].formula", "Top values are ranked by the first measure, which cannot be a CUSTOM(…) value; put another measure first.");

        var sort = new List<(string, bool)>();
        foreach (var (entry, index) in (request.Sort ?? []).Select((s, i) => (s, i)))
        {
            var descending = entry.StartsWith('-');
            var name = descending ? entry[1..] : entry;
            if (!outputs.Contains(name))
                throw Invalid($"sort[{index}]", $"'{name}' is not a group or measure of this query.");
            sort.Add((outputs.First(o => string.Equals(o, name, StringComparison.OrdinalIgnoreCase)), descending));
        }
        if (sort.Count == 0)
            sort.AddRange(groups.Select(g => (g.Output, false)));

        // A series over time is cut from its end (the newest periods): give it all rows unless asked otherwise.
        var limit = request.Limit ?? (groups.Any(g => g.Bucket is not null) ? MaxLimit : DefaultLimit);
        if (limit < 1 || limit > MaxLimit)
            throw Invalid("limit", $"limit must be between 1 and {MaxLimit}.");

        // TWR and REBASE start from the end of the period before the range: compute it, then drop it.
        if (calendar is not null && measures.Any(m => m.Series?.Kind is "twr" or "rebase"))
            calendar = WithBasePeriod(calendar);

        return new Plan(table, computed, calendar, filters, groups, measures, sort, limit);
    }

    /// <summary>A measure given as a formula over the group's rows.</summary>
    private Measure FormulaMeasure(QueryMeasure spec, string path, TableDef source, FormulaContext context, List<Group> groups, HashSet<string> outputs)
    {
        var schema = FormulaSchema.From(_schemaCache.Current);
        var compiled = FormulaCompiler.CompileMeasure(spec.Formula!, source, schema, _dbConfig.Provider, "t", context);
        var (check, running) = compiled;
        var rolling = compiled.Rolling;
        if (!check.IsValid)
        {
            var error = check.Errors[0];
            throw new ApiValidationException($"{error.Message}", new Dictionary<string, object?>
            {
                ["field"] = $"{path}.formula",
                ["errors"] = check.Errors.Select(e => new FormulaErrorResponse(e.Message, e.Start, e.End)).ToList()
            });
        }

        if (rolling is not null)
        {
            if (spec.Running || spec.CarryIn)
                throw Invalid($"{path}.running", "A rolling window is not also a running total; use one of them.");
            if (groups.Count(g => g.Bucket is not null) != 1)
                throw Invalid($"{path}.formula", "Rolling windows need exactly one date group (day, week, month, quarter or year).");
        }
        running |= spec.Running || rolling is not null;
        var custom = check.Result!.Custom;
        if (custom is not null && (running || spec.CarryIn))
            throw Invalid($"{path}.running", "A CUSTOM(…) value cannot be a running total; add up its inputs instead.");
        var series = check.Result.Series;
        if (series is not null)
        {
            if (running || spec.CarryIn)
                throw Invalid($"{path}.running", "A return cannot be a running total; TWR already accumulates over the periods.");
            // Values at the end of each period group by the calendar's period, a date group too.
            var dateGroups = groups.Count(g => g.Bucket is not null);
            if (series.Kind == "twr" && context.BucketAlias is null)
                throw Invalid($"{path}.formula", "TWR needs values at the end of each period (month, quarter, year).");
            if (series.Kind is "twr" or "rebase" && dateGroups != 1)
                throw Invalid($"{path}.formula", $"{series.Kind.ToUpperInvariant()} runs over exactly one date group (day, week, month, quarter or year).");
        }
        if (running && groups.Count(g => g.Bucket is not null) != 1)
            throw Invalid($"{path}.running", "Running totals need exactly one date group (day, week, month, quarter or year).");
        if (spec.CarryIn && !running)
            throw Invalid($"{path}.carryIn", "carryIn only applies to running totals.");
        if (spec.CarryIn && context.BucketAlias is not null)
            throw Invalid($"{path}.carryIn", "Values at the end of each period already include everything before.");

        var output = Output(spec.Name ?? $"formula_{outputs.Count + 1}", outputs, $"{path}.name");
        // An average of currency stays currency; of anything else it is a number.
        var type = rolling is { Average: true } && check.Result.Type != FormulaType.Currency ? FormulaType.Number : check.Result.Type;
        return new Measure(output, "formula", null, running, spec.CarryIn, check.Result!.Sql, type, custom, series) { Indexes = check.Result.Indexes, PeriodDates = check.Result.PeriodDates, Rolling = rolling };
    }

    // Daily values over 13 years. The work grows with periods × rows; the query
    // timeout, cancellation and the problem log guard what is still too much.
    private const int MaxPeriods = 5000;

    private static Calendar ResolveCalendar(QueryAsOf asOf, HashSet<string> outputs)
    {
        var bucket = asOf.Bucket?.Trim().ToLowerInvariant();
        if (bucket is null || !Buckets.Contains(bucket))
            throw Invalid("asOf.bucket", "Choose the period: day, week, month, quarter or year.");
        if (!DateOnly.TryParseExact(asOf.To?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var to))
            throw Invalid("asOf.to", "asOf.to must be a date (yyyy-MM-dd).");
        var output = Output(asOf.As ?? "period", outputs, "asOf.as");
        // Without a first day ("all time") the series begins where the data does (WithFirstPeriodAsync).
        if (string.IsNullOrWhiteSpace(asOf.From))
            return new Calendar(bucket, output, Periods(bucket, to, to)) { OpenUntil = to };
        if (!DateOnly.TryParseExact(asOf.From.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var from))
            throw Invalid("asOf.from", "asOf.from must be a date (yyyy-MM-dd).");
        if (to < from)
            throw Invalid("asOf.to", "asOf.to is before asOf.from.");
        var periods = Periods(bucket, from, to);
        if (periods.Count > MaxPeriods)
            throw Invalid("asOf", $"At most {MaxPeriods} periods; choose a longer period or a shorter range.");
        return new Calendar(bucket, output, periods);
    }

    /// <summary>The periods from the one holding <paramref name="from"/> to the one holding <paramref name="to"/>; at most one past <see cref="MaxPeriods"/>.</summary>
    private static List<(DateOnly Start, DateOnly End)> Periods(string bucket, DateOnly from, DateOnly to)
    {
        DateOnly Start(DateOnly day) => bucket switch
        {
            "day" => day,
            "week" => day.AddDays(-(((int)day.DayOfWeek + 6) % 7)),
            "month" => new DateOnly(day.Year, day.Month, 1),
            "quarter" => new DateOnly(day.Year, (day.Month - 1) / 3 * 3 + 1, 1),
            _ => new DateOnly(day.Year, 1, 1)
        };
        DateOnly Next(DateOnly start) => bucket switch
        {
            "day" => start.AddDays(1),
            "week" => start.AddDays(7),
            "month" => start.AddMonths(1),
            "quarter" => start.AddMonths(3),
            _ => start.AddYears(1)
        };

        // Counted from the end: "all time" keeps the newest periods.
        var periods = new List<(DateOnly Start, DateOnly End)>();
        for (var start = Start(to); start >= Start(from) && periods.Count <= MaxPeriods; start = Previous(start))
            periods.Add((start, Next(start).AddDays(-1)));
        periods.Reverse();
        return periods;

        DateOnly Previous(DateOnly start) => bucket switch
        {
            "day" => start.AddDays(-1),
            "week" => start.AddDays(-7),
            "month" => start.AddMonths(-1),
            "quarter" => start.AddMonths(-3),
            _ => start.AddYears(-1)
        };
    }

    /// <summary>
    /// "All time": the series begins with the earliest date its formulas compare
    /// with the period (<c>trades.date &lt;= BUCKET_END()</c>: the first trade), or
    /// a year before the end when they compare none. Over <see cref="MaxPeriods"/>
    /// periods it keeps the newest, and says so.
    /// </summary>
    private async Task<(Plan Plan, string? Warning)> WithFirstPeriodAsync(Plan plan, QueryScope scope)
    {
        var calendar = plan.Calendar!;
        var to = calendar.OpenUntil!.Value;
        DateOnly? first = null;
        foreach (var dated in plan.Measures.SelectMany(m => m.PeriodDates).Distinct())
        {
            if (_schemaCache.GetTable(dated.Table) is null)
                continue;
            var value = await scope.Db.Connection.ExecuteScalarAsync<object?>(new CommandDefinition(
                $"SELECT MIN({Q(dated.Column)}) FROM {Q(dated.Table)}", cancellationToken: Aborted));
            var day = value switch
            {
                DateTime time => DateOnly.FromDateTime(time),
                DateOnly date => date,
                DateTimeOffset offset => DateOnly.FromDateTime(offset.UtcDateTime),
                string text when text.Length >= 10 && DateOnly.TryParseExact(text[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) => parsed,
                _ => (DateOnly?)null
            };
            if (day is not null && (first is null || day < first))
                first = day;
        }

        var from = first ?? new DateOnly(to.Year - 1, to.Month, 1);
        var periods = Periods(calendar.Bucket, from > to ? to : from, to);
        string? warning = null;
        if (periods.Count > MaxPeriods)
        {
            periods = periods.Skip(periods.Count - MaxPeriods).ToList();
            warning = $"All time has more than {MaxPeriods} periods; this shows the newest {MaxPeriods}. Choose a longer period to see the start.";
        }
        var open = calendar with { Periods = periods, Base = false, OpenUntil = null };
        return (plan with { Calendar = calendar.Base ? WithBasePeriod(open) : open }, warning);
    }

    /// <summary>Variables sent with a query: numbers, text and yes/no as formula constants.</summary>
    internal static IReadOnlyDictionary<string, FormulaValue>? ResolveVariables(Dictionary<string, JsonElement>? variables)
    {
        if (variables is null || variables.Count == 0)
            return null;
        var result = new Dictionary<string, FormulaValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in variables)
        {
            result[name] = value.ValueKind switch
            {
                JsonValueKind.Number when value.TryGetInt64(out var whole) => new FormulaValue(FormulaType.Integer, whole),
                JsonValueKind.Number => new FormulaValue(FormulaType.Number, value.GetDecimal()),
                JsonValueKind.True or JsonValueKind.False => new FormulaValue(FormulaType.Boolean, value.GetBoolean()),
                JsonValueKind.String => new FormulaValue(FormulaType.Text, value.GetString()),
                JsonValueKind.Null => new FormulaValue(FormulaType.Null, null),
                _ => throw Invalid($"variables.{name}", "A variable is a number, text, yes/no or empty.")
            };
        }
        return result;
    }

    /// <summary>
    /// The periods as a derived table b(label, bucket, bucket_end), typed per provider.
    /// One VALUES list: SQLite allows at most 500 SELECTs joined by UNION ALL.
    /// </summary>
    private string CalendarSql(Calendar calendar)
    {
        string Day(DateOnly day) => IsPostgres ? $"DATE '{day:yyyy-MM-dd}'" : $"'{day:yyyy-MM-dd}'";
        var rows = string.Join(", ", calendar.Periods.Select(p => $"('{p.Start:yyyy-MM-dd}', {Day(p.Start)}, {Day(p.End)})"));
        return $"SELECT column1 AS {Q("label")}, column2 AS {Q("bucket")}, column3 AS {Q("bucket_end")} FROM (VALUES {rows}) v";
    }

    // ── Dashboard variables ─────────────────────────────────────────────

    public const int MaxVariables = 50;

    /// <summary>
    /// Evaluates dashboard variables (Part 9 R9.10) in dependency order: a
    /// variable with a source table is a measure over its rows (no groups),
    /// one without is a formula over other variables (<c>cash / burn_rate</c>).
    /// A broken variable gets an error; the others still get values.
    /// </summary>
    public async Task<IReadOnlyList<QueryVariableResult>> EvaluateVariablesAsync(QueryVariablesRequest request)
    {
        var definitions = request.Variables ?? [];
        if (definitions.Count > MaxVariables)
            throw Invalid("variables", $"A dashboard has at most {MaxVariables} variables.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (definition, index) in definitions.Select((d, i) => (d, i)))
        {
            var name = definition.Name?.Trim() ?? "";
            if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_]*$"))
                throw Invalid($"variables[{index}].name", "A variable name uses letters, digits and _ and starts with a letter, e.g. burn_rate.");
            if (!names.Add(name))
                throw Invalid($"variables[{index}].name", $"The variable '{name}' appears twice.");
        }

        // Which variables each formula uses, from its syntax tree.
        var uses = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var syntaxErrors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in definitions)
        {
            var name = definition.Name!.Trim();
            try
            {
                var root = FormulaParser.Parse(definition.Formula ?? "");
                uses[name] = FormulaNodes.Names(root).Where(n => names.Contains(n) && !string.Equals(n, name, StringComparison.OrdinalIgnoreCase)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (FormulaNodes.Names(root).Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) && string.IsNullOrWhiteSpace(definition.Source))
                    syntaxErrors[name] = $"Circular reference: {name} → {name}.";
            }
            catch (FormulaSyntaxException e)
            {
                uses[name] = [];
                syntaxErrors[name] = e.Error.Message;
            }
        }

        var values = new Dictionary<string, FormulaValue>(StringComparer.OrdinalIgnoreCase);
        var results = new Dictionary<string, QueryVariableResult>(StringComparer.OrdinalIgnoreCase);
        var pending = definitions.Select(d => d.Name!.Trim()).ToList();
        while (pending.Count > 0)
        {
            var ready = pending.FirstOrDefault(n => uses[n].All(results.ContainsKey));
            if (ready is null)
            {
                foreach (var name in pending)
                    results[name] = new QueryVariableResult(name, null, null, $"Circular reference between {string.Join(", ", pending)}.");
                break;
            }
            pending.Remove(ready);
            var definition = definitions.First(d => string.Equals(d.Name!.Trim(), ready, StringComparison.OrdinalIgnoreCase));

            var broken = uses[ready].FirstOrDefault(n => results[n].Error is not null);
            if (syntaxErrors.TryGetValue(ready, out var syntax))
                results[ready] = new QueryVariableResult(ready, null, null, syntax);
            else if (broken is not null)
                results[ready] = new QueryVariableResult(ready, null, null, $"'{broken}' has an error.");
            else
                results[ready] = await EvaluateVariableAsync(ready, definition, values);

            if (results[ready] is { Error: null } ok && FormulaTypes.FromName(ok.Type) is { } type)
                values[ready] = new FormulaValue(type, ok.Value is null ? null : ok.Value switch
                {
                    decimal or long or bool or string => ok.Value,
                    _ => System.Convert.ToString(ok.Value, CultureInfo.InvariantCulture)
                });
        }
        return definitions.Select(d => results[d.Name!.Trim()]).ToList();
    }

    private async Task<QueryVariableResult> EvaluateVariableAsync(string name, QueryVariable definition, IReadOnlyDictionary<string, FormulaValue> values)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(definition.Source))
            {
                var result = await RunAsync(new AggregationQueryRequest
                {
                    Source = definition.Source,
                    Filter = definition.Filter,
                    Measures = [new QueryMeasure { Name = "value", Formula = definition.Formula }]
                }, null, values);
                var column = result.Meta.Columns.Single();
                if (result.Meta.Warnings is [var warning, ..])
                    return new QueryVariableResult(name, null, null, warning);
                return new QueryVariableResult(name, column.Type, result.Data.FirstOrDefault()?["value"], null);
            }

            var table = new TableDef { Name = "__variables", DisplayName = "the dashboard variables" };
            var check = FormulaCompiler.Compile(definition.Formula ?? "", table, FormulaSchema.From(_schemaCache.Current), _dbConfig.Provider,
                context: new FormulaContext { Variables = values, Functions = _computed.Functions });
            if (!check.IsValid)
                return new QueryVariableResult(name, null, null, check.Errors[0].Message);

            await _indexes.EnsureAsync(check.Result!.Indexes);
            await using var scope = await _queryService.UseDataAsync();
            using var limit = SqliteQueryLimit.Watch(scope.Db.Connection, Aborted, TimeSpan.FromSeconds(_dbConfig.QueryTimeoutSeconds));
            try
            {
                if (check.Result!.Custom is { } custom)
                {
                    var row = custom.Arguments.Count == 0
                        ? new Dictionary<string, object?>()
                        : (IDictionary<string, object?>)(await scope.Db.Connection.QuerySingleAsync(new CommandDefinition(
                            $"SELECT {string.Join(", ", custom.Arguments.Select((a, k) => $"{a.Sql} AS {Q($"__a{k}")}"))}", cancellationToken: Aborted)));
                    var (function, syntax) = Formula.Sandbox.JsFunction.Create(custom.Function);
                    using (function)
                    {
                        var result = function?.Call(custom.Arguments.Select((a, k) => ResultValues.Shape(row[$"__a{k}"], a.Type)).ToList());
                        return result is { Ok: true }
                            ? new QueryVariableResult(name, FormulaTypes.Name(check.Result.Type), ResultValues.Shape(result.Value, check.Result.Type), null)
                            : new QueryVariableResult(name, null, null, result?.Error ?? syntax);
                    }
                }
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var raw = await scope.Db.Connection.ExecuteScalarAsync(new CommandDefinition($"SELECT {check.Result!.Sql} AS {Q("value")}", cancellationToken: Aborted));
                if (watch.ElapsedMilliseconds > _problems.SlowMs)
                    await _problems.RecordAsync("slow", $"Variable {name} took {watch.ElapsedMilliseconds / 1000.0:0.0} s.",
                        JsonSerializer.Serialize(new { variable = name, formula = definition.Formula }, ProblemJson), watch.ElapsedMilliseconds);
                return new QueryVariableResult(name, FormulaTypes.Name(check.Result.Type), ResultValues.Shape(raw, check.Result.Type), null);
            }
            catch (Exception e) when (limit.Translate(e) is { } translated)
            {
                throw translated;
            }
        }
        catch (ApiException e)
        {
            return new QueryVariableResult(name, null, null, e.Message);
        }
        catch (System.Data.Common.DbException e)
        {
            return new QueryVariableResult(name, null, null, $"The formula could not be computed: {e.Message}");
        }
    }

    private static ColumnDef RequireColumn(TableDef table, string? name, string path)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw Invalid(path, "Choose a column.");
        var column = table.Columns.FirstOrDefault(c => string.Equals(c.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (column is null || !ColumnTypes.HasPhysicalColumn(column.Type))
            throw Invalid(path, $"Table '{table.Name}' has no column '{name}'.");
        return column;
    }

    private static string Output(string name, HashSet<string> outputs, string path)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith("__", StringComparison.Ordinal))
            throw Invalid(path, "Give the result column a name.");
        if (!outputs.Add(trimmed))
            throw Invalid(path, $"The result column '{trimmed}' appears twice; name one with \"as\" or \"name\".");
        return trimmed;
    }

    /// <summary>
    /// <c>{ "status": "paid", "amount": { "lt": 0 }, "category_id": { "in": [1, 2] } }</c>
    /// → list-endpoint filter clauses (AND).
    /// </summary>
    private static List<FilterClause> ParseFilter(JsonElement? filter, TableDef table, string path)
    {
        var clauses = new List<FilterClause>();
        if (filter is not { } element || element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return clauses;
        if (element.ValueKind != JsonValueKind.Object)
            throw Invalid(path, "filter must be an object of column conditions.");

        foreach (var property in element.EnumerateObject())
        {
            var column = RequireColumn(table, property.Name, $"{path}.{property.Name}");
            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                clauses.Add(property.Value.ValueKind == JsonValueKind.Null
                    ? new FilterClause(column.Name, "null", null)
                    : new FilterClause(column.Name, "eq", ScalarText(property.Value)));
                continue;
            }

            foreach (var condition in property.Value.EnumerateObject())
            {
                var op = condition.Name.ToLowerInvariant();
                if (!FilterOperators.Contains(op))
                    throw Invalid($"{path}.{property.Name}.{condition.Name}", $"Unknown operator '{condition.Name}'.");
                var value = op switch
                {
                    "null" or "notnull" => null,
                    "in" when condition.Value.ValueKind == JsonValueKind.Array =>
                        string.Join(",", condition.Value.EnumerateArray().Select(ScalarText)),
                    _ => ScalarText(condition.Value)
                };
                clauses.Add(new FilterClause(column.Name, op, value));
            }
        }
        return clauses;
    }

    private static string ScalarText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => value.GetRawText()
    };

    // ── SQL ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Date buckets as <c>yyyy-MM-dd</c> text of the bucket's first day.
    /// Values are bucketed as stored (wall time; values written with Z are
    /// UTC), never through a session time zone.
    /// </summary>
    private string BucketExpression(ColumnDef column, string bucket, bool computed)
    {
        var col = $"t.{Q(column.Name)}";
        if (IsPostgres)
        {
            // A computed date-time already is UTC wall time (timestamp without zone).
            var stamp = column.Type == ColumnType.DateTime && !computed ? $"({col} AT TIME ZONE 'UTC')" : $"CAST({col} AS TIMESTAMP)";
            return bucket == "day"
                ? $"to_char({stamp}, 'YYYY-MM-DD')"
                : $"to_char(date_trunc('{bucket}', {stamp}), 'YYYY-MM-DD')";
        }

        var day = $"substr({col}, 1, 10)";
        return bucket switch
        {
            "day" => day,
            "week" => $"date({day}, '-' || ((CAST(strftime('%w', {day}) AS INTEGER) + 6) % 7) || ' days')",
            "month" => $"(substr({col}, 1, 7) || '-01')",
            "quarter" => $"(substr({col}, 1, 4) || '-' || printf('%02d', ((CAST(substr({col}, 6, 2) AS INTEGER) - 1) / 3) * 3 + 1) || '-01')",
            _ => $"(substr({col}, 1, 4) || '-01-01')"
        };
    }

    private (string Sql, DynamicParameters Parameters) BuildSql(Plan plan, QueryScope scope)
    {
        var (inner, parameters) = CompileFiltered(plan, plan.Filters, scope);

        var select = new List<string>();
        var groupBy = new List<string>();
        var where = new List<string>();
        foreach (var group in plan.Groups)
        {
            var expression = group.Expression;
            var label = group.LabelExpression;
            if (group.Top is { } top)
            {
                // The first N values by the first measure over all filtered rows; empty values stay.
                var rank = plan.Measures.Count > 0 ? plan.Measures[0].Expression : "COUNT(*)";
                var direction = group.Ascending ? "ASC" : "DESC";
                if (group.ByAmount)
                    rank = $"ABS({rank})";
                var topValues =
                    $"SELECT t.{Q(group.Column.Name)} FROM ({inner}) t WHERE t.{Q(group.Column.Name)} IS NOT NULL " +
                    $"GROUP BY t.{Q(group.Column.Name)} " +
                    $"ORDER BY CASE WHEN {rank} IS NULL THEN 1 ELSE 0 END, {rank} {direction}, t.{Q(group.Column.Name)} LIMIT {top}";
                var kept = $"(t.{Q(group.Column.Name)} IS NULL OR t.{Q(group.Column.Name)} IN ({topValues}))";
                if (group.OtherOutput is null)
                {
                    where.Add(kept);
                }
                else
                {
                    expression = $"CASE WHEN {kept} THEN {expression} END";
                    if (label is not null)
                        label = $"CASE WHEN {kept} THEN {label} END";
                    var flag = $"CASE WHEN {kept} THEN 0 ELSE 1 END";
                    select.Add($"{flag} AS {Q(group.OtherOutput)}");
                    groupBy.Add(flag);
                }
            }

            select.Add($"{expression} AS {Q(group.Output)}");
            groupBy.Add(expression);
            if (label is not null)
            {
                select.Add($"{label} AS {Q(group.LabelOutput!)}");
                groupBy.Add(label);
            }
        }
        for (var i = 0; i < plan.Measures.Count; i++)
        {
            var measure = plan.Measures[i];
            select.Add($"{measure.Expression} AS {Q(measure.Running ? $"__r{i}" : measure.Output)}");
            if (measure.Custom is { } custom)
                select.AddRange(custom.Arguments.Select((a, k) => $"{a.Sql} AS {Q($"__f{i}_{k}")}"));
            if (measure.Series is { } series)
                select.AddRange(series.Parts.Select((p, k) => $"{p.Sql} AS {Q($"__s{i}_{k}")}"));
        }

        // The period's days are fixed per label; grouping by them lets measures use BUCKET_END() anywhere.
        if (plan.Calendar is not null)
            groupBy.AddRange([$"b.{Q("bucket")}", $"b.{Q("bucket_end")}"]);

        // Without groups the answer is one row. An aggregate makes it so even when no
        // measure adds up rows (a constant, a variable, CUSTOM("f", rate)): those ran
        // once per row before (a CUSTOM 291 times), or not at all on an empty table.
        // XIRR(amount, date) and the like read the rows themselves, so they keep them.
        if (groupBy.Count == 0 && plan.Measures.All(m => m.Series is null))
            select.Add($"COUNT(*) AS {Q("__rows")}");

        var grouped = new StringBuilder()
            .Append("SELECT ").Append(string.Join(", ", select))
            .Append(" FROM ");
        if (plan.Calendar is not null)
            grouped.Append('(').Append(CalendarSql(plan.Calendar)).Append(") b CROSS JOIN ");
        grouped.Append('(').Append(inner).Append(") t");
        foreach (var join in plan.Groups.Select(g => g.Join).Where(j => j is not null))
            grouped.Append(' ').Append(join);
        if (where.Count > 0)
            grouped.Append(" WHERE ").Append(string.Join(" AND ", where));
        if (groupBy.Count > 0)
            grouped.Append(" GROUP BY ").Append(string.Join(", ", groupBy));

        // Output names, not g.<name>: running totals exist only in this SELECT.
        var order = string.Join(", ", plan.Sort.Select(s => $"{Q(s.Output)} {(s.Descending ? "DESC" : "ASC")}"));
        var outer = new StringBuilder("SELECT g.*");
        if (plan.Measures.Any(m => m.Running))
        {
            var bucket = plan.Groups.Single(g => g.Bucket is not null);
            var partitions = plan.Groups.Where(g => g.Bucket is null)
                .SelectMany(g => g.OtherOutput is null ? [$"g.{Q(g.Output)}"] : new[] { $"g.{Q(g.Output)}", $"g.{Q(g.OtherOutput)}" })
                .ToList();
            string Over(RollingWindow? window) => (partitions.Count > 0 ? $"PARTITION BY {string.Join(", ", partitions)} " : "")
                + $"ORDER BY g.{Q(bucket.Output)} ROWS BETWEEN {(window is null ? "UNBOUNDED" : (window.Periods - 1).ToString(CultureInfo.InvariantCulture))} PRECEDING AND CURRENT ROW";
            for (var i = 0; i < plan.Measures.Count; i++)
            {
                var measure = plan.Measures[i];
                if (measure.Running)
                    outer.Append($", {(measure.Rolling is { Average: true } ? "AVG" : "SUM")}(g.{Q($"__r{i}")}) OVER ({Over(measure.Rolling)}) AS {Q(measure.Output)}");
            }
        }
        outer.Append(" FROM (").Append(grouped).Append(") g");
        if (order.Length > 0)
            outer.Append(" ORDER BY ").Append(order);
        outer.Append(" LIMIT ").Append(plan.Limit + 1);
        return (outer.ToString(), parameters);
    }

    /// <summary>The source rows with the list endpoint's filter semantics (coercion, providers).</summary>
    private (string Sql, DynamicParameters Parameters) CompileFiltered(Plan plan, IReadOnlyList<FilterClause> filters, QueryScope scope)
    {
        var table = _schemaCache.GetTable(plan.Table.Name) ?? plan.Table;
        var query = _computed.Select(new SqlKata.Query(table.Name), table);
        CrudService.ApplyFiltersStatic(query, filters, plan.Table, _dbConfig.Provider, plan.Computed);
        var compiled = scope.Db.Compiler.Compile(query);
        return (compiled.Sql, new DynamicParameters(compiled.NamedBindings));
    }

    /// <summary>
    /// Totals of the rows before the first bucket, per partition: the same
    /// filters with the lower bounds on the date column turned around.
    /// </summary>
    private async Task<Dictionary<string, Dictionary<string, decimal>>> LoadCarryInAsync(Plan plan, List<Measure> measures, QueryScope scope)
    {
        var bucketColumn = plan.Groups.Single(g => g.Bucket is not null).Column;
        var lowerBounds = plan.Filters
            .Where(f => string.Equals(f.Column, bucketColumn.Name, StringComparison.OrdinalIgnoreCase) && f.Operator is "gt" or "gte")
            .ToList();
        var result = measures.ToDictionary(m => m.Output, _ => new Dictionary<string, decimal>(StringComparer.Ordinal));
        if (lowerBounds.Count == 0)
            return result;

        var before = plan.Filters.Except(lowerBounds).ToList();
        before.AddRange(lowerBounds.Select(f => f with { Operator = f.Operator == "gte" ? "lt" : "lte" }));
        var (inner, parameters) = CompileFiltered(plan, before, scope);

        var partitions = plan.Groups.Where(g => g.Bucket is null).ToList();
        var select = partitions.Select(g => $"{g.Expression} AS {Q(g.Output)}")
            .Concat(measures.Select(m => $"{m.Expression} AS {Q(m.Output)}"));
        var sql = new StringBuilder("SELECT ").Append(string.Join(", ", select))
            .Append(" FROM (").Append(inner).Append(") t");
        if (partitions.Count > 0)
            sql.Append(" GROUP BY ").Append(string.Join(", ", partitions.Select(g => g.Expression)));

        foreach (IDictionary<string, object?> row in await scope.Db.Connection.QueryAsync(new CommandDefinition(sql.ToString(), parameters, cancellationToken: Aborted)))
        {
            var key = PartitionKey(partitions, row);
            foreach (var measure in measures)
                result[measure.Output][key] = ResultValues.ToDecimal(row[measure.Output]) ?? 0m;
        }
        return result;
    }

    private static string PartitionKey(List<Group> partitions, IDictionary<string, object?> row) =>
        string.Join("\u001F", partitions.Select(g => Convert.ToString(row.TryGetValue(g.Output, out var v) ? v : null, CultureInfo.InvariantCulture)));

    // ── Results ──────────────────────────────────────────────────────────

    /// <summary>Runs each CUSTOM measure's function on every result row; failures leave the value empty and add a warning.</summary>
    private static List<string> RunCustomMeasures(Plan plan, List<IDictionary<string, object?>> rows)
    {
        var warnings = new List<string>();
        for (var i = 0; i < plan.Measures.Count; i++)
        {
            if (plan.Measures[i] is not { Custom: { } custom } measure)
                continue;
            var (function, syntax) = Formula.Sandbox.JsFunction.Create(custom.Function);
            using (function)
            {
                var failed = 0;
                string? first = null;
                foreach (var row in rows)
                {
                    var arguments = custom.Arguments.Select((a, k) => ResultValues.Shape(row[$"__f{i}_{k}"], a.Type)).ToList();
                    var result = function?.Call(arguments);
                    row[measure.Output] = result is { Ok: true } ? result.Value : null;
                    if (result is not { Ok: true })
                    {
                        failed++;
                        first ??= result?.Error ?? syntax;
                    }
                }
                if (failed > 0)
                    warnings.Add($"{measure.Output}: {custom.Function.Name} failed on {failed} of {rows.Count} rows — {first}");
            }
        }
        return warnings;
    }

    /// <summary>
    /// XIRR/IRR per result row from its flows; TWR and REBASE along the date
    /// group, per combination of the other groups (Step 4.3).
    /// </summary>
    private static List<string> RunSeriesMeasures(Plan plan, List<IDictionary<string, object?>> rows)
    {
        var warnings = new List<string>();
        var date = plan.Groups.FirstOrDefault(g => g.Bucket is not null);
        var partitions = plan.Groups.Where(g => g != date).ToList();
        for (var i = 0; i < plan.Measures.Count; i++)
        {
            if (plan.Measures[i] is not { Series: { } series } measure)
                continue;
            double? Number(IDictionary<string, object?> row, int part) =>
                ResultValues.ToDecimal(row[$"__s{i}_{part}"]) is { } d ? (double)d : null;

            if (series.Kind is "xirr" or "irr")
            {
                var unsolved = 0;
                foreach (var row in rows)
                {
                    var flows = new List<CashFlow>();
                    for (var k = 0; k < series.Parts.Count; k++)
                    {
                        var part = series.Parts[k];
                        if (part.Kind == "flows")
                            flows.AddRange(Returns.ParseFlows(row[$"__s{i}_{k}"] as string));
                        else if (part.Kind == "amount" && Number(row, k) is { } amount && amount != 0 &&
                                 CustomFunctionValues.DateText(row[$"__s{i}_{k + 1}"]) is { } day)
                            flows.Add(new CashFlow(DateOnly.ParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture), amount));
                    }
                    var rate = series.Kind == "xirr" ? Returns.Xirr(flows) : Returns.Irr(flows);
                    row[measure.Output] = rate is { } r ? (decimal)Math.Round(r, 10) : null;
                    if (rate is null && flows.Count > 0)
                        unsolved++;
                }
                if (unsolved > 0)
                    warnings.Add($"{measure.Output}: no rate of return for {unsolved} of {rows.Count} rows — the flows need money paid out (negative) and received (positive).");
                continue;
            }

            foreach (var group in rows.GroupBy(r => string.Join("\u001F", partitions.Select(g => Convert.ToString(r[g.Output], CultureInfo.InvariantCulture)))))
            {
                var ordered = group.OrderBy(r => Convert.ToString(r[date!.Output], CultureInfo.InvariantCulture), StringComparer.Ordinal).ToList();
                double?[] values = series.Kind == "twr"
                    ? Returns.Twr(ordered.Select(r => TwrPeriod(plan, series, r, i)).ToList(), series.Base is { } b ? (double)b : null)
                    : Returns.Rebase(ordered.Select(r => Number(r, 0)).ToList(), (double)(series.Base ?? 100));
                for (var k = 0; k < ordered.Count; k++)
                    ordered[k][measure.Output] = values[k] is { } v ? (decimal)Math.Round(v, 10) : null;
            }
        }
        return warnings;
    }

    /// <summary>
    /// A TWR period's value and money put in: from a measure (counted from the
    /// period's start), or from dated FLOWS within the period, weighted by days.
    /// </summary>
    private static (double? Value, double? Flow, double? Weighted) TwrPeriod(Plan plan, SeriesCall series, IDictionary<string, object?> row, int measure)
    {
        double? Number(int part) => ResultValues.ToDecimal(row[$"__s{measure}_{part}"]) is { } d ? (double)d : null;
        var value = Number(0);
        if (series.Parts.Count > 1 && series.Parts[1].Kind == "flow")
            return (value, Number(1), null);

        var label = Convert.ToString(row[plan.Calendar!.Output], CultureInfo.InvariantCulture);
        var period = plan.Calendar.Periods.FirstOrDefault(p => p.Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) == label);
        if (period == default)
            return (value, null, null);
        var flows = series.Parts.Select((p, k) => (p, k)).Where(x => x.p.Kind == "flows")
            .SelectMany(x => Returns.ParseFlows(row[$"__s{measure}_{x.k}"] as string));
        var (flow, weighted) = Returns.InPeriod(flows, period.Start, period.End);
        return (value, flow, weighted);
    }

    /// <summary>The calendar with the period before its first one, as the base of TWR/REBASE.</summary>
    private static Calendar WithBasePeriod(Calendar calendar)
    {
        var end = calendar.Periods[0].Start.AddDays(-1);
        var start = calendar.Bucket switch
        {
            "day" => end,
            "week" => end.AddDays(-6),
            "month" => new DateOnly(end.Year, end.Month, 1),
            "quarter" => new DateOnly(end.Year, (end.Month - 1) / 3 * 3 + 1, 1),
            _ => new DateOnly(end.Year, 1, 1)
        };
        return calendar with { Periods = [(start, end), .. calendar.Periods], Base = true };
    }

    /// <summary>The query's order when it sorts by a CUSTOM value, which exists only after the function ran.</summary>
    private static List<IDictionary<string, object?>> SortInMemory(Plan plan, List<IDictionary<string, object?>> data)
    {
        IOrderedEnumerable<IDictionary<string, object?>>? ordered = null;
        foreach (var (output, descending) in plan.Sort)
        {
            Func<IDictionary<string, object?>, object?> key = row => row.TryGetValue(output, out var v) ? v : null;
            ordered = ordered is null
                ? descending ? data.OrderByDescending(key, SortComparer.Instance) : data.OrderBy(key, SortComparer.Instance)
                : descending ? ordered.ThenByDescending(key, SortComparer.Instance) : ordered.ThenBy(key, SortComparer.Instance);
        }
        return ordered?.ToList() ?? data;
    }

    /// <summary>Empty first, then numbers, text (ordinal), yes/no — like the databases' ascending order.</summary>
    private sealed class SortComparer : IComparer<object?>
    {
        public static readonly SortComparer Instance = new();

        public int Compare(object? x, object? y)
        {
            if (x is null || y is null)
                return x is null ? (y is null ? 0 : -1) : 1;
            if (ResultValues.ToDecimal(x) is { } a && ResultValues.ToDecimal(y) is { } b && x is not string && y is not string)
                return a.CompareTo(b);
            return string.CompareOrdinal(Convert.ToString(x, CultureInfo.InvariantCulture), Convert.ToString(y, CultureInfo.InvariantCulture));
        }
    }

    private static IDictionary<string, object?> Shape(Plan plan, IDictionary<string, object?> row, Dictionary<string, Dictionary<string, decimal>>? offsets)
    {
        var shaped = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var group in plan.Groups)
        {
            shaped[group.Output] = group.Bucket is not null
                ? Convert.ToString(row[group.Output], CultureInfo.InvariantCulture)
                : plan.Computed.TryGetValue(group.Column.Name, out var formula)
                    ? ResultValues.Shape(row[group.Output], formula.Type)
                    : ResultValues.Shape(row[group.Output], group.Column.Type);
            if (group.LabelOutput is not null)
                shaped[group.LabelOutput] = row[group.LabelOutput] is null ? null : Convert.ToString(row[group.LabelOutput], CultureInfo.InvariantCulture);
            if (group.OtherOutput is not null)
                shaped[group.OtherOutput] = Convert.ToInt64(row[group.OtherOutput], CultureInfo.InvariantCulture) == 1;
        }

        var partitions = plan.Groups.Where(g => g.Bucket is null).ToList();
        foreach (var measure in plan.Measures)
        {
            var value = row[measure.Output];
            if (measure.CarryIn && offsets is not null &&
                offsets[measure.Output].TryGetValue(PartitionKey(partitions, row), out var offset))
                value = (ResultValues.ToDecimal(value) ?? 0m) + offset;
            shaped[measure.Output] = measure.Fn is "min" or "max" && value is not (null or DBNull) && measure.Column is not null &&
                                     plan.Computed.TryGetValue(measure.Column!.Name, out var formula)
                ? ResultValues.Shape(value, formula.Type)
                : ShapeMeasure(value, measure);
        }
        return shaped;
    }

    private static object? ShapeMeasure(object? value, Measure measure)
    {
        if (measure.Type is { } formulaType)
            return ResultValues.Shape(value, formulaType);
        if (value is null or DBNull)
            return measure.Fn is "count" or "count_distinct" ? 0L : null;
        if (measure.Fn is "count" or "count_distinct")
            return Convert.ToInt64(value, CultureInfo.InvariantCulture);
        if (measure.Fn is "min" or "max")
            return ResultValues.Shape(value, measure.Column!.Type);

        var type = measure.Column!.Type;
        if (measure.Fn == "sum" && type == ColumnType.Integer)
            return Convert.ToInt64(value, CultureInfo.InvariantCulture);
        var number = ResultValues.ToDecimal(value)!.Value;
        // Currency is NUMERIC(19,4) on Postgres and REAL on SQLite: rounding
        // sums to 4 places removes the float noise and gives both the same value.
        return ResultValues.Normalize(Math.Round(number, measure.Fn == "sum" && type == ColumnType.Currency ? 4 : 10, MidpointRounding.AwayFromZero));
    }

    private static List<QueryResultColumn> Columns(Plan plan)
    {
        var columns = new List<QueryResultColumn>();
        foreach (var group in plan.Groups)
        {
            columns.Add(new QueryResultColumn(group.Output, group.Bucket is not null ? "date" : TypeName(group.Column.Type), "group"));
            if (group.LabelOutput is not null)
                columns.Add(new QueryResultColumn(group.LabelOutput, "text", "label", group.Output));
            if (group.OtherOutput is not null)
                columns.Add(new QueryResultColumn(group.OtherOutput, "boolean", "other", group.Output));
        }
        foreach (var measure in plan.Measures)
        {
            var type = measure.Fn switch
            {
                "formula" => FormulaTypes.Name(measure.Type!.Value),
                "count" or "count_distinct" => "integer",
                "avg" or "median" or "percentile" => measure.Column!.Type == ColumnType.Currency ? "currency" : "float",
                _ => TypeName(measure.Column!.Type)
            };
            columns.Add(new QueryResultColumn(measure.Output, type, "measure"));
        }
        return columns;
    }

    private static string TypeName(ColumnType type) => type.ToString().ToLowerInvariant();

    private static string Q(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    private static ApiValidationException Invalid(string field, string message) =>
        new(message, new Dictionary<string, object?> { ["field"] = field });
}
