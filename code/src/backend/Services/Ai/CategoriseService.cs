using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Bento.Api.Db;
using Bento.Api.Db.Schema;
using Bento.Api.Exceptions;
using Bento.Api.Services.Mcp;

namespace Bento.Api.Services.Ai;

/// <summary>
/// Categorising rows (Step 4.8), e.g. bank bookings on import: for each row a
/// value of a link or choice column — first from the user's rules ("REWE" →
/// Groceries), then from earlier rows with the same payee, and only for the
/// rest from the AI. Corrections become rules, so next month needs fewer AI calls.
/// </summary>
public sealed class CategoriseService
{
    public const int MaxRows = 500;
    private const int AiBatchSize = 60;
    private const int AiParallelBatches = 3;
    private const int MaxOptions = 400;
    private const int MaxExamples = 150;
    private const int MaxCellLength = 160;

    private readonly SchemaCache _schemaCache;
    private readonly QueryService _queryService;
    private readonly ISystemPool _systemPool;
    private readonly AiAgent _agent;

    public CategoriseService(SchemaCache schemaCache, QueryService queryService, ISystemPool systemPool, AiAgent agent)
    {
        _schemaCache = schemaCache;
        _queryService = queryService;
        _systemPool = systemPool;
        _agent = agent;
    }

    // ── Requests and answers ─────────────────────────────────────────────

    public sealed class SuggestRequest
    {
        /// <summary>The column to fill: a link (e.g. category_id) or a choice column.</summary>
        public string? Column { get; init; }
        public IReadOnlyList<SuggestRow>? Rows { get; init; }
        /// <summary>Also ask the AI for rows no rule or earlier row decides.</summary>
        public bool Ai { get; init; }
    }

    public sealed class SuggestRow
    {
        /// <summary>The caller's id for the row, e.g. the file line.</summary>
        public string? Key { get; init; }
        /// <summary>The row's other values by column name, as shown (payee, purpose, amount, date).</summary>
        public Dictionary<string, string?>? Values { get; init; }
    }

    /// <param name="Value">What to store: the linked record's id, or the choice.</param>
    /// <param name="Source">rule, history or ai.</param>
    /// <param name="Keyword">The stable part of the text that decides it (e.g. "REWE"), for a rule.</param>
    /// <param name="MatchColumn">The column the keyword is in.</param>
    public sealed record Suggestion(string Key, string Value, string Label, string Source, double? Confidence, string? Keyword, string? MatchColumn, long? RuleId);

    public sealed record Option(string Value, string Label, string? Group);

    public sealed record AiRun(string Model, decimal CostUsd, int Rows, int DurationMs);

    public sealed record SuggestResponse(
        string Column,
        IReadOnlyList<Option> Options,
        IReadOnlyList<string> MatchColumns,
        IReadOnlyList<Suggestion> Suggestions,
        AiRun? Ai,
        string? AiError);

    public sealed record Rule(long Id, string TableName, string Column, string MatchColumn, string Pattern, string Value, string? Label, string CreatedAt);

    public sealed class RuleRequest
    {
        public string? Column { get; init; }
        public string? MatchColumn { get; init; }
        public string? Pattern { get; init; }
        public string? Value { get; init; }
    }

    // ── Suggestions ──────────────────────────────────────────────────────

