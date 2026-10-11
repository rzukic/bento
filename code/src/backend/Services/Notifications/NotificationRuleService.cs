using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bento.Api.Configuration;
using Bento.Api.Db;
using Bento.Api.Db.Schema;
using Bento.Api.Exceptions;
using Bento.Api.Models;
using Dapper;

namespace Bento.Api.Services.Notifications;

/// <summary>
/// When Bento speaks (Step 5.3): conditions written as formulas, budgets that
/// run over, and events such as a failed import. Each rule says something once
/// when its condition starts to hold and stays quiet until it clears; what it
/// says goes out over its channels (Step 5.2).
/// </summary>
public sealed class NotificationRuleService
{
    public static readonly string[] Kinds = ["condition", "budget", "event"];
    public static readonly string[] Events = ["import.failed", "price.failed"];
    public static readonly string[] Periods = ["today", "this-week", "this-month", "this-year"];
    public static readonly string[] Ops = [">", ">=", "<", "<=", "=", "<>"];
    public const string TimeZoneId = "Europe/Berlin";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ISystemPool _systemPool;
    private readonly QueryService _queryService;
    private readonly SchemaCache _schemaCache;
    private readonly AggregationQueryService _aggregation;
    private readonly NotificationService _notifications;
    private readonly WorkspaceSettings _workspace;
    private readonly BackendRuntimeOptions _runtime;
    private readonly ILogger<NotificationRuleService>? _logger;
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _locks = new();

    public NotificationRuleService(
        ISystemPool systemPool,
        QueryService queryService,
        SchemaCache schemaCache,
        AggregationQueryService aggregation,
        NotificationService notifications,
        WorkspaceSettings workspace,
        BackendRuntimeOptions runtime,
        ILogger<NotificationRuleService>? logger = null)
    {
        _systemPool = systemPool;
        _queryService = queryService;
        _schemaCache = schemaCache;
        _aggregation = aggregation;
        _notifications = notifications;
        _workspace = workspace;
        _runtime = runtime;
        _logger = logger;
    }

    private bool Pg => _systemPool.Provider == DatabaseProvider.Postgres;
    private string Rules => Pg ? "_system.notification_rules" : "notification_rules";
    private string Dashboards => Pg ? "_system.dashboards" : "dashboards";
    private string NowParam => SystemSqlDialect.TimestampParam(_systemPool.Provider, "@Now");
    private string Ts(string column) => SystemSqlDialect.TimestampText(_systemPool.Provider, column);

    // ── Rules ────────────────────────────────────────────────────────────

    private sealed class RuleRow
    {
        public long Id { get; init; }
        public string Name { get; init; } = "";
        public string Kind { get; init; } = "";
        public string Config { get; init; } = "{}";
        public string Channels { get; init; } = "[]";
        public bool Enabled { get; init; }
        public string State { get; init; } = "{}";
        public string CreatedAt { get; init; } = "";
        public string UpdatedAt { get; init; } = "";
    }

    private sealed record Rule(long Id, string Name, string Kind, NotificationRuleConfig Config, IReadOnlyList<long> Channels, bool Enabled, NotificationRuleState State, string CreatedAt, string UpdatedAt);

    private async Task<List<Rule>> LoadAsync(long? id = null)
    {
        using var conn = await _systemPool.GetConnectionAsync();
        var rows = await conn.QueryAsync<RuleRow>(
            $"""
            SELECT id, name, kind, CAST(config AS TEXT) AS Config, channels, enabled, state,
                   {Ts("created_at")} AS CreatedAt, {Ts("updated_at")} AS UpdatedAt
            FROM {Rules}{(id is null ? "" : " WHERE id = @Id")} ORDER BY name
            """, new { Id = id });
        return rows.Select(r => new Rule(r.Id, r.Name, r.Kind,
            JsonSerializer.Deserialize<NotificationRuleConfig>(r.Config, Json) ?? new NotificationRuleConfig(),
            JsonSerializer.Deserialize<List<long>>(r.Channels, Json) ?? [],
            r.Enabled,
            JsonSerializer.Deserialize<NotificationRuleState>(r.State, Json) ?? new NotificationRuleState(),
            r.CreatedAt, r.UpdatedAt)).ToList();
    }

