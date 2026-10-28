using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Bento.Api.Db;
using Bento.Api.Db.Schema;
using Bento.Api.Exceptions;
using Bento.Api.Models;

namespace Bento.Api.Services.Notifications;

/// <summary>
/// Row events (Step 5.12): a rule that speaks when rows are added, changed
/// or deleted in a table — all of them, or those a condition holds for
/// (<c>status = "urgent"</c>), changes of some columns, writes from some
/// origins (a public form, an import …). <see cref="RowEvents"/> hands them
/// over in batches; a batch is one message, and a rule says at most
/// <see cref="MaxRowMessagesPerHour"/> an hour.
/// </summary>
public sealed partial class NotificationRuleService
{
    public static readonly string[] RowEvents = ["row.created", "row.updated", "row.deleted"];
    public const int MaxRowMessagesPerHour = 20;
    private const int ShownRows = 5;
    private const int ShownColumns = 8;

    private readonly ConcurrentDictionary<long, Queue<DateTime>> _rowMessages = new();

    private static bool IsRowEvent(string? name) => name is not null && RowEvents.Contains(name);

    /// <summary>The tables and operations rules listen to in this workspace, e.g. (orders, created).</summary>
    public async Task<IReadOnlyList<(string Table, string Op)>> RowListenersAsync() =>
        (await LoadAsync())
            .Where(r => r.Enabled && r.Kind == "event" && IsRowEvent(r.Config.Event) && r.Config.Source is not null)
            .Select(r => (r.Config.Source!.ToLowerInvariant(), r.Config.Event!["row.".Length..]))
            .Distinct()
            .ToList();

    private NotificationRuleConfig ValidateRowEvent(string name, NotificationRuleConfig config)
    {
        var table = _schemaCache.GetTable(config.Source?.Trim() ?? "")
            ?? throw Invalid("config.source", "Choose the table whose rows the rule watches.");
        var formula = string.IsNullOrWhiteSpace(config.Formula) ? null : config.Formula.Trim();
        if (formula is not null && name == "row.deleted")
            throw Invalid("config.formula", "Deleted rows are gone; a rule on them has no condition.");
        if (formula is not null && table.Columns.Count(c => c.PrimaryKey) != 1)
            throw Invalid("config.formula", $"{table.DisplayName ?? table.Name} has a composite key; a rule on its rows has no condition.");
        var columns = (config.Columns ?? []).Where(c => !string.IsNullOrWhiteSpace(c)).Select(c =>
            table.Columns.FirstOrDefault(column => string.Equals(column.Name, c.Trim(), StringComparison.OrdinalIgnoreCase) && ColumnTypes.HasPhysicalColumn(column.Type))?.Name
            ?? throw Invalid("config.columns", $"{table.DisplayName ?? table.Name} has no stored column '{c}'.")).Distinct().ToList();
        if (columns.Count > 0 && name != "row.updated")
            throw Invalid("config.columns", "Only a rule on changed rows watches columns.");
        var origins = (config.Origins ?? []).Select(o => o.Trim().ToLowerInvariant()).Where(o => o.Length > 0).Distinct().ToList();
        var unknown = origins.FirstOrDefault(o => !RowOrigin.Kinds.Contains(o));
        if (unknown is not null)
            throw Invalid("config.origins", $"'{unknown}' is no origin; they are {string.Join(", ", RowOrigin.Kinds)}.");
        return new NotificationRuleConfig
        {
            Event = name,
            Source = table.Name,
            Formula = formula,
            Columns = columns.Count > 0 ? columns : null,
            Origins = origins.Count > 0 ? origins : null
        };
    }

    /// <summary>A row condition is a formula true for the row: tried at once on the table, so a typo is said now.</summary>
    private async Task ValidateRowConditionAsync(NotificationRuleConfig config)
    {
        if (config.Formula is null)
            return;
        var (_, error) = await MatchingKeysAsync(_schemaCache.GetTable(config.Source!)!, config.Formula, []);
        if (error is not null)
            throw Invalid("config.formula", error);
    }

