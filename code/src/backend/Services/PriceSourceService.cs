using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Bento.Api.Db;
using Bento.Api.Db.Schema;
using Bento.Api.Exceptions;
using Bento.Api.Formula;
using Bento.Api.Formula.Sandbox;
using Bento.Api.Models;
using Dapper;

namespace Bento.Api.Services;

/// <summary>
/// Price sources (Step 4.2): a user's function fetches prices, Bento keeps them
/// as history in a table of the user's model. Nothing about a provider is
/// built in. A source runs:
/// <list type="bullet">
/// <item>when a dashboard is opened or refreshed and its prices are older than the freshness window ("latest": the last days of every key),</item>
/// <item>once a day at the configured time (closing prices, and the history of keys that have none yet),</item>
/// <item>on demand.</item>
/// </list>
/// Each run is logged with the requests it made, so a daily budget protects free API tiers.
/// </summary>
public sealed class PriceSourceService
{
    public const int DefaultFreshMinutes = 15;
    public const string DefaultDailyAt = "22:30";
    public const string DefaultTimeZone = "Europe/Berlin";
    private const int LatestDays = 7;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ISystemPool _systemPool;
    private readonly IDataPool _dataPool;
    private readonly SchemaCache _schemaCache;
    private readonly CrudService _crud;
    private readonly CustomFunctionCatalog _functions;
    private readonly CustomFunctionService _functionService;
    private readonly ICustomValueRecalculation? _recalculation;
    private readonly ILogger<PriceSourceService>? _logger;
    private readonly Notifications.NotificationRuleService? _rules;
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _locks = new();

    public PriceSourceService(
        ISystemPool systemPool,
        IDataPool dataPool,
        SchemaCache schemaCache,
        CrudService crud,
        CustomFunctionCatalog functions,
        CustomFunctionService functionService,
        ICustomValueRecalculation? recalculation = null,
        ILogger<PriceSourceService>? logger = null,
        Notifications.NotificationRuleService? rules = null)
    {
        _rules = rules;
        _systemPool = systemPool;
        _dataPool = dataPool;
        _schemaCache = schemaCache;
        _crud = crud;
        _functions = functions;
        _functionService = functionService;
        _recalculation = recalculation;
        _logger = logger;
    }

    private bool SystemPg => _systemPool.Provider == DatabaseProvider.Postgres;
    private bool DataPg => _dataPool.Provider == DatabaseProvider.Postgres;
    private string Sources => SystemPg ? "_system.price_sources" : "price_sources";
    private string Runs => SystemPg ? "_system.price_source_runs" : "price_source_runs";
    private string NowParam => SystemSqlDialect.TimestampParam(_systemPool.Provider, "@Now");
    private string Ts(string column) => SystemSqlDialect.TimestampText(_systemPool.Provider, column);

    // ── Sources ──────────────────────────────────────────────────────────

    private sealed class SourceRow
    {
        public long Id { get; init; }
        public string Name { get; init; } = "";
        public string Config { get; init; } = "{}";
        public bool Enabled { get; init; }
    }

    private sealed record Source(long Id, string Name, bool Enabled, PriceSourceConfig Config);

    private async Task<List<Source>> LoadAsync(long? id = null)
    {
        using var conn = await _systemPool.GetConnectionAsync();
        var rows = await conn.QueryAsync<SourceRow>(
            $"SELECT id, name, CAST(config AS TEXT) AS Config, enabled FROM {Sources}{(id is null ? "" : " WHERE id = @Id")} ORDER BY name",
            new { Id = id });
        return rows.Select(r => new Source(r.Id, r.Name, r.Enabled,
            JsonSerializer.Deserialize<PriceSourceConfig>(r.Config, Json) ?? new PriceSourceConfig())).ToList();
    }

    private async Task<Source> RequireAsync(long id) =>
        (await LoadAsync(id)).SingleOrDefault() ?? throw new ApiNotFoundException($"Price source {id} not found.");

    public async Task<IReadOnlyList<PriceSourceResponse>> ListAsync()
    {
        var result = new List<PriceSourceResponse>();
        foreach (var source in await LoadAsync())
            result.Add(await ToResponseAsync(source));
        return result;
    }

    public async Task<PriceSourceResponse> GetAsync(long id) => await ToResponseAsync(await RequireAsync(id));

