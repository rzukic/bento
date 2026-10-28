using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bento.Api.Db;
using Bento.Api.Db.Schema;
using Bento.Api.Exceptions;
using Bento.Api.Models;
using Bento.Api.Services.Workspaces;
using Dapper;

namespace Bento.Api.Services.Sharing;

/// <summary>
/// What a share link's visitor may ask for (Step 5.6). The browser builds a
/// dashboard's queries itself; here each one must be what a widget of the
/// shared dashboard asks: its table, its measures, its groups (any period on
/// its date column), its own filter unchanged — plus the date range the
/// visitor picks and the link's fixed filters, which are always applied.
/// Anything else is refused, so a link shows the dashboard and nothing more
/// of the workspace's data.
/// </summary>
public sealed class ShareGuard
{
    private static readonly string[] Buckets = ["day", "week", "month", "quarter", "year"];
    private static readonly string[] RangeOps = ["gte", "lt", "gt", "lte"];
    private const int MaxRows = 1000;

    private readonly ISystemPool _systemPool;
    private readonly SchemaCache _schemaCache;

    public ShareGuard(ISystemPool systemPool, SchemaCache schemaCache)
    {
        _systemPool = systemPool;
        _schemaCache = schemaCache;
    }

    // ── What the dashboard asks ──────────────────────────────────────────

    /// <summary>One query a widget runs, as stored.</summary>
    private sealed record Shape(string Source, IReadOnlyList<JsonObject> Measures, IReadOnlyList<JsonObject> Groups, JsonObject Filter,
        IReadOnlySet<string> DateColumns, int? Limit, bool AsOf);

    private sealed record RowsShape(string Source, JsonObject Filter, string? DateColumn, int Limit);

    private sealed record VariableShape(string Name, string? Source, string Formula, string? DateColumn, bool Input);

    private sealed record Plan(long DashboardId, IReadOnlyList<Shape> Queries, IReadOnlyList<RowsShape> Rows, IReadOnlySet<string> LinkedTables,
        IReadOnlyList<VariableShape> Variables, IReadOnlySet<long> SavedQueries, IReadOnlySet<string> Tables);