    private async Task<Rule> RequireAsync(long id) =>
        (await LoadAsync(id)).SingleOrDefault() ?? throw new ApiNotFoundException($"Notification rule {id} not found.");

    public async Task<IReadOnlyList<NotificationRuleResponse>> ListAsync()
    {
        var workspace = await _workspace.GetAsync();
        return (await LoadAsync()).Select(r => ToResponse(r, workspace)).ToList();
    }

    public async Task<NotificationRuleResponse> GetAsync(long id) => ToResponse(await RequireAsync(id), await _workspace.GetAsync());

    public async Task<NotificationRuleResponse> CreateAsync(NotificationRuleRequest request, int? userId)
    {
        var name = RequireName(request.Name);
        var kind = request.Kind?.Trim().ToLowerInvariant() ?? "";
        if (!Kinds.Contains(kind))
            throw Invalid("kind", "The kind is condition, budget or event.");
        var config = await ValidateAsync(kind, request.Config ?? new NotificationRuleConfig());
        var channels = await ValidateChannelsAsync(request.Channels);
        using var conn = await _systemPool.GetConnectionAsync();
        var id = await conn.ExecuteScalarAsync<long>(
            $"""
            INSERT INTO {Rules} (name, kind, config, channels, enabled, state, created_by, created_at, updated_at)
            VALUES (@Name, @Kind, {SystemSqlDialect.JsonParam(_systemPool.Provider, "@Config")}, @Channels, @Enabled, @State, @CreatedBy, {NowParam}, {NowParam})
            RETURNING id
            """,
            new { Name = name, Kind = kind, Config = JsonSerializer.Serialize(config, Json), Channels = JsonSerializer.Serialize(channels), Enabled = request.Enabled ?? true, State = "{}", CreatedBy = userId, Now = UtcNowIso() });
        return await GetAsync(id);
    }

    public async Task<NotificationRuleResponse> UpdateAsync(long id, NotificationRuleRequest request)
    {
        var existing = await RequireAsync(id);
        if (request.Kind is not null && !string.Equals(request.Kind.Trim(), existing.Kind, StringComparison.OrdinalIgnoreCase))
            throw Invalid("kind", "A rule keeps its kind; create a new rule for another one.");
        var name = request.Name is null ? existing.Name : RequireName(request.Name);
        var config = request.Config is null ? existing.Config : await ValidateAsync(existing.Kind, request.Config);
        var channels = request.Channels is null ? existing.Channels : await ValidateChannelsAsync(request.Channels);
        // A changed condition starts afresh: it speaks again when it holds.
        var state = request.Config is null ? existing.State : new NotificationRuleState();
        using var conn = await _systemPool.GetConnectionAsync();
        await conn.ExecuteAsync(
            $"""
            UPDATE {Rules} SET name = @Name, config = {SystemSqlDialect.JsonParam(_systemPool.Provider, "@Config")}, channels = @Channels,
                   enabled = @Enabled, state = @State, updated_at = {NowParam}
            WHERE id = @Id
            """,
            new { Id = id, Name = name, Config = JsonSerializer.Serialize(config, Json), Channels = JsonSerializer.Serialize(channels), Enabled = request.Enabled ?? existing.Enabled, State = JsonSerializer.Serialize(state, Json), Now = UtcNowIso() });
        return await GetAsync(id);
    }

    public async Task DeleteAsync(long id)
    {
        await RequireAsync(id);
        using var conn = await _systemPool.GetConnectionAsync();
        await conn.ExecuteAsync($"DELETE FROM {Rules} WHERE id = @Id", new { Id = id });
    }

    private async Task SaveStateAsync(long id, NotificationRuleState state)
    {
        using var conn = await _systemPool.GetConnectionAsync();
        await conn.ExecuteAsync($"UPDATE {Rules} SET state = @State WHERE id = @Id", new { Id = id, State = JsonSerializer.Serialize(state, Json) });
    }