    public async Task<PriceSourceResponse> CreateAsync(PriceSourceRequest request)
    {
        var name = RequireName(request.Name);
        var config = Validate(request.Config);
        using var conn = await _systemPool.GetConnectionAsync();
        if (await conn.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {Sources} WHERE LOWER(name) = LOWER(@Name)", new { Name = name }) > 0)
            throw new ApiConflictException($"A price source named '{name}' already exists.", new Dictionary<string, object?> { ["field"] = "name" });
        var id = await conn.ExecuteScalarAsync<long>(
            $"""
            INSERT INTO {Sources} (name, config, enabled, created_at, updated_at)
            VALUES (@Name, {SystemSqlDialect.JsonParam(_systemPool.Provider, "@Config")}, @Enabled, {NowParam}, {NowParam})
            RETURNING id
            """,
            new { Name = name, Config = JsonSerializer.Serialize(config, Json), Enabled = request.Enabled ?? true, Now = UtcNowIso() });
        return await GetAsync(id);
    }

    public async Task<PriceSourceResponse> UpdateAsync(long id, PriceSourceRequest request)
    {
        var existing = await RequireAsync(id);
        var name = request.Name is null ? existing.Name : RequireName(request.Name);
        var config = request.Config is null ? existing.Config : Validate(request.Config);
        using var conn = await _systemPool.GetConnectionAsync();
        if (await conn.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {Sources} WHERE LOWER(name) = LOWER(@Name) AND id <> @Id", new { Name = name, Id = id }) > 0)
            throw new ApiConflictException($"A price source named '{name}' already exists.", new Dictionary<string, object?> { ["field"] = "name" });
        await conn.ExecuteAsync(
            $"""
            UPDATE {Sources} SET name = @Name, config = {SystemSqlDialect.JsonParam(_systemPool.Provider, "@Config")}, enabled = @Enabled, updated_at = {NowParam}
            WHERE id = @Id
            """,
            new { Id = id, Name = name, Config = JsonSerializer.Serialize(config, Json), Enabled = request.Enabled ?? existing.Enabled, Now = UtcNowIso() });
        return await GetAsync(id);
    }

    public async Task DeleteAsync(long id)
    {
        await RequireAsync(id);
        using var conn = await _systemPool.GetConnectionAsync();
        await conn.ExecuteAsync($"DELETE FROM {Runs} WHERE source_id = @Id", new { Id = id });
        await conn.ExecuteAsync($"DELETE FROM {Sources} WHERE id = @Id", new { Id = id });
    }

    public async Task<IReadOnlyList<PriceSourceRun>> RunsAsync(long id, int limit = 20)
    {
        await RequireAsync(id);
        using var conn = await _systemPool.GetConnectionAsync();
        var rows = await conn.QueryAsync<RunRow>(
            $"""
            SELECT id, kind, {Ts("started_at")} AS StartedAt, {Ts("finished_at")} AS FinishedAt, requests, rows_written AS RowsWritten,
                   {Ts("last_quote_at")} AS LastQuoteAt, error
            FROM {Runs} WHERE source_id = @Id ORDER BY started_at DESC, id DESC LIMIT {Math.Clamp(limit, 1, 100)}
            """, new { Id = id });
        return rows.Select(r => new PriceSourceRun(r.Id, r.Kind, r.StartedAt, r.FinishedAt, r.Requests, r.RowsWritten, r.LastQuoteAt, r.Error)).ToList();
    }

    private sealed class RunRow
    {
        public long Id { get; init; }
        public string Kind { get; init; } = "";
        public string StartedAt { get; init; } = "";
        public string? FinishedAt { get; init; }
        public int Requests { get; init; }
        public int RowsWritten { get; init; }
        public string? LastQuoteAt { get; init; }
        public string? Error { get; init; }
    }

    // ── Validation ───────────────────────────────────────────────────────

    private PriceSourceConfig Validate(PriceSourceConfig? config)
    {
        if (config is null)
            throw Invalid("config", "Describe the source: the function and the prices table.");

        var function = RequireFunction(config.Function, "config.function");
        if (function.Arguments.Count != 3)
            throw Invalid("config.function", $"{function.Name} must take (symbols, from, to): the symbols to fetch and the first and last day.");
        if (function.ResultType != "any")
            throw Invalid("config.function", $"{function.Name} must give \"any\": a list of {{ symbol, date, price }}.");
        if (!string.IsNullOrWhiteSpace(config.LookupFunction))
        {
            var lookup = RequireFunction(config.LookupFunction, "config.lookupFunction");
            if (lookup.Arguments.Count != 1 || lookup.ResultType != "any")
                throw Invalid("config.lookupFunction", $"{lookup.Name} must take (keys) and give \"any\": [{{ key, symbol }}] or {{ key: symbol }}.");
        }

        var table = _schemaCache.GetTable(config.Table?.Trim() ?? "")
            ?? throw Invalid("config.table", $"Table '{config.Table}' does not exist.");
        var key = RequireColumn(table, config.KeyColumn, "config.keyColumn", ColumnType.Text, ColumnType.Enum, ColumnType.Uuid);
        var date = RequireColumn(table, config.DateColumn, "config.dateColumn", ColumnType.Date);
        var price = RequireColumn(table, config.PriceColumn, "config.priceColumn", ColumnType.Currency, ColumnType.Float);
        if (!table.Columns.Any(c => c.PrimaryKey))
            throw Invalid("config.table", $"{table.Name} needs a primary key.");

        string? keysFrom = null;
        if (!string.IsNullOrWhiteSpace(config.KeysFrom))
        {
            var parts = config.KeysFrom.Trim().Split('.', 2);
            var keysTable = parts.Length == 2 ? _schemaCache.GetTable(parts[0]) : null;
            var keysColumn = keysTable?.Columns.FirstOrDefault(c => string.Equals(c.Name, parts[1], StringComparison.OrdinalIgnoreCase));
            if (keysColumn is null || !ColumnTypes.HasPhysicalColumn(keysColumn.Type))
                throw Invalid("config.keysFrom", $"'{config.KeysFrom}' is not a column; write table.column, e.g. finance_trades.isin.");
            keysFrom = $"{keysTable!.Name}.{keysColumn.Name}";
        }

        DateOnly? historyFrom = null;
        if (!string.IsNullOrWhiteSpace(config.HistoryFrom))
        {
            if (!DateOnly.TryParseExact(config.HistoryFrom.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var from))
                throw Invalid("config.historyFrom", "historyFrom is a date (yyyy-MM-dd).");
            historyFrom = from;
        }

        var dailyAt = string.IsNullOrWhiteSpace(config.DailyAt) ? DefaultDailyAt : config.DailyAt.Trim();
        if (!TimeOnly.TryParseExact(dailyAt, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            throw Invalid("config.dailyAt", "dailyAt is a time of day (HH:mm), e.g. 22:30.");
        var timeZone = string.IsNullOrWhiteSpace(config.TimeZone) ? DefaultTimeZone : config.TimeZone.Trim();
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(timeZone, out _))
            throw Invalid("config.timeZone", $"Unknown time zone '{timeZone}', e.g. Europe/Berlin.");
        if (config.FreshMinutes is < 1 or > 24 * 60)
            throw Invalid("config.freshMinutes", "freshMinutes is between 1 and 1440.");
        if (config.MaxRequestsPerDay is < 1)
            throw Invalid("config.maxRequestsPerDay", "maxRequestsPerDay is at least 1, or empty for no limit.");

        var symbols = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in config.Symbols ?? [])
        {
            if (!string.IsNullOrWhiteSpace(k) && !string.IsNullOrWhiteSpace(v))
                symbols[k.Trim()] = v.Trim();
        }

        return new PriceSourceConfig
        {
            Function = function.Name,
            LookupFunction = string.IsNullOrWhiteSpace(config.LookupFunction) ? null : _functions.Functions[config.LookupFunction.Trim()].Name,
            Table = table.Name,
            KeyColumn = key.Name,
            DateColumn = date.Name,
            PriceColumn = price.Name,
            KeysFrom = keysFrom,
            Symbols = symbols,
            HistoryFrom = historyFrom?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            FreshMinutes = config.FreshMinutes ?? DefaultFreshMinutes,
            DailyAt = dailyAt,
            TimeZone = timeZone,
            MaxRequestsPerDay = config.MaxRequestsPerDay
        };
    }

    private CustomFunctionDef RequireFunction(string? name, string field)
    {
        if (string.IsNullOrWhiteSpace(name) || !_functions.Functions.TryGetValue(name.Trim(), out var function))
            throw Invalid(field, $"There is no function '{name}'; create it under Functions.");
        return function;
    }

    private static ColumnDef RequireColumn(TableDef table, string? name, string field, params ColumnType[] types)
    {
        var column = table.Columns.FirstOrDefault(c => string.Equals(c.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw Invalid(field, $"{table.Name} has no column '{name}'.");
        if (!types.Contains(column.Type))
            throw Invalid(field, $"'{column.Name}' is {column.Type.ToString().ToLowerInvariant()}; this needs {string.Join(" or ", types.Select(t => t.ToString().ToLowerInvariant()))}.");
        return column;
    }

    private static string RequireName(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            throw Invalid("name", "Give the source a name, e.g. Yahoo Finance.");
        if (trimmed.Length > 255)
            throw Invalid("name", "The name is too long.");
        return trimmed;
    }

    // ── Keys and symbols ─────────────────────────────────────────────────

    /// <summary>The keys to fetch: those in KeysFrom and those with a symbol.</summary>
    private async Task<List<string>> KeysAsync(PriceSourceConfig config)
    {
        var keys = new SortedSet<string>(config.Symbols?.Keys ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
        if (config.KeysFrom is { } from && from.Split('.', 2) is [var table, var column] && _schemaCache.GetTable(table) is not null)
        {
            using var conn = await _dataPool.GetConnectionAsync();
            foreach (var value in await conn.QueryAsync<string?>($"SELECT DISTINCT CAST({Q(column)} AS TEXT) FROM {Q(table)} WHERE {Q(column)} IS NOT NULL"))
            {
                var key = value?.Trim();
                if (!string.IsNullOrEmpty(key))
                    keys.Add(key);
            }
        }
        return keys.ToList();
    }

    private static string SymbolOf(PriceSourceConfig config, string key) =>
        config.Symbols is not null && config.Symbols.TryGetValue(key, out var symbol) ? symbol : key;

    /// <summary>Runs the lookup function for keys without a symbol; nothing is saved.</summary>
    public async Task<IReadOnlyList<SymbolSuggestion>> SuggestAsync(long id)
    {
        var source = await RequireAsync(id);
        if (source.Config.LookupFunction is not { } name || !_functions.Functions.TryGetValue(name, out var lookup))
            throw Invalid("config.lookupFunction", "This source has no lookup function for symbols.");
        var missing = (await KeysAsync(source.Config)).Where(k => source.Config.Symbols?.ContainsKey(k) != true).ToList();
        if (missing.Count == 0)
            return [];

        var (function, error) = JsFunction.Create(lookup, host: await _functionService.HostAsync(lookup));
        if (function is null)
            throw new ApiUnprocessableEntityException($"{lookup.Name}: {error}");
        using (function)
        {
            var result = await Task.Run(() => function.Call([JsonSerializer.SerializeToElement(missing)]));
            if (!result.Ok)
                throw new ApiUnprocessableEntityException($"{lookup.Name}: {result.Error}");
            var suggestions = new List<SymbolSuggestion>();
            if (result.Value is JsonElement { ValueKind: JsonValueKind.Array } list)
            {
                foreach (var item in list.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object && Text(item, "key") is { } key && Text(item, "symbol") is { } symbol)
                        suggestions.Add(new SymbolSuggestion(key, symbol));
                }
            }
            else if (result.Value is JsonElement { ValueKind: JsonValueKind.Object } map)
            {
                foreach (var property in map.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(property.Value.GetString()))
                        suggestions.Add(new SymbolSuggestion(property.Name, property.Value.GetString()!.Trim()));
                }
            }
            return suggestions.Where(s => missing.Contains(s.Key, StringComparer.Ordinal)).ToList();
        }
    }

    // ── Running ──────────────────────────────────────────────────────────

    /// <summary>
    /// Fetches for every enabled source writing to one of <paramref name="tables"/>
    /// (all when empty) whose last fetch is older than its freshness window.
    /// </summary>
    public async Task<PriceRefreshResponse> RefreshAsync(PriceRefreshRequest request, CancellationToken cancellationToken = default)
    {
        var tables = (request.Tables ?? []).Select(t => t.Trim()).Where(t => t.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var results = new List<PriceRefreshSource>();
        foreach (var source in (await LoadAsync()).Where(s => s.Enabled && (tables.Count == 0 || tables.Contains(s.Config.Table ?? ""))))
        {
            var status = await StatusAsync(source);
            var fresh = status.LastSuccessAt is { } last &&
                        DateTimeOffset.TryParse(last, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) &&
                        DateTimeOffset.UtcNow - at < TimeSpan.FromMinutes(source.Config.FreshMinutes ?? DefaultFreshMinutes);
            if (fresh && !request.Force)
            {
                results.Add(new PriceRefreshSource(source.Id, source.Name, source.Config.Table ?? "", false, 0, status.LastQuoteAt, null));
                continue;
            }
            var run = await RunAsync(source.Id, "latest", cancellationToken);
            results.Add(new PriceRefreshSource(source.Id, source.Name, source.Config.Table ?? "", true, run.RowsWritten, run.LastQuoteAt ?? status.LastQuoteAt, run.Error));
        }
        var lastQuote = results.Select(r => r.LastQuoteAt).Where(q => q is not null).Max();
        return new PriceRefreshResponse(results.Any(r => r.RowsWritten > 0), lastQuote, results);
    }

    /// <summary>
    /// One run: "latest" fetches the last days of every key, "history" the
    /// history of keys without enough rows, "daily" both.
    /// </summary>
    public async Task<PriceSourceRun> RunAsync(long id, string kind, CancellationToken cancellationToken = default)
    {
        if (kind is not ("latest" or "history" or "daily"))
            throw Invalid("kind", "kind is latest, history or daily.");
        var gate = _locks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var source = await RequireAsync(id);
            return await RunLockedAsync(source, kind, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<PriceSourceRun> RunLockedAsync(Source source, string kind, CancellationToken cancellationToken)
    {
        var started = UtcNowIso();
        long runId;
        using (var conn = await _systemPool.GetConnectionAsync())
        {
            runId = await conn.ExecuteScalarAsync<long>(
                $"INSERT INTO {Runs} (source_id, kind, started_at) VALUES (@Source, @Kind, {SystemSqlDialect.TimestampParam(_systemPool.Provider, "@Started")}) RETURNING id",
                new { Source = source.Id, Kind = kind, Started = started });
        }

        var requests = 0;
        var written = 0;
        string? lastQuote = null;
        string? error = null;
        try
        {
            var config = source.Config;
            var function = RequireFunction(config.Function, "config.function");
            var table = _schemaCache.GetTable(config.Table ?? "") ?? throw new InvalidOperationException($"The table '{config.Table}' no longer exists.");
            var budget = config.MaxRequestsPerDay is { } max ? max - await RequestsTodayAsync(source.Id) : int.MaxValue;
            if (budget <= 0)
                throw new InvalidOperationException($"The daily budget of {config.MaxRequestsPerDay} requests is used up; prices stay as they are until tomorrow.");

            var keys = await KeysAsync(config);
            if (keys.Count == 0)
                throw new InvalidOperationException(config.KeysFrom is null
                    ? "There are no symbols to fetch; add some to the source."
                    : $"{config.KeysFrom} has no values yet, so there is nothing to fetch.");

            var today = Today(config);
            var calls = new List<(List<string> Keys, DateOnly From)>();
            if (kind is "history" or "daily")
            {
                var historyFrom = config.HistoryFrom is { } h ? DateOnly.ParseExact(h, "yyyy-MM-dd", CultureInfo.InvariantCulture) : today.AddYears(-5);
                var earliest = await EarliestAsync(table, config, keys);
                foreach (var key in keys.Where(k => !earliest.TryGetValue(k, out var first) || first > historyFrom.AddDays(LatestDays)))
                    calls.Add(([key], historyFrom));
            }
            if (kind is "latest" or "daily")
                calls.Add((keys, today.AddDays(-LatestDays)));

            var host = await _functionService.HostAsync(function);
            var (instance, createError) = JsFunction.Create(function, host: host);
            if (instance is null)
                throw new InvalidOperationException(createError);
            using (instance)
            {
                foreach (var (callKeys, from) in calls)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (instance.Requests >= budget)
                        throw new InvalidOperationException($"The daily budget of {config.MaxRequestsPerDay} requests ran out during the run.");
                    var symbols = callKeys.Select(k => SymbolOf(config, k)).ToList();
                    var result = await Task.Run(() => instance.Call([JsonSerializer.SerializeToElement(symbols), from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)]), cancellationToken);
                    requests = instance.Requests;
                    if (!result.Ok)
                        throw new InvalidOperationException($"{function.Name}: {result.Error}");
                    var rows = ParseRows(result.Value, config, callKeys, out var quote);
                    if (quote is not null && (lastQuote is null || string.CompareOrdinal(quote, lastQuote) > 0))
                        lastQuote = quote;
                    written += await WriteAsync(table, config, rows);
                }
                requests = instance.Requests;
            }
            if (written > 0)
                _recalculation?.Request();
        }
        catch (Exception e) when (e is InvalidOperationException or ApiException or System.Data.Common.DbException)
        {
            error = e.Message;
            _logger?.LogWarning("Price source {Source} ({Kind}) failed: {Error}", source.Name, kind, e.Message);
        }

        using (var conn = await _systemPool.GetConnectionAsync())
        {
            await conn.ExecuteAsync(
                $"""
                UPDATE {Runs} SET finished_at = {NowParam}, requests = @Requests, rows_written = @Written,
                    last_quote_at = {SystemSqlDialect.TimestampParam(_systemPool.Provider, "@Quote")}, error = @Error
                WHERE id = @Id
                """,
                new { Id = runId, Requests = requests, Written = written, Quote = lastQuote, Error = error, Now = UtcNowIso() });
        }
        // Rules on failed price updates (Step 5.3): a failed daily run speaks; any run that works clears it.
        if (_rules is not null && (kind == "daily" || error is null))
            await _rules.OnEventAsync("price.failed", $"price:{source.Id}", error is not null,
                new Models.Notification("price.failed", $"Price update failed: {source.Name}", error ?? "", _rules.Link("/functions"),
                    [new Models.NotificationFact("Price source", source.Name), new Models.NotificationFact("Run", kind)]),
                cancellationToken);
        return (await RunsAsync(source.Id, 1)).First();
    }

    private sealed record PriceRow(string Key, DateOnly Date, decimal Price);

    /// <summary>
    /// The function's rows: <c>[{ symbol, date, price, time? }]</c>. A row may name
    /// the key instead of the symbol; rows for other symbols are ignored.
    /// </summary>
    private static List<PriceRow> ParseRows(object? value, PriceSourceConfig config, List<string> keys, out string? lastQuote)
    {
        lastQuote = null;
        if (value is not JsonElement { ValueKind: JsonValueKind.Array } list)
            throw new InvalidOperationException($"{config.Function} must return a list of {{ symbol, date, price }}.");
        var bySymbol = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in keys)
        {
            bySymbol.TryAdd(SymbolOf(config, key), key);
            bySymbol.TryAdd(key, key);
        }

        var rows = new Dictionary<(string, DateOnly), PriceRow>();
        var index = 0;
        foreach (var item in list.EnumerateArray())
        {
            index++;
            if (item.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException($"Row {index} is not an object {{ symbol, date, price }}.");
            var symbol = Text(item, "symbol") ?? Text(item, "key");
            if (symbol is null || !bySymbol.TryGetValue(symbol, out var key))
                continue;
            var dateText = Text(item, "date");
            if (dateText is null || dateText.Length < 10 ||
                !DateOnly.TryParseExact(dateText[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                throw new InvalidOperationException($"Row {index} ({symbol}) has no date (yyyy-MM-dd).");
            if (!item.TryGetProperty("price", out var priceElement) || priceElement.ValueKind != JsonValueKind.Number)
            {
                if (priceElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                    continue; // a day without a price
                throw new InvalidOperationException($"Row {index} ({symbol}, {dateText}) has no price number.");
            }
            // Four places, like a currency column: providers differ in float noise (129.13499450683594 vs 129.135).
            rows[(key, date)] = new PriceRow(key, date, Math.Round(priceElement.GetDecimal(), 4, MidpointRounding.AwayFromZero));

            var quote = Text(item, "time") is { } time &&
                        DateTimeOffset.TryParse(time, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var stamp)
                ? stamp.UtcDateTime.ToString("o", CultureInfo.InvariantCulture)
                : date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "T00:00:00.0000000Z";
            if (lastQuote is null || string.CompareOrdinal(quote, lastQuote) > 0)
                lastQuote = quote;
        }
        return rows.Values.ToList();
    }

    /// <summary>Inserts new days and updates changed prices; returns the rows written.</summary>
    private async Task<int> WriteAsync(TableDef table, PriceSourceConfig config, List<PriceRow> rows)
    {
        if (rows.Count == 0)
            return 0;
        var pk = table.Columns.First(c => c.PrimaryKey).Name;
        var from = rows.Min(r => r.Date).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var keys = rows.Select(r => r.Key).Distinct().ToArray();

        var existing = new Dictionary<(string, DateOnly), (string Id, decimal? Price)>();
        using (var conn = await _dataPool.GetConnectionAsync())
        {
            var found = await conn.QueryAsync(
                $"""
                SELECT {Q(pk)} AS id, {Q(config.KeyColumn!)} AS k, {Q(config.DateColumn!)} AS d, {Q(config.PriceColumn!)} AS p
                FROM {Q(table.Name)}
                WHERE {InKeys(Q(config.KeyColumn!))} AND {Q(config.DateColumn!)} >= {(DataPg ? "CAST(@From AS DATE)" : "@From")}
                """,
                new { Keys = keys, From = from });
            foreach (IDictionary<string, object?> row in found)
            {
                if (CustomFunctionValues.DateText(row["d"]) is not { } day)
                    continue;
                existing[(Convert.ToString(row["k"], CultureInfo.InvariantCulture)!, DateOnly.ParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture))] =
                    (Convert.ToString(row["id"], CultureInfo.InvariantCulture)!, ResultValues.ToDecimal(row["p"]));
            }
        }

        var written = 0;
        foreach (var row in rows.OrderBy(r => r.Key, StringComparer.Ordinal).ThenBy(r => r.Date))
        {
            var day = row.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (existing.TryGetValue((row.Key, row.Date), out var current))
            {
                if (current.Price is { } price && Math.Round(price, 4, MidpointRounding.AwayFromZero) == row.Price)
                    continue;
                await _crud.PatchAsync(table, current.Id, JsonSerializer.SerializeToElement(new Dictionary<string, object?> { [config.PriceColumn!] = row.Price }));
            }
            else
            {
                await _crud.CreateAsync(table, JsonSerializer.SerializeToElement(new Dictionary<string, object?>
                {
                    [config.KeyColumn!] = row.Key,
                    [config.DateColumn!] = day,
                    [config.PriceColumn!] = row.Price
                }));
            }
            written++;
        }
        return written;
    }

    private async Task<Dictionary<string, DateOnly>> EarliestAsync(TableDef table, PriceSourceConfig config, List<string> keys)
    {
        using var conn = await _dataPool.GetConnectionAsync();
        var rows = await conn.QueryAsync(
            $"SELECT {Q(config.KeyColumn!)} AS k, MIN({Q(config.DateColumn!)}) AS d FROM {Q(table.Name)} WHERE {InKeys(Q(config.KeyColumn!))} GROUP BY {Q(config.KeyColumn!)}",
            new { Keys = keys.ToArray() });
        var result = new Dictionary<string, DateOnly>(StringComparer.Ordinal);
        foreach (IDictionary<string, object?> row in rows)
        {
            if (CustomFunctionValues.DateText(row["d"]) is { } day)
                result[Convert.ToString(row["k"], CultureInfo.InvariantCulture)!] = DateOnly.ParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        return result;
    }

    // ── Status ───────────────────────────────────────────────────────────

    private async Task<int> RequestsTodayAsync(long id)
    {
        using var conn = await _systemPool.GetConnectionAsync();
        var since = DateTime.UtcNow.Date.ToString("o", CultureInfo.InvariantCulture);
        return await conn.ExecuteScalarAsync<int?>(
            $"SELECT SUM(requests) FROM {Runs} WHERE source_id = @Id AND started_at >= {SystemSqlDialect.TimestampParam(_systemPool.Provider, "@Since")}",
            new { Id = id, Since = since }) ?? 0;
    }

    private async Task<PriceSourceStatus> StatusAsync(Source source)
    {
        using var conn = await _systemPool.GetConnectionAsync();
        var last = await conn.QuerySingleOrDefaultAsync<RunRow>(
            $"SELECT id, kind, {Ts("started_at")} AS StartedAt, {Ts("finished_at")} AS FinishedAt, requests, rows_written AS RowsWritten, {Ts("last_quote_at")} AS LastQuoteAt, error FROM {Runs} WHERE source_id = @Id ORDER BY started_at DESC, id DESC LIMIT 1",
            new { source.Id });
        var lastSuccess = await conn.ExecuteScalarAsync<string?>(
            $"SELECT {Ts("finished_at")} FROM {Runs} WHERE source_id = @Id AND error IS NULL AND finished_at IS NOT NULL AND kind IN ('latest', 'daily') ORDER BY finished_at DESC LIMIT 1",
            new { source.Id });
        var lastQuote = await conn.ExecuteScalarAsync<string?>(
            $"SELECT {Ts("last_quote_at")} FROM {Runs} WHERE source_id = @Id AND last_quote_at IS NOT NULL ORDER BY last_quote_at DESC LIMIT 1",
            new { source.Id });
        var running = _locks.TryGetValue(source.Id, out var gate) && gate.CurrentCount == 0;
        return new PriceSourceStatus(last?.StartedAt, lastSuccess, lastQuote, last?.Error, await RequestsTodayAsync(source.Id), running);
    }

    private async Task<PriceSourceResponse> ToResponseAsync(Source source)
    {
        var keys = new List<PriceSourceKey>();
        try
        {
            var table = _schemaCache.GetTable(source.Config.Table ?? "");
            var all = await KeysAsync(source.Config);
            var latest = new Dictionary<string, (string Date, decimal? Price)>(StringComparer.Ordinal);
            if (table is not null && all.Count > 0)
            {
                using var conn = await _dataPool.GetConnectionAsync();
                var k = Q(source.Config.KeyColumn!);
                var d = Q(source.Config.DateColumn!);
                var rows = await conn.QueryAsync(
                    $"""
                    SELECT k, d, p FROM (
                        SELECT {k} AS k, {d} AS d, {Q(source.Config.PriceColumn!)} AS p, ROW_NUMBER() OVER (PARTITION BY {k} ORDER BY {d} DESC) AS n
                        FROM {Q(table.Name)} WHERE {InKeys(k)}
                    ) x WHERE n = 1
                    """, new { Keys = all.ToArray() });
                foreach (IDictionary<string, object?> row in rows)
                    latest[Convert.ToString(row["k"], CultureInfo.InvariantCulture)!] = (CustomFunctionValues.DateText(row["d"]) ?? "", ResultValues.ToDecimal(row["p"]));
            }
            keys.AddRange(all.Select(key => new PriceSourceKey(
                key, SymbolOf(source.Config, key), source.Config.Symbols?.ContainsKey(key) == true,
                latest.TryGetValue(key, out var l) ? l.Date : null,
                latest.TryGetValue(key, out var p) ? p.Price : null)));
        }
        catch (System.Data.Common.DbException e)
        {
            _logger?.LogWarning("Price source {Source}: keys could not be read: {Error}", source.Name, e.Message);
        }
        return new PriceSourceResponse(source.Id, source.Name, source.Enabled, source.Config, await StatusAsync(source), keys);
    }

    // ── Schedule ─────────────────────────────────────────────────────────

    /// <summary>Sources whose daily time has passed today (in their time zone) without a daily run.</summary>
    public async Task<IReadOnlyList<long>> DueDailyAsync()
    {
        var due = new List<long>();
        foreach (var source in (await LoadAsync()).Where(s => s.Enabled))
        {
            var zone = Zone(source.Config);
            var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone);
            var at = TimeOnly.ParseExact(source.Config.DailyAt ?? DefaultDailyAt, "HH:mm", CultureInfo.InvariantCulture);
            if (TimeOnly.FromDateTime(now.DateTime) < at)
                continue;
            var startOfDay = new DateTimeOffset(now.Date, now.Offset).ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
            using var conn = await _systemPool.GetConnectionAsync();
            var ran = await conn.ExecuteScalarAsync<long>(
                $"SELECT COUNT(*) FROM {Runs} WHERE source_id = @Id AND kind = 'daily' AND started_at >= {SystemSqlDialect.TimestampParam(_systemPool.Provider, "@Since")}",
                new { source.Id, Since = startOfDay });
            if (ran == 0)
                due.Add(source.Id);
        }
        return due;
    }

    private static TimeZoneInfo Zone(PriceSourceConfig config) =>
        TimeZoneInfo.TryFindSystemTimeZoneById(config.TimeZone ?? DefaultTimeZone, out var zone) ? zone : TimeZoneInfo.Utc;

    private static DateOnly Today(PriceSourceConfig config) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Zone(config)).DateTime);

    // ── Helpers ──────────────────────────────────────────────────────────

    private static string? Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;

    /// <summary>column IN the @Keys list: Npgsql binds a list as an array.</summary>
    private string InKeys(string column) => DataPg ? $"{column} = ANY(@Keys)" : $"{column} IN @Keys";

    private static string Q(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    private static string UtcNowIso() => DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

    private static ApiValidationException Invalid(string field, string message) =>
        new(message, new Dictionary<string, object?> { ["field"] = field });
}