    private async Task<Plan> PlanAsync(ShareService.Visitor visitor)
    {
        var link = visitor.Link;
        if (link.Kind != "dashboard")
            throw NotShared();
        using var conn = await _systemPool.GetConnectionAsync();
        var table = _systemPool.Provider == DatabaseProvider.Postgres ? "_system.dashboards" : "dashboards";
        var json = await conn.ExecuteScalarAsync<string?>(
            $"SELECT CAST(dashboard_json AS TEXT) FROM {table} WHERE id = @Id AND workspace_id = @Ws", new { Id = link.TargetId, Ws = link.WorkspaceId })
            ?? throw new ApiNotFoundException("This dashboard is no longer there.");
        var document = JsonNode.Parse(json) as JsonObject ?? new JsonObject();

        var queries = new List<Shape>();
        var rows = new List<RowsShape>();
        var linked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var saved = new HashSet<long>();
        foreach (var widget in (document["widgets"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (widget["data"] is not JsonObject data)
                continue;
            if (data["savedQueryId"] is JsonValue savedId && savedId.TryGetValue<long>(out var id))
                saved.Add(id);
            if (data["target"] is JsonObject target && Text(target, "source") is { } targetSource)
            {
                var date = Text(target, "date");
                queries.Add(new Shape(targetSource, [new JsonObject { ["formula"] = Text(target, "value") }],
                    [new JsonObject { ["column"] = Text(target, "group") }], new JsonObject(), Set(date), null, false));
                if (Text(target, "group") is { } group && _schemaCache.GetTable(targetSource)?.Columns
                        .FirstOrDefault(c => string.Equals(c.Name, group, StringComparison.OrdinalIgnoreCase))?.References is { } references)
                    linked.Add(references);
            }
            if (data["query"] is not JsonObject query || Text(query, "source") is not { } source)
                continue;
            var filter = query["filter"] as JsonObject ?? new JsonObject();
            var groups = (query["groupBy"] as JsonArray ?? []).OfType<JsonObject>().ToList();
            var dateGroup = groups.FirstOrDefault(g => Text(g, "bucket") is not null);
            var dateColumn = data.ContainsKey("dateColumn") ? Text(data, "dateColumn") : dateGroup is null ? null : Text(dateGroup, "column");
            var dates = Set(dateColumn, dateGroup is null ? null : Text(dateGroup, "column"));
            if (data["rows"] is JsonObject rowSettings)
            {
                rows.Add(new RowsShape(source, filter, dateColumn, Math.Min(IntOf(rowSettings["limit"]) ?? 25, MaxRows)));
                continue;
            }
            queries.Add(new Shape(source, (query["measures"] as JsonArray ?? []).OfType<JsonObject>().ToList(), groups, filter, dates,
                IntOf(query["limit"]), data["asOf"] is JsonValue asOf && asOf.TryGetValue<bool>(out var on) && on));
        }

        var variables = (document["variables"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(v => Text(v, "name") is not null && Text(v, "formula") is not null)
            .Select(v => new VariableShape(Text(v, "name")!, Text(v, "source"), Text(v, "formula")!, Text(v, "dateColumn"), v["input"] is JsonObject))
            .ToList();

        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        tables.UnionWith(queries.Select(q => q.Source));
        tables.UnionWith(rows.Select(r => r.Source));
        tables.UnionWith(linked);
        tables.UnionWith(variables.Where(v => v.Source is not null).Select(v => v.Source!));
        return new Plan(link.TargetId, queries, rows, linked, variables, saved, tables);
    }

    // ── Checks ───────────────────────────────────────────────────────────

    public async Task CheckDashboardAsync(ShareService.Visitor visitor, long id)
    {
        if (visitor.Link.Kind != "dashboard" || visitor.Link.TargetId != id)
            throw NotShared();
        await PlanAsync(visitor);
    }

    /// <summary>A query of one of the dashboard's widgets, with the link's fixed filters applied.</summary>
    public async Task<AggregationQueryRequest> CheckQueryAsync(ShareService.Visitor visitor, AggregationQueryRequest request)
    {
        var plan = await PlanAsync(visitor);
        var filter = ToObject(request.Filter);
        foreach (var shape in plan.Queries)
        {
            if (!string.Equals(shape.Source, request.Source, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!MeasuresFit(shape, request.Measures) || !GroupsFit(shape, request.GroupBy) || !FilterFits(shape.Filter, shape.DateColumns, filter, visitor, shape.Source))
                continue;
            if (shape.Limit is { } limit && (request.Limit is null || request.Limit > limit))
                continue;
            if (request.AsOf is not null && (!shape.AsOf || !Buckets.Contains(request.AsOf.Bucket ?? "")))
                continue;
            if (request.Variables is { } values && !VariablesFit(plan, values))
                continue;
            return request with { Filter = Enforce(filter, visitor, shape.Source) };
        }
        throw NotShared();
    }

    /// <summary>The dashboard's variables as stored; inputs may carry the visitor's value.</summary>
    public async Task<QueryVariablesRequest> CheckVariablesAsync(ShareService.Visitor visitor, QueryVariablesRequest request)
    {
        var plan = await PlanAsync(visitor);
        var checkedVariables = new List<QueryVariable>();
        foreach (var variable in request.Variables ?? [])
        {
            var shape = plan.Variables.FirstOrDefault(v => v.Name == variable.Name) ?? throw NotShared();
            var literal = shape.Input && variable.Source is null && IsLiteral(variable.Formula);
            if (!literal && (!Same(shape.Source, variable.Source) || !SameFormula(shape.Formula, variable.Formula)))
                throw NotShared();
            var filter = ToObject(variable.Filter);
            if (literal)
            {
                checkedVariables.Add(variable with { Filter = null });
                continue;
            }
            if (shape.Source is null)
            {
                checkedVariables.Add(variable with { Filter = null });
                continue;
            }
            if (!FilterFits(new JsonObject(), Set(shape.DateColumn), filter, visitor, shape.Source))
                throw NotShared();
            checkedVariables.Add(variable with { Filter = Enforce(filter, visitor, shape.Source) });
        }
        return request with { Variables = checkedVariables };
    }

    public async Task CheckSavedQueryAsync(ShareService.Visitor visitor, long id)
    {
        if (!(await PlanAsync(visitor)).SavedQueries.Contains(id))
            throw NotShared();
    }

    /// <summary>A saved query of the dashboard, with only a date range and the link's filters added.</summary>
    public async Task<JsonElement?> CheckSavedRunAsync(ShareService.Visitor visitor, long id, string source, JsonElement? extra)
    {
        await CheckSavedQueryAsync(visitor, id);
        var filter = ToObject(extra);
        foreach (var (column, condition) in filter)
        {
            if (condition is not JsonObject ops || ops.Any(op => !RangeOps.Contains(op.Key) || !IsDate(op.Value)))
            {
                if (!IsLinkCondition(visitor, column, condition))
                    throw NotShared();
            }
        }
        return Enforce(filter, visitor, source);
    }

    /// <summary>A table of rows on the dashboard, or the targets a target widget reads; the link's filters added.</summary>
    public async Task<ListQueryParameters> CheckRowsAsync(ShareService.Visitor visitor, TableDef table, ListQueryParameters parameters)
    {
        if (visitor.Link.Kind == "form")
            return await CheckChoicesAsync(visitor, table, parameters);
        var plan = await PlanAsync(visitor);
        if (parameters.Includes.Count > 0 || parameters.Fields is not null || !string.IsNullOrEmpty(parameters.Search))
            throw NotShared();
        if (plan.LinkedTables.Contains(table.Name) && parameters.Filters.Count == 0 && parameters.PerPage <= 200)
            return parameters;
        foreach (var shape in plan.Rows.Where(r => string.Equals(r.Source, table.Name, StringComparison.OrdinalIgnoreCase)))
        {
            if (parameters.PerPage > shape.Limit || parameters.Page != 1)
                continue;
            var expected = Clauses(shape.Filter);
            var rest = parameters.Filters.ToList();
            if (!expected.All(e => rest.Remove(e)))
                continue;
            var fits = rest.All(clause =>
                (string.Equals(clause.Column, shape.DateColumn, StringComparison.OrdinalIgnoreCase) && RangeOps.Contains(clause.Operator) && IsDate(clause.Value)) ||
                IsLinkClause(visitor, clause));
            if (!fits)
                continue;
            // The link's fixed filters, whatever the browser sent.
            var forced = parameters.Filters.Where(c => !IsLinkColumn(visitor, c.Column)).ToList();
            forced.AddRange(LinkClauses(visitor, table));
            return parameters with { Filters = forced };
        }
        throw NotShared();
    }

    /// <summary>The model of the tables the dashboard reads: nothing about the rest of the workspace.</summary>
    public async Task<ModelJson> SchemaAsync(ShareService.Visitor visitor, ModelJson model)
    {
        var tables = visitor.Link.Kind == "form" ? (await FormAsync(visitor)).Tables : (await PlanAsync(visitor)).Tables;
        return new ModelJson
        {
            Version = model.Version,
            Tables = model.Tables.Where(t => tables.Contains(t.Name)).ToList(),
            Relations = model.Relations.Where(r => tables.Contains(r.From) && tables.Contains(r.To)).ToList()
        };
    }

    // ── Forms (part B) ───────────────────────────────────────────────────

    /// <summary>A field of the form: its column, and the value it always gets when hidden with a fixed one.</summary>
    private sealed record FormField(string Column, bool Hidden, JsonNode? FixedValue);

    /// <param name="Links">The tables its visible link fields choose from.</param>
    private sealed record FormPlan(long FormId, string Table, IReadOnlyList<FormField> Fields, IReadOnlySet<string> Links, IReadOnlySet<string> Tables);

    private async Task<FormPlan> FormAsync(ShareService.Visitor visitor)
    {
        var link = visitor.Link;
        if (link.Kind != "form")
            throw NotShared();
        using var conn = await _systemPool.GetConnectionAsync();
        var forms = _systemPool.Provider == DatabaseProvider.Postgres ? "_system.forms" : "forms";
        var row = await conn.QuerySingleOrDefaultAsync<(string Table, string Json)?>(
            $"SELECT table_name, CAST(form_json AS TEXT) FROM {forms} WHERE id = @Id AND workspace_id = @Ws", new { Id = link.TargetId, Ws = link.WorkspaceId })
            ?? throw new ApiNotFoundException("This form is no longer there.");
        var table = _schemaCache.GetTable(row.Table) ?? throw new ApiNotFoundException("This form's table is no longer there.");
        var document = JsonNode.Parse(row.Json) as JsonObject ?? new JsonObject();
        var fields = (document["fields"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(f => Text(f, "column") is not null)
            .Select(f =>
            {
                var hidden = Flag(f["hidden"]);
                var fixedValue = hidden && f["default"] is JsonObject d && Text(d, "kind") == "value" ? d["value"]?.DeepClone() : null;
                return new FormField(Text(f, "column")!, hidden, fixedValue);
            })
            .Where(f => table.Columns.Any(c => string.Equals(c.Name, f.Column, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var links = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in fields.Where(f => !f.Hidden))
        {
            if (table.Columns.First(c => string.Equals(c.Name, field.Column, StringComparison.OrdinalIgnoreCase)).References is { } target)
                links.Add(target);
        }
        var tables = new HashSet<string>(links, StringComparer.OrdinalIgnoreCase) { table.Name };
        return new FormPlan(link.TargetId, table.Name, fields, links, tables);
    }

    public async Task CheckFormAsync(ShareService.Visitor visitor, long id)
    {
        if ((await FormAsync(visitor)).FormId != id)
            throw NotShared();
    }

    /// <summary>
    /// The choices of a link field: the linked table's rows, named only — its
    /// key and the columns that label a record, nothing else of them.
    /// </summary>
    private async Task<ListQueryParameters> CheckChoicesAsync(ShareService.Visitor visitor, TableDef table, ListQueryParameters parameters)
    {
        var form = await FormAsync(visitor);
        if (!form.Links.Contains(table.Name) || parameters.Includes.Count > 0 || parameters.Filters.Count > 0 || parameters.PerPage > 50)
            throw NotShared();
        var label = CrudService.GetRecordLabelColumns(table);
        if (parameters.Sorts.Any(s => !label.Contains(s.Column, StringComparer.OrdinalIgnoreCase)))
            throw NotShared();
        var key = table.Columns.FirstOrDefault(c => c.PrimaryKey)?.Name ?? "id";
        return parameters with { Fields = [key, .. label.Where(l => !string.Equals(l, key, StringComparison.OrdinalIgnoreCase))] };
    }

    /// <summary>
    /// A row sent through the form: only its fields, into its table; hidden
    /// fields with a fixed value get that value, whatever the browser sent.
    /// </summary>
    public async Task<JsonElement> CheckEntryAsync(ShareService.Visitor visitor, TableDef table, JsonElement body)
    {
        var form = await FormAsync(visitor);
        if (!string.Equals(form.Table, table.Name, StringComparison.OrdinalIgnoreCase) || body.ValueKind != JsonValueKind.Object)
            throw NotShared();
        var row = JsonNode.Parse(body.GetRawText()) as JsonObject ?? new JsonObject();
        var unknown = row.Select(p => p.Key).FirstOrDefault(k => !form.Fields.Any(f => string.Equals(f.Column, k, StringComparison.OrdinalIgnoreCase)));
        if (unknown is not null)
            throw new ApiForbiddenException($"'{unknown}' is not a field of this form.", new Dictionary<string, object?> { ["field"] = unknown });
        foreach (var field in form.Fields.Where(f => f.FixedValue is not null))
        {
            foreach (var key in row.Select(p => p.Key).Where(k => string.Equals(k, field.Column, StringComparison.OrdinalIgnoreCase)).ToList())
                row.Remove(key);
            row[field.Column] = field.FixedValue!.DeepClone();
        }
        return JsonSerializer.Deserialize<JsonElement>(row.ToJsonString());
    }

    // ── Matching ─────────────────────────────────────────────────────────

    private static bool MeasuresFit(Shape shape, IReadOnlyList<QueryMeasure>? measures) =>
        (measures ?? []).All(m => shape.Measures.Any(stored =>
            Same(Text(stored, "fn"), m.Fn) && Same(Text(stored, "column"), m.Column) && SameFormula(Text(stored, "formula"), m.Formula) &&
            Nullable(stored["percentile"]) == m.Percentile && Flag(stored["running"]) == m.Running && Flag(stored["carryIn"]) == m.CarryIn));

    private static bool GroupsFit(Shape shape, IReadOnlyList<QueryGroupBy>? groups) =>
        (groups ?? []).All(g =>
            // Any period on the date column (the interval switch, a KPI's trend) …
            (g.Column is not null && shape.DateColumns.Contains(g.Column) && (g.Bucket is null || Buckets.Contains(g.Bucket))) ||
            // … or a group of the widget as stored.
            shape.Groups.Any(stored => Same(Text(stored, "column"), g.Column) && Same(Text(stored, "bucket"), g.Bucket) &&
                IntOf(stored["top"]) == g.Top && Flag(stored["other"]) == g.Other && Same(Text(stored, "rank"), g.Rank)));

    /// <summary>
    /// The widget's own conditions unchanged (none dropped), a range on its date
    /// column, and the link's fixed filters — nothing else.
    /// </summary>
    private static bool FilterFits(JsonObject stored, IReadOnlySet<string> dateColumns, JsonObject filter, ShareService.Visitor visitor, string source)
    {
        foreach (var (column, condition) in stored)
        {
            if (!filter.TryGetPropertyValue(column, out var sent))
                return false;
            var expected = Normalize(condition);
            var got = Normalize(sent);
            if (dateColumns.Contains(column))
            {
                // Its own date conditions stay; the range may come on top.
                if (expected is not JsonObject own || got is not JsonObject ops || !own.All(op => ops.TryGetPropertyValue(op.Key, out var v) && Equal(op.Value, v)))
                    return false;
                if (ops.Any(op => !own.ContainsKey(op.Key) && (!RangeOps.Contains(op.Key) || !IsDate(op.Value))))
                    return false;
                continue;
            }
            if (LinkCondition(visitor, column) is { } fixedCondition)
                expected = Merge(expected, fixedCondition);
            if (!Equal(expected, got))
                return false;
        }
        foreach (var (column, condition) in filter)
        {
            if (stored.ContainsKey(column))
                continue;
            if (dateColumns.Contains(column) && condition is JsonObject ops && ops.All(op => RangeOps.Contains(op.Key) && IsDate(op.Value)))
                continue;
            if (IsLinkCondition(visitor, column, condition))
                continue;
            return false;
        }
        return true;
    }

    private static bool VariablesFit(Plan plan, Dictionary<string, JsonElement> values) =>
        values.All(v => plan.Variables.Any(s => s.Name == v.Key) && v.Value.ValueKind is JsonValueKind.Number or JsonValueKind.String or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null);

    // ── The link's fixed filters ─────────────────────────────────────────

    private static JsonNode? LinkCondition(ShareService.Visitor visitor, string column) =>
        visitor.Link.Options.Filters?.FirstOrDefault(f => string.Equals(f.Key, column, StringComparison.OrdinalIgnoreCase)) is { Value.Count: > 0 } pair
            ? ShareService.ConditionOf(pair.Value)
            : null;

    private static bool IsLinkColumn(ShareService.Visitor visitor, string column) => LinkCondition(visitor, column) is not null;

    private static bool IsLinkCondition(ShareService.Visitor visitor, string column, JsonNode? condition) =>
        LinkCondition(visitor, column) is { } fixedCondition && Equal(Normalize(fixedCondition), Normalize(condition));

    private static bool IsLinkClause(ShareService.Visitor visitor, FilterClause clause) =>
        LinkCondition(visitor, clause.Column) is not null;

    /// <summary>The filter with the link's fixed filters on every column the table has.</summary>
    private JsonElement? Enforce(JsonObject filter, ShareService.Visitor visitor, string source)
    {
        var table = _schemaCache.GetTable(source);
        var result = (JsonObject)filter.DeepClone();
        foreach (var (column, values) in visitor.Link.Options.Filters ?? [])
        {
            if (values.Count == 0 || table?.Columns.Any(c => string.Equals(c.Name, column, StringComparison.OrdinalIgnoreCase)) != true)
                continue;
            result[column] = result.TryGetPropertyValue(column, out var existing) && existing is not null
                ? Merge(Normalize(existing), ShareService.ConditionOf(values))
                : ShareService.ConditionOf(values);
        }
        return result.Count == 0 ? null : JsonSerializer.Deserialize<JsonElement>(result.ToJsonString());
    }

    private static IEnumerable<FilterClause> LinkClauses(ShareService.Visitor visitor, TableDef table)
    {
        foreach (var (column, values) in visitor.Link.Options.Filters ?? [])
        {
            if (values.Count == 0 || !table.Columns.Any(c => string.Equals(c.Name, column, StringComparison.OrdinalIgnoreCase)))
                continue;
            yield return values.Count == 1
                ? new FilterClause(column, "eq", Scalar(values[0]))
                : new FilterClause(column, "in", string.Join(",", values.Select(Scalar)));
        }
    }

    // ── JSON helpers ─────────────────────────────────────────────────────

    /// <summary>Filters as the list endpoint takes them (the dashboard's toFilterClauses).</summary>
    private static List<FilterClause> Clauses(JsonObject filter)
    {
        var clauses = new List<FilterClause>();
        foreach (var (column, condition) in filter)
        {
            if (condition is JsonObject ops)
            {
                foreach (var (op, value) in ops)
                    clauses.Add(new FilterClause(column, op, value is JsonArray list ? string.Join(",", list.Select(v => v?.ToString())) : value is JsonValue b && b.TryGetValue<bool>(out var t) && t ? "" : value?.ToString() ?? ""));
            }
            else
            {
                clauses.Add(condition is null ? new FilterClause(column, "null", "") : new FilterClause(column, "eq", condition.ToString()));
            }
        }
        return clauses;
    }

    private static JsonObject ToObject(JsonElement? element) =>
        element is { ValueKind: JsonValueKind.Object } value ? JsonNode.Parse(value.GetRawText()) as JsonObject ?? new JsonObject() : new JsonObject();

    /// <summary>A plain value means equals; null means "is empty" — as the dashboard merges them.</summary>
    private static JsonNode? Normalize(JsonNode? condition) => condition switch
    {
        JsonObject o => o,
        null => new JsonObject { ["null"] = true },
        _ => new JsonObject { ["eq"] = condition.DeepClone() }
    };

    private static JsonNode Merge(JsonNode? a, JsonNode b)
    {
        var merged = a is JsonObject o ? (JsonObject)o.DeepClone() : new JsonObject();
        foreach (var (key, value) in (JsonObject)Normalize(b)!)
            merged[key] = value?.DeepClone();
        return merged;
    }

    /// <summary>Equal as the browser may type them: 5 and "5" are the same value.</summary>
    private static bool Equal(JsonNode? a, JsonNode? b) => (a, b) switch
    {
        (null, null) => true,
        (JsonObject x, JsonObject y) => x.Count == y.Count && x.All(p => y.TryGetPropertyValue(p.Key, out var v) && Equal(p.Value, v)),
        (JsonArray x, JsonArray y) => x.Count == y.Count && x.Zip(y).All(p => Equal(p.First, p.Second)),
        (JsonValue x, JsonValue y) => x.ToString() == y.ToString(),
        _ => false
    };

    private static bool IsDate(JsonNode? value) => value is JsonValue v && v.TryGetValue<string>(out var text) && IsDate(text);

    private static bool IsDate(string? text) =>
        text is { Length: >= 10 and <= 40 } && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out _);

    /// <summary>A visitor's value for an input variable: a number or a quoted text, never a formula over rows.</summary>
    private static bool IsLiteral(string? formula)
    {
        var text = formula?.Trim().TrimStart('=').Trim() ?? "";
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _) ||
               (text.Length >= 2 && text[0] == '"' && text[^1] == '"' && !text[1..^1].Contains('"'));
    }

    private static string Scalar(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText();

    private static string? Text(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;

    private static int? IntOf(JsonNode? node) => node is JsonValue v && v.TryGetValue<int>(out var i) ? i : node is JsonValue d && d.TryGetValue<double>(out var f) ? (int)f : null;

    private static double? Nullable(JsonNode? node) => node is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;

    private static bool Flag(JsonNode? node) => node is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    private static bool Same(string? a, string? b) => string.Equals(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);

    private static bool SameFormula(string? a, string? b) => string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.Ordinal);

    private static HashSet<string> Set(params string?[] values) =>
        new(values.Where(v => !string.IsNullOrWhiteSpace(v))!, StringComparer.OrdinalIgnoreCase);

    private static ApiForbiddenException NotShared() => new("A share link shows its dashboard, nothing else.");
}