    /// <summary>
    /// The keys of <paramref name="keys"/> whose row the condition holds for —
    /// one query per 500 rows: <c>COUNT(WHERE condition)</c> per key. With no
    /// keys, only checks the formula.
    /// </summary>
    private async Task<(HashSet<string> Keys, string? Error)> MatchingKeysAsync(TableDef table, string condition, IReadOnlyList<string> keys)
    {
        var key = table.Columns.Single(c => c.PrimaryKey).Name;
        var matching = new HashSet<string>(StringComparer.Ordinal);
        var chunks = keys.Count == 0 ? [Array.Empty<string>()] : keys.Chunk(500).ToList();
        foreach (var chunk in chunks)
        {
            var request = new Dictionary<string, object?>
            {
                ["source"] = table.Name,
                ["groupBy"] = new[] { new { column = key } },
                ["measures"] = new[] { new { name = "hit", formula = $"COUNT(WHERE {condition})" } },
                ["filter"] = chunk.Length == 0
                    ? new Dictionary<string, object> { [key] = new { @null = true } }
                    : new Dictionary<string, object> { [key] = new { @in = chunk } },
                ["limit"] = 1000
            };
            AggregationQueryResponse result;
            try
            {
                result = await _aggregation.RunAsync(JsonSerializer.Deserialize<AggregationQueryRequest>(JsonSerializer.Serialize(request), Json)!);
            }
            catch (ApiException e)
            {
                return (matching, e.Message);
            }
            foreach (var row in result.Data)
            {
                if (decimal.TryParse(Convert.ToString(row.TryGetValue("hit", out var hit) ? hit : null, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var count) && count > 0)
                    matching.Add(Convert.ToString(row.TryGetValue(key, out var id) ? id : null, CultureInfo.InvariantCulture) ?? "");
            }
        }
        return (matching, null);
    }

    /// <summary>
    /// Rows were written (a batch from <see cref="RowEvents"/>): each rule on
    /// this table and operation picks the rows it is about and, if any, says so once.
    /// </summary>
    public async Task OnRowsChangedAsync(string tableName, string op, IReadOnlyList<RowChange> changes, int dropped, RowOrigin origin, CancellationToken cancellationToken = default)
    {
        var rows = changes.Where(c => c.Key.Length > 0).ToList();
        if (rows.Count == 0)
            return;
        var eventName = "row." + op;
        var rules = (await LoadAsync()).Where(r => r.Enabled && r.Kind == "event" && r.Config.Event == eventName &&
            string.Equals(r.Config.Source, tableName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (rules.Count == 0 || _schemaCache.GetTable(tableName) is not { } table)
            return;
        var workspace = await _workspace.GetAsync();
        foreach (var rule in rules)
        {
            try
            {
                if (rule.Config.Origins is { Count: > 0 } origins && !origins.Contains(origin.Kind))
                    continue;
                var picked = rows;
                if (rule.Config.Columns is { Count: > 0 } watched)
                    picked = picked.Where(r => r.Changed is null || r.Changed.Any(c => watched.Contains(c, StringComparer.OrdinalIgnoreCase))).ToList();
                if (rule.Config.Formula is { } condition && picked.Count > 0)
                {
                    var (matching, error) = await MatchingKeysAsync(table, condition, picked.Select(r => r.Key).Distinct().ToList());
                    if (error is not null)
                    {
                        await SaveStateAsync(rule.Id, rule.State with { CheckedAt = UtcNowIso(), Error = error });
                        continue;
                    }
                    picked = picked.Where(r => matching.Contains(r.Key)).ToList();
                }
                if (picked.Count == 0)
                    continue;
                var now = UtcNowIso();
                if (!TakeRowMessage(rule.Id, DateTime.UtcNow))
                {
                    await SaveStateAsync(rule.Id, rule.State with { CheckedAt = now, Error = $"More than {MaxRowMessagesPerHour} messages this hour; quiet until the hour is over." });
                    continue;
                }
                var notification = await RowNotificationAsync(rule, table, op, picked, picked.Count + (rule.Config.Formula is null && rule.Config.Columns is null ? dropped : 0), origin, workspace, test: false);
                await _notifications.SendAsync(rule.Channels, notification, cancellationToken);
                await SaveStateAsync(rule.Id, rule.State with { CheckedAt = now, FiredAt = now, Value = notification.Text, Error = null });
            }
            catch (Exception e) when (!cancellationToken.IsCancellationRequested && e is not OutOfMemoryException)
            {
                _logger?.LogWarning(e, "Notification rule {Rule} could not take the rows.", rule.Name);
            }
        }
    }

    private bool TakeRowMessage(long ruleId, DateTime now)
    {
        var times = _rowMessages.GetOrAdd(ruleId, _ => new Queue<DateTime>());
        lock (times)
        {
            while (times.Count > 0 && now - times.Peek() >= TimeSpan.FromHours(1))
                times.Dequeue();
            if (times.Count >= MaxRowMessagesPerHour)
                return false;
            times.Enqueue(now);
            return true;
        }
    }

    /// <summary>
    /// "New in Orders: Acme · 3 items" with the row's values, or "12 new rows
    /// in Orders" with the first few — and who wrote them.
    /// </summary>
    private async Task<Notification> RowNotificationAsync(Rule rule, TableDef table, string op, IReadOnlyList<RowChange> rows, int count, RowOrigin origin, WorkspaceSettings.Values workspace, bool test)
    {
        var tableName = table.DisplayName ?? table.Name;
        var crud = _services.GetRequiredService<CrudService>();
        var shown = new List<(RowChange Change, IDictionary<string, object?>? Row)>();
        foreach (var change in rows.Take(ShownRows))
        {
            var row = op == "deleted" ? change.Before : change.After;
            if (row is null && op != "deleted")
            {
                try
                {
                    row = await crud.GetByIdAsync(table, change.Key);
                }
                catch (ApiNotFoundException)
                {
                    // Gone again since: shown by its key.
                }
            }
            shown.Add((change, row));
        }
        var labels = new List<string>();
        foreach (var (change, row) in shown)
            labels.Add(row is null ? $"#{change.Key}" : await LabelAsync(table, row, workspace));

        var verb = op switch { "created" => "New in", "updated" => "Changed in", _ => "Deleted from" };
        var plural = op switch { "created" => "new rows in", "updated" => "rows changed in", _ => "rows deleted from" };
        string text;
        var facts = new List<NotificationFact>();
        if (count == 1 && shown.Count == 1)
        {
            var (change, row) = shown[0];
            text = $"{verb} {tableName}: {labels[0]} — {origin.Text}.";
            if (row is not null)
                facts.AddRange(await RowFactsAsync(table, row, op == "updated" ? change : null, workspace));
        }
        else
        {
            text = $"{count} {plural} {tableName} — {origin.Text}.";
            facts.AddRange(shown.Select((s, i) => new NotificationFact(labels[i], s.Change.Changed is { Count: > 0 } changed
                ? string.Join(", ", changed.Select(c => ColumnLabel(table, c)))
                : op == "deleted" ? "deleted" : "")));
            if (count > shown.Count)
                facts.Add(new NotificationFact("…", $"and {count - shown.Count} more"));
        }
        var link = op == "deleted" || count > 1
            ? Link($"/data/{Uri.EscapeDataString(table.Name)}")
            : Link($"/data/{Uri.EscapeDataString(table.Name)}?row={Uri.EscapeDataString(rows[0].Key)}");
        return new Notification(rule.Config.Event!, test ? $"Test: {rule.Name}" : rule.Name, test ? $"An example of what you will get. {text}" : text, link, facts);
    }

    /// <summary>The row's record label ("Acme · 2026-10-22"), its key when it has none.</summary>
    private async Task<string> LabelAsync(TableDef table, IDictionary<string, object?> row, WorkspaceSettings.Values workspace)
    {
        var parts = new List<string>();
        foreach (var name in CrudService.GetRecordLabelColumns(table))
        {
            var column = table.Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            if (column is not null && row.TryGetValue(column.Name, out var value) && value is not null && Convert.ToString(value, CultureInfo.InvariantCulture) is { Length: > 0 })
                parts.Add(await ValueTextAsync(column, value, workspace));
        }
        return parts.Count > 0 ? string.Join(" · ", parts) : $"#{row.FirstOrDefault(p => table.Columns.Any(c => c.PrimaryKey && c.Name == p.Key)).Value}";
    }

    /// <summary>The row's values (the changed ones first, as before → after), at most <see cref="ShownColumns"/>.</summary>
    private async Task<List<NotificationFact>> RowFactsAsync(TableDef table, IDictionary<string, object?> row, RowChange? update, WorkspaceSettings.Values workspace)
    {
        var facts = new List<NotificationFact>();
        var changed = update?.Changed ?? [];
        foreach (var name in changed)
        {
            var column = table.Columns.FirstOrDefault(c => c.Name == name);
            if (column is null)
                continue;
            var before = update!.Before is { } b && b.TryGetValue(name, out var old) ? await ValueTextAsync(column, old, workspace) : "—";
            var after = row.TryGetValue(name, out var now) ? await ValueTextAsync(column, now, workspace) : "—";
            facts.Add(new NotificationFact(ColumnLabel(table, name), $"{before} → {after}"));
        }
        foreach (var column in table.Columns.Where(c => !c.PrimaryKey && ColumnTypes.HasPhysicalColumn(c.Type) && !changed.Contains(c.Name)))
        {
            if (facts.Count >= ShownColumns)
                break;
            if (!row.TryGetValue(column.Name, out var value) || value is null || Convert.ToString(value, CultureInfo.InvariantCulture) is not { Length: > 0 })
                continue;
            facts.Add(new NotificationFact(ColumnLabel(table, column.Name), await ValueTextAsync(column, value, workspace)));
        }
        return facts;
    }

    private static string ColumnLabel(TableDef table, string name) =>
        table.Columns.FirstOrDefault(c => c.Name == name) is { } column && !string.IsNullOrWhiteSpace(column.DisplayName) ? column.DisplayName! : name;

    /// <summary>A value as people read it: money and numbers in the workspace's format, yes/no, a linked record by its label.</summary>
    private async Task<string> ValueTextAsync(ColumnDef column, object? value, WorkspaceSettings.Values workspace)
    {
        if (value is null)
            return "—";
        if (value is JsonElement { ValueKind: JsonValueKind.Null })
            return "—";
        switch (column.Type)
        {
            case ColumnType.Currency:
                return MessageFormat.Format(value, "currency", workspace);
            case ColumnType.Integer:
                return MessageFormat.Format(value, "integer", workspace);
            case ColumnType.Float:
                return MessageFormat.Format(value, "number", workspace);
            case ColumnType.Boolean:
                return value is true || string.Equals(Convert.ToString(value, CultureInfo.InvariantCulture), "true", StringComparison.OrdinalIgnoreCase) ? "yes" : "no";
            case ColumnType.Reference when column.References is { } target && _schemaCache.GetTable(target) is { } linked:
                try
                {
                    var record = await _services.GetRequiredService<CrudService>().GetByIdAsync(linked, Convert.ToString(value, CultureInfo.InvariantCulture)!);
                    return await LabelAsync(linked, record, workspace);
                }
                catch (ApiException)
                {
                    return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
                }
            default:
                var text = value is JsonElement element ? element.ToString() : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
                return text.Length > 200 ? text[..200] + "…" : text;
        }
    }

    /// <summary>What a test sends: the table's newest row as an example.</summary>
    private async Task<Notification> RowTestNotificationAsync(Rule rule, WorkspaceSettings.Values workspace)
    {
        var table = _schemaCache.GetTable(rule.Config.Source!) ?? throw new ApiUnprocessableEntityException($"The table '{rule.Config.Source}' no longer exists.");
        var crud = _services.GetRequiredService<CrudService>();
        var key = table.Columns.FirstOrDefault(c => c.PrimaryKey)?.Name;
        var newest = key is null ? null : (await crud.ListAsync(table, new ListQueryParameters { Page = 1, PerPage = 1, Sorts = [new SortClause(key, true)] })).Data.FirstOrDefault();
        if (newest is null)
            return new Notification(rule.Config.Event!, $"Test: {rule.Name}", $"{table.DisplayName ?? table.Name} has no rows yet; when one is {rule.Config.Event!["row.".Length..]}, you hear about it here.", Link($"/data/{Uri.EscapeDataString(table.Name)}"));
        var op = rule.Config.Event!["row.".Length..];
        var change = new RowChange(Convert.ToString(newest[key!], CultureInfo.InvariantCulture)!, op == "deleted" ? newest : null, op == "deleted" ? null : newest);
        return await RowNotificationAsync(rule, table, op, [change], 1, new RowOrigin("app", "someone"), workspace, test: true);
    }

    private static string RowSummary(NotificationRuleConfig config, SchemaCache schema)
    {
        var table = schema.GetTable(config.Source ?? "");
        var name = table?.DisplayName ?? config.Source;
        var what = config.Event switch
        {
            "row.created" => $"A row is added to {name}",
            "row.updated" => config.Columns is { Count: > 0 } columns
                ? $"{string.Join(" or ", columns.Select(c => table is null ? c : ColumnLabel(table, c)))} changes in {name}"
                : $"A row changes in {name}",
            _ => $"A row is deleted from {name}"
        };
        if (config.Formula is { } formula)
            what += $" where {formula}";
        if (config.Origins is { Count: > 0 } origins)
            what += $" · from {string.Join(", ", origins)}";
        return what;
    }
}