    public async Task<SuggestResponse> SuggestAsync(string tableName, SuggestRequest request, McpCaller? aiCaller, CancellationToken ct)
    {
        var (table, column) = ResolveColumn(tableName, request.Column);
        var options = await LoadOptionsAsync(column);
        var byValue = options.ToDictionary(o => o.Value, StringComparer.OrdinalIgnoreCase);
        var rows = (request.Rows ?? [])
            .Where(r => !string.IsNullOrWhiteSpace(r.Key))
            .Select(r => (Key: r.Key!.Trim(), Values: Clean(r.Values)))
            .ToList();
        if (rows.Count > MaxRows)
            throw new ApiValidationException($"At most {MaxRows} rows per request.", new Dictionary<string, object?> { ["field"] = "rows" });

        var matchColumns = MatchColumns(table, column, rows.SelectMany(r => r.Values.Keys));
        var suggestions = new Dictionary<string, Suggestion>(StringComparer.Ordinal);

        // 1. The user's rules: the longest matching keyword wins.
        var rules = (await ListRulesAsync(table.Name, column.Name))
            .Where(r => byValue.ContainsKey(r.Value))
            .OrderByDescending(r => r.Pattern.Length)
            .ToList();
        foreach (var (key, values) in rows)
        {
            var rule = rules.FirstOrDefault(r => values.TryGetValue(r.MatchColumn, out var text) && text is not null &&
                                                 text.Contains(r.Pattern, StringComparison.OrdinalIgnoreCase));
            if (rule is not null)
                suggestions[key] = new Suggestion(key, byValue[rule.Value].Value, byValue[rule.Value].Label, "rule", 1, rule.Pattern, rule.MatchColumn, rule.Id);
        }

        // 2. Earlier rows with exactly the same payee: what they were given. Only the
        //    first text column (the payee): a purpose like "Kartenzahlung" says nothing.
        foreach (var match in matchColumns.Take(1))
        {
            var open = rows.Where(r => !suggestions.ContainsKey(r.Key) && r.Values.TryGetValue(match, out var v) && !string.IsNullOrEmpty(v)).ToList();
            if (open.Count == 0)
                continue;
            var history = await HistoryAsync(table, column, match, open.Select(r => r.Values[match]!).Distinct().ToList());
            foreach (var (key, values) in open)
            {
                if (history.TryGetValue(values[match]!, out var found) && byValue.TryGetValue(found.Value, out var option))
                    suggestions[key] = new Suggestion(key, option.Value, option.Label, "history", Math.Round(found.Share, 2), values[match], match, null);
            }
        }

        // 3. The AI, for what is left.
        AiRun? aiRun = null;
        string? aiError = null;
        var rest = rows.Where(r => !suggestions.ContainsKey(r.Key)).ToList();
        if (request.Ai && rest.Count > 0 && options.Count > 0)
        {
            if (aiCaller is null)
                throw new ApiForbiddenException("Bento's AI is for signed-in users; scripts and connected apps use their own model.");
            try
            {
                var (found, run) = await AskAiAsync(table, column, options, matchColumns, rest, aiCaller, ct);
                aiRun = run;
                foreach (var suggestion in found)
                    suggestions[suggestion.Key] = suggestion;
            }
            catch (AiProviderException ex)
            {
                aiError = ex.Message;
            }
            catch (ApiUnprocessableEntityException ex)
            {
                aiError = ex.Message;
            }
        }

        return new SuggestResponse(
            column.Name,
            options,
            matchColumns,
            rows.Where(r => suggestions.ContainsKey(r.Key)).Select(r => suggestions[r.Key]).ToList(),
            aiRun,
            aiError);
    }

