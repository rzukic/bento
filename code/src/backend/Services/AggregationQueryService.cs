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

    public AggregationQueryService(SchemaCache schemaCache, QueryService queryService, DatabaseConfig dbConfig, ComputedColumns computed)
    {
        _schemaCache = schemaCache;
        _queryService = queryService;
        _dbConfig = dbConfig;
        _computed = computed;
    }

    private bool IsPostgres => _dbConfig.Provider == DatabaseProvider.Postgres;

    // ── Resolved query ───────────────────────────────────────────────────

    /// <param name="Top">Top values: only the first N values by the first measure (<paramref name="Ascending"/> for smallest first).</param>
    /// <param name="OtherOutput">Top values with "Other": the flag column marking the remaining values' group.</param>
    private sealed record Group(
        ColumnDef Column, string? Bucket, string Output, string Expression, string? LabelOutput, string? LabelExpression, string? Join,
        int? Top = null, bool Ascending = false, string? OtherOutput = null);

    public const int MaxTop = 1000;

    /// <param name="Fn">count, sum, … or "formula" (then <paramref name="Type"/> is the formula's result).</param>
    private sealed record Measure(string Output, string Fn, ColumnDef? Column, bool Running, bool CarryIn, string Expression, FormulaType? Type = null);

    /// <summary>One row per period: label (yyyy-MM-dd), first and last day.</summary>
    private sealed record Calendar(string Bucket, string Output, IReadOnlyList<(DateOnly Start, DateOnly End)> Periods);

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
        await using var scope = await _queryService.UseDataAsync();

        var (sql, parameters) = BuildSql(plan, scope);
        var rows = (await scope.Db.Connection.QueryAsync(sql, parameters))
            .Cast<IDictionary<string, object?>>()
            .ToList();

        var truncated = rows.Count > plan.Limit;
        if (truncated)
            rows = rows.Take(plan.Limit).ToList();

        var carryIns = plan.Measures.Where(m => m.CarryIn).ToList();
        Dictionary<string, Dictionary<string, decimal>>? offsets = null;
        if (carryIns.Count > 0)
            offsets = await LoadCarryInAsync(plan, carryIns, scope);

        var data = rows.Select(row => Shape(plan, row, offsets)).ToList();
        return new AggregationQueryResponse(data, new QueryResultMeta(Columns(plan), truncated));
    }

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
            Variables = variables ?? ResolveVariables(request.Variables)
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
            if (spec.Top is { } n)
            {
                if (bucket is not null)
                    throw Invalid($"{path}.top", "Top values work on columns, not on date periods.");
                if (calendar is not null)
                    throw Invalid($"{path}.top", "Top values cannot be combined with values at the end of each period.");
                if (n < 1 || n > MaxTop)
                    throw Invalid($"{path}.top", $"top must be between 1 and {MaxTop}.");
                var rank = spec.Rank?.Trim().ToLowerInvariant();
                if (rank is not (null or "" or "asc" or "desc"))
                    throw Invalid($"{path}.rank", "rank is \"desc\" (largest first) or \"asc\" (smallest first).");
                top = n;
                ascending = rank == "asc";
                if (spec.Other)
                    otherOutput = Output($"{output}_other", outputs, $"{path}.as");
            }
            else if (spec.Other)
            {
                throw Invalid($"{path}.other", "\"other\" needs top: the values after the top ones form the other group.");
            }
            groups.Add(new Group(column, bucket, output, expression, labelOutput, labelExpression, join, top, ascending, otherOutput));
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

        var limit = request.Limit ?? DefaultLimit;
        if (limit < 1 || limit > MaxLimit)
            throw Invalid("limit", $"limit must be between 1 and {MaxLimit}.");

        return new Plan(table, computed, calendar, filters, groups, measures, sort, limit);
    }

    /// <summary>A measure given as a formula over the group's rows.</summary>
    private Measure FormulaMeasure(QueryMeasure spec, string path, TableDef source, FormulaContext context, List<Group> groups, HashSet<string> outputs)
    {
        var schema = FormulaSchema.From(_schemaCache.Current);
        var (check, running) = FormulaCompiler.CompileMeasure(spec.Formula!, source, schema, _dbConfig.Provider, "t", context);
        if (!check.IsValid)
        {
            var error = check.Errors[0];
            throw new ApiValidationException($"{error.Message}", new Dictionary<string, object?>
            {
                ["field"] = $"{path}.formula",
                ["errors"] = check.Errors.Select(e => new FormulaErrorResponse(e.Message, e.Start, e.End)).ToList()
            });
        }

        running |= spec.Running;
        if (running && groups.Count(g => g.Bucket is not null) != 1)
            throw Invalid($"{path}.running", "Running totals need exactly one date group (day, week, month, quarter or year).");
        if (spec.CarryIn && !running)
            throw Invalid($"{path}.carryIn", "carryIn only applies to running totals.");
        if (spec.CarryIn && context.BucketAlias is not null)
            throw Invalid($"{path}.carryIn", "Values at the end of each period already include everything before.");

        var output = Output(spec.Name ?? $"formula_{outputs.Count + 1}", outputs, $"{path}.name");
        return new Measure(output, "formula", null, running, spec.CarryIn, check.Result!.Sql, check.Result.Type);
    }

    private const int MaxPeriods = 1000;

    private static Calendar ResolveCalendar(QueryAsOf asOf, HashSet<string> outputs)
    {
        var bucket = asOf.Bucket?.Trim().ToLowerInvariant();
        if (bucket is null || !Buckets.Contains(bucket))
            throw Invalid("asOf.bucket", "Choose the period: day, week, month, quarter or year.");
        if (!DateOnly.TryParseExact(asOf.From?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var from))
            throw Invalid("asOf.from", "asOf.from must be a date (yyyy-MM-dd).");
        if (!DateOnly.TryParseExact(asOf.To?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var to))
            throw Invalid("asOf.to", "asOf.to must be a date (yyyy-MM-dd).");
        if (to < from)
            throw Invalid("asOf.to", "asOf.to is before asOf.from.");

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

        var periods = new List<(DateOnly, DateOnly)>();
        for (var start = Start(from); start <= to; start = Next(start))
        {
            if (periods.Count == MaxPeriods)
                throw Invalid("asOf", $"At most {MaxPeriods} periods; choose a longer period or a shorter range.");
            periods.Add((start, Next(start).AddDays(-1)));
        }
        return new Calendar(bucket, Output(asOf.As ?? "period", outputs, "asOf.as"), periods);
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

    /// <summary>The periods as a derived table b(label, bucket, bucket_end), typed per provider.</summary>
    private string CalendarSql(Calendar calendar)
    {
        string Day(DateOnly day) => IsPostgres ? $"DATE '{day:yyyy-MM-dd}'" : $"'{day:yyyy-MM-dd}'";
        return string.Join(" UNION ALL ", calendar.Periods.Select(p =>
            $"SELECT '{p.Start:yyyy-MM-dd}' AS {Q("label")}, {Day(p.Start)} AS {Q("bucket")}, {Day(p.End)} AS {Q("bucket_end")}"));
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
                return new QueryVariableResult(name, column.Type, result.Data.FirstOrDefault()?["value"], null);
            }

            var table = new TableDef { Name = "__variables", DisplayName = "the dashboard variables" };
            var check = FormulaCompiler.Compile(definition.Formula ?? "", table, FormulaSchema.From(_schemaCache.Current), _dbConfig.Provider,
                context: new FormulaContext { Variables = values });
            if (!check.IsValid)
                return new QueryVariableResult(name, null, null, check.Errors[0].Message);

            await using var scope = await _queryService.UseDataAsync();
            var raw = await scope.Db.Connection.ExecuteScalarAsync($"SELECT {check.Result!.Sql} AS {Q("value")}");
            return new QueryVariableResult(name, FormulaTypes.Name(check.Result.Type), ResultValues.Shape(raw, check.Result.Type), null);
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
        }

        // The period's days are fixed per label; grouping by them lets measures use BUCKET_END() anywhere.
        if (plan.Calendar is not null)
            groupBy.AddRange([$"b.{Q("bucket")}", $"b.{Q("bucket_end")}"]);

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
            var over = (partitions.Count > 0 ? $"PARTITION BY {string.Join(", ", partitions)} " : "")
                + $"ORDER BY g.{Q(bucket.Output)} ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW";
            for (var i = 0; i < plan.Measures.Count; i++)
            {
                if (plan.Measures[i].Running)
                    outer.Append($", SUM(g.{Q($"__r{i}")}) OVER ({over}) AS {Q(plan.Measures[i].Output)}");
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

        foreach (IDictionary<string, object?> row in await scope.Db.Connection.QueryAsync(sql.ToString(), parameters))
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