    // ── Validation ───────────────────────────────────────────────────────

    private async Task<NotificationRuleConfig> ValidateAsync(string kind, NotificationRuleConfig config)
    {
        switch (kind)
        {
            case "condition":
            {
                var table = _schemaCache.GetTable(config.Source?.Trim() ?? "")
                    ?? throw Invalid("config.source", "Choose the table the value comes from.");
                if (string.IsNullOrWhiteSpace(config.Formula))
                    throw Invalid("config.formula", "Write the value as a formula, e.g. SUM(amount WHERE amount < 0).");
                var period = string.IsNullOrWhiteSpace(config.Period) ? null : config.Period.Trim().ToLowerInvariant();
                if (period is not null && !Periods.Contains(period))
                    throw Invalid("config.period", "The period is today, this-week, this-month or this-year.");
                string? dateColumn = null;
                if (period is not null)
                {
                    var column = table.Columns.FirstOrDefault(c => string.Equals(c.Name, config.DateColumn?.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (column is null || column.Type is not (ColumnType.Date or ColumnType.DateTime))
                        throw Invalid("config.dateColumn", $"Choose the date column of {table.DisplayName ?? table.Name} the period applies to.");
                    dateColumn = column.Name;
                }
                var op = config.Op?.Trim() switch { "≠" or "!=" => "<>", "≥" => ">=", "≤" => "<=", var o => o };
                if (op is null || !Ops.Contains(op))
                    throw Invalid("config.op", "Compare with >, >=, <, <=, = or <>.");
                if (config.Threshold is null)
                    throw Invalid("config.threshold", "Enter the number to compare with.");
                var every = string.IsNullOrWhiteSpace(config.Every) ? "hour" : config.Every.Trim().ToLowerInvariant();
                if (every is not ("hour" or "day"))
                    throw Invalid("config.every", "Check every hour or every day.");
                if (every == "day" && !TimeOnly.TryParseExact(config.At?.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                    throw Invalid("config.at", "Give the time of day as HH:mm, e.g. 08:00.");
                // The formula runs once now: a typo is said at once, not at the first check.
                var trial = await EvaluateConditionAsync(config with { Source = table.Name, Period = period, DateColumn = dateColumn });
                if (trial.Error is not null)
                    throw Invalid("config.formula", trial.Error);
                return new NotificationRuleConfig
                {
                    Source = table.Name,
                    Formula = config.Formula.Trim(),
                    Period = period,
                    DateColumn = dateColumn,
                    Op = op,
                    Threshold = config.Threshold,
                    Every = every,
                    At = every == "day" ? config.At!.Trim() : null
                };
            }
            case "budget":
            {
                if (config.DashboardId is null || string.IsNullOrWhiteSpace(config.WidgetId))
                    throw Invalid("config.widgetId", "Choose the budget widget of a dashboard.");
                await LoadBudgetAsync(config.DashboardId.Value, config.WidgetId.Trim());
                var percent = config.Percent ?? 100;
                if (percent is <= 0 or > 1000)
                    throw Invalid("config.percent", "Say at how many percent of the limit to tell you, e.g. 100 or 90.");
                return new NotificationRuleConfig { DashboardId = config.DashboardId, WidgetId = config.WidgetId.Trim(), Percent = percent };
            }
            default:
            {
                var name = config.Event?.Trim().ToLowerInvariant();
                if (name is null || !Events.Contains(name))
                    throw Invalid("config.event", "The event is import.failed or price.failed.");
                return new NotificationRuleConfig { Event = name };
            }
        }
    }

    private async Task<List<long>> ValidateChannelsAsync(IReadOnlyList<long>? channels)
    {
        var ids = (channels ?? []).Distinct().ToList();
        if (ids.Count == 0)
            throw Invalid("channels", "Choose at least one channel to tell you on (Settings → Notifications).");
        var known = (await _notifications.ListAsync()).Select(c => c.Id).ToHashSet();
        var missing = ids.Where(id => !known.Contains(id)).ToList();
        if (missing.Count > 0)
            throw Invalid("channels", $"There is no channel {string.Join(", ", missing)}.");
        return ids;
    }

    // ── Conditions ───────────────────────────────────────────────────────

    private sealed record ConditionValue(decimal? Value, string? Type, string? Error);

    /// <summary>The rule's formula over its table, in its period (today, this month … in Europe/Berlin).</summary>
    private async Task<ConditionValue> EvaluateConditionAsync(NotificationRuleConfig config)
    {
        JsonElement? filter = null;
        if (config.Period is { } period && config.DateColumn is { } dateColumn)
        {
            var (from, to) = PeriodRange(period, Today());
            filter = JsonSerializer.SerializeToElement(new Dictionary<string, object>
            {
                [dateColumn] = new Dictionary<string, string> { ["gte"] = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ["lt"] = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) }
            });
        }
        IReadOnlyList<QueryVariableResult> results;
        try
        {
            results = await _aggregation.EvaluateVariablesAsync(new QueryVariablesRequest
            {
                Variables = [new QueryVariable { Name = "value", Source = config.Source, Formula = config.Formula, Filter = filter }]
            });
        }
        catch (ApiException e)
        {
            return new ConditionValue(null, null, e.Message);
        }
        var result = results.Single();
        if (result.Error is not null)
            return new ConditionValue(null, null, result.Error);
        if (result.Value is null)
            return new ConditionValue(0, result.Type, null);
        return decimal.TryParse(Convert.ToString(result.Value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? new ConditionValue(number, result.Type, null)
            : new ConditionValue(null, result.Type, "The formula gives text or yes/no; a rule compares a number.");
    }

    private static bool Holds(decimal value, string op, decimal threshold) => op switch
    {
        ">" => value > threshold,
        ">=" => value >= threshold,
        "<" => value < threshold,
        "<=" => value <= threshold,
        "=" => value == threshold,
        _ => value != threshold
    };

    // ── Budgets ──────────────────────────────────────────────────────────

    private sealed record BudgetConfig(string Source, string Amount, string Date, string Category, string Limit, bool Negative, string DashboardName);

    private async Task<BudgetConfig> LoadBudgetAsync(long dashboardId, string widgetId)
    {
        string? json;
        string? name;
        using (var conn = await _systemPool.GetConnectionAsync())
        {
            var row = await conn.QuerySingleOrDefaultAsync<(string Name, string Json)?>(
                $"SELECT name, CAST(dashboard_json AS TEXT) FROM {Dashboards} WHERE id = @Id", new { Id = dashboardId });
            (name, json) = row is { } found ? (found.Name, found.Json) : (null, null);
        }
        if (json is null)
            throw Invalid("config.dashboardId", $"There is no dashboard {dashboardId}.");
        var widget = (JsonNode.Parse(json)?["widgets"] as JsonArray)?
            .FirstOrDefault(w => string.Equals(w?["id"]?.GetValue<string>(), widgetId, StringComparison.Ordinal));
        var budget = widget?["data"]?["budget"];
        string? Text(string field) => budget?[field]?.GetValue<string>();
        if (widget?["type"]?.GetValue<string>() != "budget" || Text("source") is not { } source || Text("amount") is not { } amount ||
            Text("date") is not { } date || Text("category") is not { } category || Text("limit") is not { Length: > 0 } limit)
            throw Invalid("config.widgetId", $"The dashboard “{name}” has no budget widget {widgetId} with a limit column.");
        return new BudgetConfig(source, amount, date, category, limit, Text("expenses") != "positive", name!);
    }

    private sealed record BudgetLine(string Key, string Label, decimal Spent, decimal Limit);

    /// <summary>Spent this month per category against its monthly limit (as the budget widget counts it).</summary>
    private async Task<List<BudgetLine>> BudgetLinesAsync(BudgetConfig budget)
    {
        var source = _schemaCache.GetTable(budget.Source) ?? throw new InvalidOperationException($"The table {budget.Source} no longer exists.");
        var link = source.Columns.FirstOrDefault(c => string.Equals(c.Name, budget.Category, StringComparison.OrdinalIgnoreCase));
        var categories = link?.References is { } target ? _schemaCache.GetTable(target) : null;
        var key = categories?.Columns.FirstOrDefault(c => c.PrimaryKey);
        if (categories is null || key is null)
            throw new InvalidOperationException($"{budget.Category} in {budget.Source} links to no table.");
        var (from, to) = PeriodRange("this-month", Today());
        var spent = await _aggregation.RunAsync(new AggregationQueryRequest
        {
            Source = source.Name,
            Filter = JsonSerializer.SerializeToElement(new Dictionary<string, object>
            {
                [budget.Date] = new Dictionary<string, string> { ["gte"] = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ["lt"] = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) }
            }),
            GroupBy = [new QueryGroupBy { Column = link!.Name }],
            Measures = [new QueryMeasure { Name = "total", Fn = "sum", Column = budget.Amount }],
            Limit = AggregationQueryService.MaxLimit
        });
        var totals = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var row in spent.Data)
        {
            var id = Convert.ToString(row.TryGetValue(link.Name, out var v) ? v : null, CultureInfo.InvariantCulture);
            if (id is null || !decimal.TryParse(Convert.ToString(row["total"], CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var total))
                continue;
            totals[id] = budget.Negative ? -total : total;
        }

        var label = CrudService.GetRecordLabelColumns(categories).FirstOrDefault() ?? key.Name;
        var lines = new List<BudgetLine>();
        await using var scope = await _queryService.UseDataAsync();
        foreach (IDictionary<string, object?> row in await scope.Db.Connection.QueryAsync(
                     $"SELECT {Q(key.Name)} AS k, {Q(label)} AS l, {Q(budget.Limit)} AS m FROM {Q(categories.Name)}"))
        {
            var id = Convert.ToString(row["k"], CultureInfo.InvariantCulture)!;
            if (row["m"] is null || !decimal.TryParse(Convert.ToString(row["m"], CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var limit) || limit <= 0)
                continue;
            lines.Add(new BudgetLine(id, Convert.ToString(row["l"], CultureInfo.InvariantCulture) ?? id, totals.GetValueOrDefault(id), limit));
        }
        return lines;
    }

    // ── Checking ─────────────────────────────────────────────────────────

    /// <summary>Checks a rule now (by hand): the state is kept, and it speaks if it newly holds.</summary>
    public async Task<NotificationRuleCheck> CheckAsync(long id, CancellationToken cancellationToken = default) =>
        await CheckLockedAsync(await RequireAsync(id), send: true, cancellationToken);

    /// <summary>
    /// Sends what the rule would say now, whether or not it holds, to try the
    /// channels and the wording; its state is left as it is.
    /// </summary>
    public async Task<NotificationRuleCheck> TestAsync(long id, CancellationToken cancellationToken = default)
    {
        var rule = await RequireAsync(id);
        var workspace = await _workspace.GetAsync();
        Notification notification;
        switch (rule.Kind)
        {
            case "condition":
            {
                var value = await EvaluateConditionAsync(rule.Config);
                if (value.Error is not null)
                    throw new ApiUnprocessableEntityException(value.Error);
                notification = ConditionNotification(rule, value, workspace, test: true);
                break;
            }
            case "budget":
            {
                var budget = await LoadBudgetAsync(rule.Config.DashboardId!.Value, rule.Config.WidgetId!);
                var lines = await BudgetLinesAsync(budget);
                var top = lines.OrderByDescending(l => l.Spent / l.Limit).Take(3).ToList();
                notification = BudgetNotification(rule, budget, top, workspace, test: true);
                break;
            }
            default:
                notification = new Notification(rule.Config.Event!, $"Test: {rule.Name}",
                    rule.Config.Event == "import.failed"
                        ? "When a scheduled import fails, this is where you hear about it, with the reason."
                        : "When a price update fails, this is where you hear about it, with the reason.",
                    Link(rule.Config.Event == "import.failed" ? "/data" : "/functions"));
                break;
        }
        var deliveries = await _notifications.SendAsync(rule.Channels, notification, cancellationToken);
        return new NotificationRuleCheck(rule.State, true, notification, deliveries);
    }

    private async Task<NotificationRuleCheck> CheckLockedAsync(Rule rule, bool send, CancellationToken cancellationToken)
    {
        var gate = _locks.GetOrAdd(rule.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            // Read again under the lock: another check may have just spoken.
            rule = (await LoadAsync(rule.Id)).SingleOrDefault() ?? rule;
            var workspace = await _workspace.GetAsync();
            var now = UtcNowIso();
            NotificationRuleState state;
            Notification? notification = null;
            switch (rule.Kind)
            {
                case "condition":
                {
                    var value = await EvaluateConditionAsync(rule.Config);
                    if (value.Error is not null)
                    {
                        state = rule.State with { CheckedAt = now, Error = value.Error };
                        break;
                    }
                    var holds = Holds(value.Value!.Value, rule.Config.Op!, rule.Config.Threshold!.Value);
                    state = rule.State with
                    {
                        Active = holds,
                        Value = MessageFormat.Format(value.Value, value.Type, workspace),
                        CheckedAt = now,
                        Error = null
                    };
                    if (holds && !rule.State.Active)
                        notification = ConditionNotification(rule, value, workspace, test: false);
                    break;
                }
                case "budget":
                {
                    try
                    {
                        var budget = await LoadBudgetAsync(rule.Config.DashboardId!.Value, rule.Config.WidgetId!);
                        var lines = await BudgetLinesAsync(budget);
                        var period = Today().ToString("yyyy-MM", CultureInfo.InvariantCulture);
                        var told = rule.State.Period == period ? (rule.State.Keys ?? []).ToHashSet() : [];
                        var over = lines.Where(l => l.Spent >= l.Limit * rule.Config.Percent!.Value / 100).ToList();
                        var fresh = over.Where(l => !told.Contains(l.Key)).ToList();
                        state = rule.State with
                        {
                            Active = over.Count > 0,
                            Value = over.Count == 0 ? "All categories within budget" : string.Join(", ", over.Select(l => $"{l.Label} {Percent(l)}")),
                            CheckedAt = now,
                            Error = null,
                            Period = period,
                            Keys = told.Concat(fresh.Select(l => l.Key)).ToList()
                        };
                        if (fresh.Count > 0)
                            notification = BudgetNotification(rule, budget, fresh, workspace, test: false);
                    }
                    catch (Exception e) when (e is ApiException or InvalidOperationException)
                    {
                        state = rule.State with { CheckedAt = now, Error = e.Message };
                    }
                    break;
                }
                default:
                    // Events speak when they happen (OnEventAsync), not when checked.
                    state = rule.State with { CheckedAt = now };
                    break;
            }

            IReadOnlyList<NotificationDelivery> deliveries = [];
            if (notification is not null && send)
            {
                deliveries = await _notifications.SendAsync(rule.Channels, notification, cancellationToken);
                state = state with { FiredAt = now };
            }
            await SaveStateAsync(rule.Id, state);
            return new NotificationRuleCheck(state, notification is not null, notification, deliveries);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Conditions and budgets whose check is due: hourly ones after an hour, daily ones at their time.</summary>
    public async Task<IReadOnlyList<long>> DueAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var zone = Zone();
        var due = new List<long>();
        foreach (var rule in (await LoadAsync()).Where(r => r.Enabled && r.Kind is "condition" or "budget"))
        {
            DateTimeOffset? checkedAt = rule.State.CheckedAt is { } text ? DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal) : null;
            if (rule.Kind == "condition" && rule.Config.Every == "day")
            {
                var local = TimeZoneInfo.ConvertTime(now, zone);
                var at = TimeOnly.ParseExact(rule.Config.At ?? "08:00", "HH:mm", CultureInfo.InvariantCulture);
                var todayAt = new DateTimeOffset(local.Date + at.ToTimeSpan(), zone.GetUtcOffset(local.Date + at.ToTimeSpan()));
                if (todayAt <= now && (checkedAt is null || checkedAt < todayAt))
                    due.Add(rule.Id);
            }
            else if (checkedAt is null || now - checkedAt >= TimeSpan.FromMinutes(59))
            {
                due.Add(rule.Id);
            }
        }
        return due;
    }

    public async Task<NotificationRuleCheck> CheckScheduledAsync(long id, CancellationToken cancellationToken) =>
        await CheckLockedAsync(await RequireAsync(id), send: true, cancellationToken);

    /// <summary>After rows arrived (a scheduled import): hourly conditions and budgets look at once.</summary>
    public async Task OnDataChangedAsync(CancellationToken cancellationToken = default)
    {
        foreach (var rule in (await LoadAsync()).Where(r => r.Enabled && r.Kind is "condition" or "budget" && r.Config.Every != "day"))
        {
            try
            {
                await CheckLockedAsync(rule, send: true, cancellationToken);
            }
            catch (Exception e) when (!cancellationToken.IsCancellationRequested)
            {
                _logger?.LogWarning(e, "Notification rule {Rule} could not be checked.", rule.Name);
            }
        }
    }

    /// <summary>
    /// Something happened: an import or a price source ran (<paramref name="failed"/>
    /// or not). A failure is told once; it is told again only after the source worked in between.
    /// </summary>
    public async Task OnEventAsync(string eventName, string key, bool failed, Notification notification, CancellationToken cancellationToken = default)
    {
        foreach (var rule in (await LoadAsync()).Where(r => r.Enabled && r.Kind == "event" && r.Config.Event == eventName))
        {
            try
            {
                var gate = _locks.GetOrAdd(rule.Id, _ => new SemaphoreSlim(1, 1));
                await gate.WaitAsync(cancellationToken);
                try
                {
                    var current = (await LoadAsync(rule.Id)).SingleOrDefault() ?? rule;
                    var failing = (current.State.Keys ?? []).ToHashSet();
                    var now = UtcNowIso();
                    var state = current.State with { CheckedAt = now };
                    if (failed && failing.Add(key))
                    {
                        await _notifications.SendAsync(current.Channels, notification with { Link = notification.Link ?? Link("/") }, cancellationToken);
                        state = state with { FiredAt = now, Value = notification.Title };
                    }
                    else if (!failed)
                    {
                        failing.Remove(key);
                    }
                    await SaveStateAsync(current.Id, state with { Active = failing.Count > 0, Keys = failing.ToList() });
                }
                finally
                {
                    gate.Release();
                }
            }
            catch (Exception e) when (!cancellationToken.IsCancellationRequested)
            {
                _logger?.LogWarning(e, "Notification rule {Rule} could not take the event {Event}.", rule.Name, eventName);
            }
        }
    }

    // ── Messages ─────────────────────────────────────────────────────────

    private Notification ConditionNotification(Rule rule, ConditionValue value, WorkspaceSettings.Values workspace, bool test)
    {
        var shown = MessageFormat.Format(value.Value, value.Type, workspace);
        var threshold = MessageFormat.Format(rule.Config.Threshold, value.Type, workspace);
        var table = _schemaCache.GetTable(rule.Config.Source!);
        var facts = new List<NotificationFact>
        {
            new("Value", shown),
            new("Condition", $"{OpText(rule.Config.Op!)} {threshold}")
        };
        if (rule.Config.Period is { } period)
            facts.Add(new NotificationFact("Period", PeriodText(period)));
        facts.Add(new NotificationFact("From", table?.DisplayName ?? rule.Config.Source!));
        return new Notification(
            "rule.condition",
            test ? $"Test: {rule.Name}" : rule.Name,
            test
                ? $"The value is {shown} now; the rule tells you when it is {OpText(rule.Config.Op!)} {threshold}."
                : $"{shown} — {OpText(rule.Config.Op!)} {threshold}{(rule.Config.Period is { } p ? $" ({PeriodText(p).ToLowerInvariant()})" : "")}.",
            Link($"/data/{Uri.EscapeDataString(rule.Config.Source!)}"),
            facts);
    }

    private Notification BudgetNotification(Rule rule, BudgetConfig budget, IReadOnlyList<BudgetLine> lines, WorkspaceSettings.Values workspace, bool test)
    {
        var month = Today().ToDateTime(TimeOnly.MinValue).ToString("MMMM yyyy", CultureInfo.InvariantCulture);
        var text = lines.Count == 0
            ? $"Nothing has spent {rule.Config.Percent:0.#} % of its budget in {month} yet."
            : lines.Count == 1
                ? $"{lines[0].Label} is at {Percent(lines[0])} of its budget for {month}."
                : $"{lines.Count} categories are at {rule.Config.Percent:0.#} % of their budget or more for {month}.";
        return new Notification(
            "rule.budget",
            test ? $"Test: {rule.Name}" : rule.Name,
            test ? $"The fullest budgets now. {text}" : text,
            Link($"/dashboards/{rule.Config.DashboardId}"),
            lines.Select(l => new NotificationFact(l.Label, $"{MessageFormat.Amount(l.Spent, workspace)} of {MessageFormat.Amount(l.Limit, workspace)} · {Percent(l)}")).ToList());
    }

    private static string Percent(BudgetLine line) =>
        (line.Limit == 0 ? 0 : line.Spent / line.Limit * 100).ToString("0", CultureInfo.InvariantCulture) + " %";

    private static string OpText(string op) => op switch
    {
        ">" => "above",
        ">=" => "at or above",
        "<" => "below",
        "<=" => "at or below",
        "=" => "exactly",
        _ => "not"
    };

    private static string PeriodText(string period) => period switch
    {
        "today" => "Today",
        "this-week" => "This week",
        "this-month" => "This month",
        _ => "This year"
    };

    private NotificationRuleResponse ToResponse(Rule rule, WorkspaceSettings.Values workspace)
    {
        var summary = rule.Kind switch
        {
            "condition" => $"{rule.Config.Formula} {rule.Config.Op} {rule.Config.Threshold?.ToString(CultureInfo.InvariantCulture)}" +
                           (rule.Config.Period is { } p ? $" · {PeriodText(p).ToLowerInvariant()}" : "") +
                           (rule.Config.Every == "day" ? $" · daily at {rule.Config.At}" : " · hourly"),
            "budget" => $"A budget at {rule.Config.Percent:0.#} % or more",
            _ => rule.Config.Event == "import.failed" ? "A scheduled import fails" : "A price update fails"
        };
        return new NotificationRuleResponse(rule.Id, rule.Name, rule.Kind, rule.Config, rule.Channels, rule.Enabled, rule.State, summary, rule.CreatedAt, rule.UpdatedAt);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>A link into Bento when the server knows its public address (PUBLIC_URL).</summary>
    public string? Link(string path) =>
        string.IsNullOrWhiteSpace(_runtime.PublicUrl) ? null : _runtime.PublicUrl.Trim().TrimEnd('/') + path;

    private static TimeZoneInfo Zone() =>
        TimeZoneInfo.TryFindSystemTimeZoneById(TimeZoneId, out var zone) ? zone : TimeZoneInfo.Utc;

    private static DateOnly Today() => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Zone()).DateTime);

    /// <summary>[from, to) of a period around <paramref name="today"/>; a week starts on Monday.</summary>
    public static (DateOnly From, DateOnly To) PeriodRange(string period, DateOnly today) => period switch
    {
        "today" => (today, today.AddDays(1)),
        "this-week" => (today.AddDays(-(((int)today.DayOfWeek + 6) % 7)), today.AddDays(7 - (((int)today.DayOfWeek + 6) % 7))),
        "this-month" => (new DateOnly(today.Year, today.Month, 1), new DateOnly(today.Year, today.Month, 1).AddMonths(1)),
        _ => (new DateOnly(today.Year, 1, 1), new DateOnly(today.Year + 1, 1, 1))
    };

    private static string RequireName(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            throw Invalid("name", "Give the rule a name, e.g. Groceries over budget.");
        if (trimmed.Length > 255)
            throw Invalid("name", "The name is too long.");
        return trimmed;
    }

    private static string Q(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    private static string UtcNowIso() => DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

    private static ApiValidationException Invalid(string field, string message) =>
        new(message, new Dictionary<string, object?> { ["field"] = field });
}