    private static Dictionary<string, string?> Clean(Dictionary<string, string?>? values)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in values ?? [])
        {
            var text = value?.Trim();
            result[name] = string.IsNullOrEmpty(text) ? null : text.Length > 500 ? text[..500] : text;
        }
        return result;
    }

    /// <summary>The text columns that describe a row (payee, purpose), in table order.</summary>
    private static List<string> MatchColumns(TableDef table, ColumnDef target, IEnumerable<string> present)
    {
        var names = new HashSet<string>(present, StringComparer.OrdinalIgnoreCase);
        return table.Columns
            .Where(c => c.Type == ColumnType.Text && !c.PrimaryKey && c != target && names.Contains(c.Name))
            .Select(c => c.Name)
            .ToList();
    }

    private (TableDef Table, ColumnDef Column) ResolveColumn(string tableName, string? columnName)
    {
        var table = _schemaCache.GetTable(tableName)
            ?? throw new ApiNotFoundException($"Table '{tableName}' does not exist.", new Dictionary<string, object?> { ["table"] = tableName });
        if (string.IsNullOrWhiteSpace(columnName))
            throw new ApiValidationException("Choose the column to fill.", new Dictionary<string, object?> { ["field"] = "column" });
        var column = table.Columns.FirstOrDefault(c => string.Equals(c.Name, columnName.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new ApiValidationException($"Table '{table.Name}' has no column '{columnName}'.", new Dictionary<string, object?> { ["field"] = "column" });
        var categorisable = (column.Type == ColumnType.Reference && !string.IsNullOrWhiteSpace(column.References)) ||
                            (column.Type == ColumnType.Enum && column.Values.Count > 0);
        if (!categorisable || !CrudService.IsWritableColumn(column))
            throw new ApiValidationException($"Column '{column.Name}' is not a link or a choice column, so it cannot be categorised.",
                new Dictionary<string, object?> { ["field"] = "column" });
        return (table, column);
    }

    /// <summary>What the column can hold: the linked table's records (grouped by their parent, if they have one) or the choices.</summary>
    private async Task<List<Option>> LoadOptionsAsync(ColumnDef column)
    {
        if (column.Type == ColumnType.Enum)
            return column.Values.Select(v => new Option(v, v, null)).ToList();

        var target = _schemaCache.GetTable(column.References!);
        var primaryKey = target?.Columns.FirstOrDefault(c => c.PrimaryKey);
        if (target is null || primaryKey is null)
            return [];
        var label = CrudService.GetRecordLabelColumns(target).FirstOrDefault() ?? primaryKey.Name;
        var parent = target.Columns.FirstOrDefault(c => c.Type == ColumnType.Reference &&
                                                        string.Equals(c.References, target.Name, StringComparison.OrdinalIgnoreCase));

        var sql = parent is null
            ? $"SELECT {Quote(primaryKey.Name)} AS id, {Quote(label)} AS label, NULL AS parent FROM {Quote(target.Name)} ORDER BY {Quote(label)} LIMIT {MaxOptions}"
            : $"""
              SELECT t.{Quote(primaryKey.Name)} AS id, t.{Quote(label)} AS label, p.{Quote(label)} AS parent
              FROM {Quote(target.Name)} t LEFT JOIN {Quote(target.Name)} p ON p.{Quote(primaryKey.Name)} = t.{Quote(parent.Name)}
              ORDER BY t.{Quote(label)} LIMIT {MaxOptions}
              """;
        await using var scope = await _queryService.UseDataAsync();
        var records = await scope.Db.Connection.QueryAsync(sql);
        var options = new List<Option>();
        foreach (IDictionary<string, object?> record in records)
        {
            var id = Convert.ToString(record["id"], CultureInfo.InvariantCulture);
            if (string.IsNullOrEmpty(id))
                continue;
            var text = Convert.ToString(record["label"], CultureInfo.InvariantCulture)?.Trim();
            var group = Convert.ToString(record["parent"], CultureInfo.InvariantCulture)?.Trim();
            options.Add(new Option(id, string.IsNullOrEmpty(text) ? id : text, string.IsNullOrEmpty(group) ? null : group));
        }
        return options;
    }

    /// <summary>For each text, the value earlier rows with exactly that text were given, when at least two thirds agree.</summary>
    private async Task<Dictionary<string, (string Value, double Share)>> HistoryAsync(TableDef table, ColumnDef target, string match, List<string> texts)
    {
        var result = new Dictionary<string, (string, double)>(StringComparer.Ordinal);
        await using var scope = await _queryService.UseDataAsync();
        foreach (var chunk in texts.Chunk(200))
        {
            var rows = await scope.Db.Connection.QueryAsync(
                $"""
                SELECT {Quote(match)} AS text, {Quote(target.Name)} AS value, COUNT(*) AS n
                FROM {Quote(table.Name)}
                WHERE {Quote(target.Name)} IS NOT NULL AND {(_systemPool.Provider == DatabaseProvider.Postgres ? $"{Quote(match)} = ANY(@Texts)" : $"{Quote(match)} IN @Texts")}
                GROUP BY {Quote(match)}, {Quote(target.Name)}
                """,
                new { Texts = chunk.ToArray() });
            foreach (var group in rows.Cast<IDictionary<string, object?>>()
                         .Select(r => (Text: Convert.ToString(r["text"], CultureInfo.InvariantCulture)!, Value: Convert.ToString(r["value"], CultureInfo.InvariantCulture)!, N: Convert.ToInt64(r["n"], CultureInfo.InvariantCulture)))
                         .GroupBy(r => r.Text))
            {
                var total = group.Sum(g => g.N);
                var top = group.OrderByDescending(g => g.N).First();
                if (top.N * 3 >= total * 2)
                    result[group.Key] = (top.Value, (double)top.N / total);
            }
        }
        return result;
    }

    // ── The AI ───────────────────────────────────────────────────────────

    private static readonly string SystemPrompt =
        """
        You categorise rows of a table in Bento, a personal data workspace — usually bank bookings: payee, purpose, amount, date.

        You get the categories to choose from (with their group, if they have one), examples of rows the user categorised before, and the rows to categorise. For each row choose the one category that fits best, the way the user would; follow the examples where they apply.

        Answer with JSON only, no other text: an array with one object per row, in this form:
        [{"key": "<the row's key>", "category": "<the category's id exactly as given>", "confidence": 0.0-1.0, "keyword": "<the stable part of the text that identifies the payee or the kind of booking, e.g. REWE, Netflix, Miete>", "column": "<the column the keyword is in>"}]

        - "confidence" is how sure you are: 0.9 and above only when the payee or purpose clearly says it; below 0.5 when you are guessing.
        - "keyword" is short, without numbers, dates, references, city names or legal forms (GmbH, AG, SE), so that it matches the same payee next month. Leave it empty when nothing in the text is stable.
        - Signs: negative amounts are usually expenses, positive ones income or refunds.
        - When no category fits at all, use "category": null.
        """;

    private async Task<(List<Suggestion> Suggestions, AiRun Run)> AskAiAsync(
        TableDef table, ColumnDef column, List<Option> options, List<string> matchColumns,
        List<(string Key, Dictionary<string, string?> Values)> rows, McpCaller caller, CancellationToken ct)
    {
        var contextColumns = ContextColumns(table, column, rows.SelectMany(r => r.Values.Keys));
        var examples = await ExamplesAsync(table, column, contextColumns, options);

        // Rows with the same text get the same answer: ask once.
        var unique = rows
            .GroupBy(r => string.Join("\u001f", contextColumns.Select(c => r.Values.GetValueOrDefault(c) ?? "")), StringComparer.OrdinalIgnoreCase)
            .ToList();

        var optionsJson = new JsonArray(options.Select(o => (JsonNode)new JsonObject
        {
            ["id"] = o.Value,
            ["name"] = o.Label,
            ["group"] = o.Group
        }).ToArray());

        var byValue = options.ToDictionary(o => o.Value, StringComparer.OrdinalIgnoreCase);
        var byLabel = options.GroupBy(o => o.Label, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var suggestions = new List<Suggestion>();
        var cost = 0m;
        var duration = 0;
        var model = "";
        using var gate = new SemaphoreSlim(AiParallelBatches);

        var batches = unique.Chunk(AiBatchSize).Select(async batch =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var items = new JsonArray(batch.Select((group, index) =>
                {
                    var item = new JsonObject { ["key"] = "r" + index.ToString(CultureInfo.InvariantCulture) };
                    foreach (var name in contextColumns)
                        item[name] = Short(group.First().Values.GetValueOrDefault(name));
                    return (JsonNode)item;
                }).ToArray());
                var message = new JsonObject
                {
                    ["table"] = table.DisplayName ?? table.Name,
                    ["column"] = column.DisplayName ?? column.Name,
                    ["categories"] = optionsJson.DeepClone(),
                    ["examples"] = examples.DeepClone(),
                    ["rows"] = items
                }.ToJsonString();

                var result = await _agent.RunAsync("categorise", caller, SystemPrompt, message, [], ct);
                var answers = ParseAnswers(result.Text);
                lock (suggestions)
                {
                    cost += result.CostUsd;
                    duration = Math.Max(duration, result.DurationMs);
                    model = result.Model;
                    for (var index = 0; index < batch.Length; index++)
                    {
                        if (!answers.TryGetValue("r" + index.ToString(CultureInfo.InvariantCulture), out var answer) || answer.Category is null)
                            continue;
                        var option = byValue.GetValueOrDefault(answer.Category) ?? byLabel.GetValueOrDefault(answer.Category);
                        if (option is null)
                            continue;
                        var keywordColumn = answer.Column is not null && matchColumns.Contains(answer.Column, StringComparer.OrdinalIgnoreCase)
                            ? matchColumns.First(c => string.Equals(c, answer.Column, StringComparison.OrdinalIgnoreCase))
                            : matchColumns.FirstOrDefault();
                        foreach (var (key, values) in batch[index])
                        {
                            // A keyword only helps when it really is in the row's text.
                            var keyword = answer.Keyword is { Length: >= 2 } k && keywordColumn is not null &&
                                          (values.GetValueOrDefault(keywordColumn) ?? "").Contains(k, StringComparison.OrdinalIgnoreCase)
                                ? k
                                : null;
                            suggestions.Add(new Suggestion(key, option.Value, option.Label, "ai", Math.Clamp(answer.Confidence ?? 0.5, 0, 1), keyword, keyword is null ? null : keywordColumn, null));
                        }
                    }
                }
            }
            finally
            {
                gate.Release();
            }
        }).ToList();
        await Task.WhenAll(batches);

        return (suggestions, new AiRun(model, cost, rows.Count, duration));
    }

    /// <summary>What the AI sees of a row: the text columns, amounts and dates, never the target itself.</summary>
    private static List<string> ContextColumns(TableDef table, ColumnDef target, IEnumerable<string> present)
    {
        var names = new HashSet<string>(present, StringComparer.OrdinalIgnoreCase);
        return table.Columns
            .Where(c => c != target && !c.PrimaryKey && names.Contains(c.Name) &&
                        c.Type is ColumnType.Text or ColumnType.Currency or ColumnType.Float or ColumnType.Integer or ColumnType.Date or ColumnType.Enum or ColumnType.Reference)
            .Select(c => c.Name)
            .ToList();
    }

    private async Task<JsonArray> ExamplesAsync(TableDef table, ColumnDef target, List<string> contextColumns, List<Option> options)
    {
        var primaryKey = table.Columns.FirstOrDefault(c => c.PrimaryKey);
        var textColumns = contextColumns
            .Select(name => table.Columns.First(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)))
            .Where(c => c.Type is ColumnType.Text or ColumnType.Currency or ColumnType.Float)
            .ToList();
        if (primaryKey is null || textColumns.Count == 0)
            return [];

        var select = string.Join(", ", textColumns.Select(c => Quote(c.Name)));
        await using var scope = await _queryService.UseDataAsync();
        var records = await scope.Db.Connection.QueryAsync(
            $"SELECT {select}, {Quote(target.Name)} AS bento_category FROM {Quote(table.Name)} WHERE {Quote(target.Name)} IS NOT NULL ORDER BY {Quote(primaryKey.Name)} DESC LIMIT {MaxExamples * 3}");

        var labels = options.ToDictionary(o => o.Value, o => o.Label, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var examples = new JsonArray();
        foreach (IDictionary<string, object?> record in records)
        {
            var value = Convert.ToString(record["bento_category"], CultureInfo.InvariantCulture) ?? "";
            if (!labels.ContainsKey(value))
                continue;
            var text = string.Join("|", textColumns.Where(c => c.Type == ColumnType.Text).Select(c => Convert.ToString(record[c.Name], CultureInfo.InvariantCulture)));
            if (!seen.Add(text + "\u001f" + value))
                continue;
            var example = new JsonObject();
            foreach (var column in textColumns)
                example[column.Name] = record[column.Name] switch
                {
                    null => null,
                    IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
                    var other => Short(Convert.ToString(other, CultureInfo.InvariantCulture))
                };
            example["category"] = value;
            examples.Add(example);
            if (examples.Count >= MaxExamples)
                break;
        }
        return examples;
    }

    internal sealed record Answer(string? Category, double? Confidence, string? Keyword, string? Column);

    /// <summary>Reads the JSON array from the answer, also inside a code fence or with text around it.</summary>
    internal static Dictionary<string, Answer> ParseAnswers(string text)
    {
        var result = new Dictionary<string, Answer>(StringComparer.Ordinal);
        var start = text.IndexOf('[');
        var end = text.LastIndexOf(']');
        if (start < 0 || end <= start)
            throw new AiProviderException("The AI's answer could not be read.");
        JsonElement array;
        try
        {
            array = JsonDocument.Parse(text[start..(end + 1)]).RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new AiProviderException("The AI's answer could not be read.");
        }
        foreach (var item in array.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object))
        {
            var key = String(item, "key");
            if (key is null)
                continue;
            double? confidence = item.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDouble() : null;
            result[key] = new Answer(String(item, "category"), confidence, String(item, "keyword")?.Trim(), String(item, "column"));
        }
        return result;
    }

    private static string? String(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null
            }
            : null;

    private static string? Short(string? text) =>
        text is null ? null : text.Length > MaxCellLength ? text[..MaxCellLength] : text;

    // ── Rules ────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<Rule>> ListRulesAsync(string tableName, string? column = null)
    {
        var table = _schemaCache.GetTable(tableName)
            ?? throw new ApiNotFoundException($"Table '{tableName}' does not exist.", new Dictionary<string, object?> { ["table"] = tableName });
        using var conn = await _systemPool.GetConnectionAsync();
        var rows = (await conn.QueryAsync<RuleRow>(
            $"""
            SELECT id, table_name AS TableName, target_column AS TargetColumn, match_column AS MatchColumn, pattern, value,
                   {SystemSqlDialect.TimestampText(_systemPool.Provider, "created_at")} AS CreatedAt
            FROM {RulesTable}
            WHERE workspace_id = @Ws AND table_name = @Table AND (@Column IS NULL OR target_column = @Column)
            ORDER BY target_column, LOWER(pattern)
            """,
            new { Table = table.Name, Column = column, Ws })).ToList();

        // Labels for the linked records, so the list reads "REWE → Groceries".
        var labels = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in rows.Select(r => r.TargetColumn).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var def = table.Columns.FirstOrDefault(c => string.Equals(c.Name, target, StringComparison.OrdinalIgnoreCase));
            labels[target] = def is null ? [] : (await LoadOptionsAsync(def)).ToDictionary(o => o.Value, o => o.Label, StringComparer.OrdinalIgnoreCase);
        }
        return rows.Select(r => new Rule(r.Id, r.TableName, r.TargetColumn, r.MatchColumn, r.Pattern, r.Value,
            labels.GetValueOrDefault(r.TargetColumn)?.GetValueOrDefault(r.Value), r.CreatedAt)).ToList();
    }

    /// <summary>Saves rules; a rule with the same column, match column and keyword is replaced.</summary>
    public async Task<IReadOnlyList<Rule>> SaveRulesAsync(string tableName, IReadOnlyList<RuleRequest> requests, long? userId)
    {
        var table = _schemaCache.GetTable(tableName)
            ?? throw new ApiNotFoundException($"Table '{tableName}' does not exist.", new Dictionary<string, object?> { ["table"] = tableName });
        var validated = new List<(string Column, string Match, string Pattern, string Value)>();
        foreach (var request in requests)
        {
            var (_, column) = ResolveColumn(table.Name, request.Column);
            var match = table.Columns.FirstOrDefault(c => c.Type == ColumnType.Text && string.Equals(c.Name, request.MatchColumn?.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new ApiValidationException($"'{request.MatchColumn}' is not a text column of '{table.Name}'.", new Dictionary<string, object?> { ["field"] = "matchColumn" });
            var pattern = request.Pattern?.Trim();
            if (string.IsNullOrEmpty(pattern) || pattern.Length < 2 || pattern.Length > 200)
                throw new ApiValidationException("A rule needs a keyword of 2 to 200 characters.", new Dictionary<string, object?> { ["field"] = "pattern" });
            var options = await LoadOptionsAsync(column);
            var option = options.FirstOrDefault(o => string.Equals(o.Value, request.Value?.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new ApiValidationException($"'{request.Value}' is not a value of '{column.Name}'.", new Dictionary<string, object?> { ["field"] = "value" });
            validated.Add((column.Name, match.Name, pattern, option.Value));
        }

        using var conn = await _systemPool.GetConnectionAsync();
        using var transaction = conn.BeginTransaction();
        foreach (var (column, match, pattern, value) in validated)
        {
            await conn.ExecuteAsync(
                $"DELETE FROM {RulesTable} WHERE workspace_id = @Ws AND table_name = @Table AND target_column = @Column AND match_column = @Match AND LOWER(pattern) = LOWER(@Pattern)",
                new { Table = table.Name, Column = column, Match = match, Pattern = pattern, Ws }, transaction);
            await conn.ExecuteAsync(
                $"""
                INSERT INTO {RulesTable} (workspace_id, table_name, target_column, match_column, pattern, value, created_by, created_at)
                VALUES (@Ws, @Table, @Column, @Match, @Pattern, @Value, @UserId, {SystemSqlDialect.TimestampParam(_systemPool.Provider, "@Now")})
                """,
                new { Table = table.Name, Column = column, Match = match, Pattern = pattern, Value = value, UserId = userId, Now = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture), Ws },
                transaction);
        }
        transaction.Commit();
        return await ListRulesAsync(table.Name);
    }

    public async Task DeleteRuleAsync(string tableName, long id)
    {
        var table = _schemaCache.GetTable(tableName)
            ?? throw new ApiNotFoundException($"Table '{tableName}' does not exist.", new Dictionary<string, object?> { ["table"] = tableName });
        using var conn = await _systemPool.GetConnectionAsync();
        var deleted = await conn.ExecuteAsync($"DELETE FROM {RulesTable} WHERE id = @Id AND workspace_id = @Ws AND table_name = @Table", new { Id = id, Table = table.Name, Ws });
        if (deleted == 0)
            throw new ApiNotFoundException($"Rule {id} not found for '{table.Name}'.");
    }

    private sealed class RuleRow
    {
        public long Id { get; init; }
        public string TableName { get; init; } = "";
        public string TargetColumn { get; init; } = "";
        public string MatchColumn { get; init; } = "";
        public string Pattern { get; init; } = "";
        public string Value { get; init; } = "";
        public string CreatedAt { get; init; } = "";
    }

    private static long Ws => Workspaces.WorkspaceContext.Id;
    private string RulesTable => _systemPool.Provider == DatabaseProvider.Postgres ? "_system.category_rules" : "category_rules";

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";
}
