using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Bento.Api.Db;
using Bento.Api.Exceptions;
using Bento.Api.Models;
using Bento.Api.Services.Ai;
using Dapper;

namespace Bento.Api.Services.Import;

/// <summary>
/// Saved imports (Step 5.1): a file fetched from a URL, read and mapped like
/// an upload, on demand or on a schedule. Each run fetches, categorises new
/// rows by the rules and earlier rows (Step 4.8, no AI), imports, and logs
/// what it did or why it failed.
/// </summary>
public sealed class ImportSourceService
{
    private const int KeptRuns = 100;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ISystemPool _systemPool;
    private readonly SchemaCache _schemaCache;
    private readonly ImportService _import;
    private readonly ImportFetcher _fetcher;
    private readonly ImportFunctionRunner? _functions;
    private readonly CredentialService _credentials;
    private readonly CategoriseService _categorise;
    private readonly ProblemLog _problems;
    private readonly ICustomValueRecalculation? _recalculation;
    private readonly ILogger<ImportSourceService>? _logger;
    private readonly Notifications.NotificationRuleService? _rules;
    private readonly WorkspaceSettings? _workspace;
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _locks = new();

    public ImportSourceService(
        ISystemPool systemPool,
        SchemaCache schemaCache,
        ImportService import,
        ImportFetcher fetcher,
        CredentialService credentials,
        CategoriseService categorise,
        ProblemLog problems,
        ICustomValueRecalculation? recalculation = null,
        ILogger<ImportSourceService>? logger = null,
        Notifications.NotificationRuleService? rules = null,
        WorkspaceSettings? workspace = null,
        ImportFunctionRunner? functions = null)
    {
        _functions = functions;
        _rules = rules;
        _workspace = workspace;
        _systemPool = systemPool;
        _schemaCache = schemaCache;
        _import = import;
        _fetcher = fetcher;
        _credentials = credentials;
        _categorise = categorise;
        _problems = problems;
        _recalculation = recalculation;
        _logger = logger;
    }

    private bool Pg => _systemPool.Provider == DatabaseProvider.Postgres;
    private static long Ws => Workspaces.WorkspaceContext.Id;
    private string Sources => Pg ? "_system.import_sources" : "import_sources";
    private string Runs => Pg ? "_system.import_runs" : "import_runs";
    private string NowParam => SystemSqlDialect.TimestampParam(_systemPool.Provider, "@Now");
    private string Ts(string column) => SystemSqlDialect.TimestampText(_systemPool.Provider, column);

    // ── Sources ──────────────────────────────────────────────────────────

    private sealed class SourceRow
    {
        public long Id { get; init; }
        public string Name { get; init; } = "";
        public string TableName { get; init; } = "";
        public string Config { get; init; } = "{}";
        public bool Enabled { get; init; }
        public string CreatedAt { get; init; } = "";
        public string UpdatedAt { get; init; } = "";
    }

    private sealed record Source(long Id, string Name, string TableName, ImportSourceConfig Config, bool Enabled, string CreatedAt, string UpdatedAt);

    private async Task<List<Source>> LoadAsync(string? tableName = null, long? id = null)
    {
        using var conn = await _systemPool.GetConnectionAsync();
        var where = new List<string> { "workspace_id = @Ws" };
        if (tableName is not null)
            where.Add("table_name = @TableName");
        if (id is not null)
            where.Add("id = @Id");
        var rows = await conn.QueryAsync<SourceRow>(
            $"""
            SELECT id, name, table_name AS TableName, CAST(config AS TEXT) AS Config, enabled,
                   {Ts("created_at")} AS CreatedAt, {Ts("updated_at")} AS UpdatedAt
            FROM {Sources} WHERE {string.Join(" AND ", where)} ORDER BY name
            """,
            new { TableName = tableName, Id = id, Ws });
        return rows.Select(r => new Source(r.Id, r.Name, r.TableName,
            JsonSerializer.Deserialize<ImportSourceConfig>(r.Config, Json) ?? new ImportSourceConfig(), r.Enabled, r.CreatedAt, r.UpdatedAt)).ToList();
    }

    private async Task<Source> RequireAsync(string tableName, long id) =>
        (await LoadAsync(tableName, id)).SingleOrDefault()
        ?? throw new ApiNotFoundException($"Saved import {id} not found for '{tableName}'.");

    public async Task<IReadOnlyList<ImportSourceResponse>> ListAsync(string table)
    {
        var tableName = TableName(table);
        var result = new List<ImportSourceResponse>();
        foreach (var source in await LoadAsync(tableName))
            result.Add(await ToResponseAsync(source));
        return result;
    }

    public async Task<ImportSourceResponse> GetAsync(string table, long id) =>
        await ToResponseAsync(await RequireAsync(TableName(table), id));

    public async Task<ImportSourceResponse> CreateAsync(string table, ImportSourceRequest request, bool admin, int? userId)
    {
        var tableName = TableName(table);
        var name = RequireName(request.Name);
        var config = await ValidateAsync(request.Config, admin);
        using var conn = await _systemPool.GetConnectionAsync();
        await EnsureNameFreeAsync(conn, tableName, name, null);
        var id = await conn.ExecuteScalarAsync<long>(
            $"""
            INSERT INTO {Sources} (workspace_id, name, table_name, config, enabled, created_by, created_at, updated_at)
            VALUES (@Ws, @Name, @TableName, {SystemSqlDialect.JsonParam(_systemPool.Provider, "@Config")}, @Enabled, @CreatedBy, {NowParam}, {NowParam})
            RETURNING id
            """,
            new { Name = name, TableName = tableName, Config = JsonSerializer.Serialize(config, Json), Enabled = request.Enabled ?? true, CreatedBy = userId, Now = UtcNowIso(), Ws });
        return await GetAsync(tableName, id);
    }

    public async Task<ImportSourceResponse> UpdateAsync(string table, long id, ImportSourceRequest request, bool admin)
    {
        var tableName = TableName(table);
        var existing = await RequireAsync(tableName, id);
        var name = request.Name is null ? existing.Name : RequireName(request.Name);
        // Only admins may touch a saved import that reads credentials, or make one do so.
        if (!admin && UsesCredentials(existing.Config))
            throw new ApiForbiddenException("This import uses credentials from the vault; only admins can change it.");
        var config = request.Config is null ? existing.Config : await ValidateAsync(request.Config, admin);
        using var conn = await _systemPool.GetConnectionAsync();
        await EnsureNameFreeAsync(conn, tableName, name, id);
        await conn.ExecuteAsync(
            $"""
            UPDATE {Sources} SET name = @Name, config = {SystemSqlDialect.JsonParam(_systemPool.Provider, "@Config")}, enabled = @Enabled, updated_at = {NowParam}
            WHERE id = @Id
            """,
            new { Id = id, Name = name, Config = JsonSerializer.Serialize(config, Json), Enabled = request.Enabled ?? existing.Enabled, Now = UtcNowIso() });
        return await GetAsync(tableName, id);
    }

    public async Task DeleteAsync(string table, long id, bool admin)
    {
        var source = await RequireAsync(TableName(table), id);
        if (!admin && UsesCredentials(source.Config))
            throw new ApiForbiddenException("This import uses credentials from the vault; only admins can delete it.");
        using var conn = await _systemPool.GetConnectionAsync();
        await conn.ExecuteAsync($"DELETE FROM {Runs} WHERE source_id = @Id", new { Id = id });
        await conn.ExecuteAsync($"DELETE FROM {Sources} WHERE id = @Id", new { Id = id });
    }

    public async Task<IReadOnlyList<ImportSourceRun>> RunsAsync(string table, long id, int limit = 20)
    {
        await RequireAsync(TableName(table), id);
        return await LoadRunsAsync(id, limit);
    }

    private sealed class RunRow
    {
        public long Id { get; init; }
        public string Trigger { get; init; } = "";
        public string StartedAt { get; init; } = "";
        public string? FinishedAt { get; init; }
        public string Status { get; init; } = "";
        public int Total { get; init; }
        public int Inserted { get; init; }
        public int Updated { get; init; }
        public int Duplicates { get; init; }
        public int Invalid { get; init; }
        public int Categorised { get; init; }
        public long Bytes { get; init; }
        public string? Error { get; init; }
    }

    private async Task<List<ImportSourceRun>> LoadRunsAsync(long id, int limit)
    {
        using var conn = await _systemPool.GetConnectionAsync();
        var rows = await conn.QueryAsync<RunRow>(
            $"""
            SELECT id, run_trigger AS Trigger, {Ts("started_at")} AS StartedAt, {Ts("finished_at")} AS FinishedAt, status,
                   total, inserted, updated, duplicates, invalid, categorised, bytes, error
            FROM {Runs} WHERE source_id = @Id ORDER BY started_at DESC, id DESC LIMIT {Math.Clamp(limit, 1, KeptRuns)}
            """, new { Id = id });
        return rows.Select(r => new ImportSourceRun(r.Id, r.Trigger, r.StartedAt, r.FinishedAt, r.Status, r.Total, r.Inserted, r.Updated,
            r.Duplicates, r.Invalid, r.Categorised, r.Bytes, r.Error)).ToList();
    }

    private async Task<ImportSourceResponse> ToResponseAsync(Source source)
    {
        var last = (await LoadRunsAsync(source.Id, 1)).FirstOrDefault();
        var next = source.Enabled ? await NextRunAsync(source) : null;
        return new ImportSourceResponse(source.Id, source.Name, source.TableName, source.Config, source.Enabled, last,
            next?.ToString("o", CultureInfo.InvariantCulture), source.CreatedAt, source.UpdatedAt);
    }

    // ── Validation ───────────────────────────────────────────────────────

    private async Task<ImportSourceConfig> ValidateAsync(ImportSourceConfig? config, bool admin)
    {
        if (config is null)
            throw Invalid("config", "Send the import's config: url, mapping and schedule.");
        var hasUrl = !string.IsNullOrWhiteSpace(config.Url);
        var hasFunction = !string.IsNullOrWhiteSpace(config.Function);
        if (hasUrl == hasFunction)
            throw Invalid("config.url", "An import reads from a URL or from a function; give one of them.");
        if (hasFunction)
        {
            if (_functions is null)
                throw Invalid("config.function", "Functions cannot be used as an import source here.");
            var function = _functions.Require(config.Function);
            if (function.Credentials.Count > 0 && !admin)
                throw new ApiForbiddenException($"{function.Name} reads credentials from the vault; only admins can use it in an import.");
            config = config with { Function = function.Name, Url = null, Headers = null };
        }
        else
            _fetcher.Validate(config.Url, config.Headers);
        var credentials = hasFunction ? [] : ImportFetcher.CredentialNames(config.Url, config.Headers);
        if (credentials.Count > 0)
        {
            if (!admin)
                throw new ApiForbiddenException("Only admins can use credentials from the vault in an import.");
            var missing = await _credentials.MissingAsync(credentials);
            if (missing.Count > 0)
                throw Invalid("config.headers", $"The vault has no credential named {string.Join(", ", missing.Select(m => $"'{m}'"))}; add it under Functions → Credentials.");
        }
        if ((config.Mapping ?? []).All(m => string.IsNullOrWhiteSpace(m.Target)))
            throw Invalid("config.mapping", "Map at least one file column to a table column.");

        var schedule = config.Schedule ?? new ImportSchedule { Kind = "manual" };
        var kind = schedule.Kind?.Trim().ToLowerInvariant() ?? "manual";
        switch (kind)
        {
            case "manual":
                schedule = new ImportSchedule { Kind = "manual" };
                break;
            case "daily":
                if (!TimeOnly.TryParseExact(schedule.At?.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                    throw Invalid("config.schedule.at", "Give the time of day as HH:mm, e.g. 06:30.");
                // No zone of its own: the workspace's (Settings → Workspace), also after it changes.
                var zone = string.IsNullOrWhiteSpace(schedule.TimeZone) ? null : schedule.TimeZone.Trim();
                if (zone is not null && !TimeZoneInfo.TryFindSystemTimeZoneById(zone, out _))
                    throw Invalid("config.schedule.timeZone", $"Unknown time zone '{zone}'.");
                schedule = new ImportSchedule { Kind = "daily", At = schedule.At!.Trim(), TimeZone = zone };
                break;
            case "hours":
                if (schedule.Every is not (>= 1 and <= 168))
                    throw Invalid("config.schedule.every", "Run every 1 to 168 hours.");
                schedule = new ImportSchedule { Kind = "hours", Every = schedule.Every };
                break;
            default:
                throw Invalid("config.schedule.kind", "The schedule is manual, daily or hours.");
        }
        return config with { Url = config.Url?.Trim(), Schedule = schedule, CategoryColumn = string.IsNullOrWhiteSpace(config.CategoryColumn) ? null : config.CategoryColumn.Trim() };
    }

    // ── Running ──────────────────────────────────────────────────────────

    public async Task<ImportSourceRun> RunAsync(string table, long id, string trigger, CancellationToken cancellationToken = default)
    {
        var source = await RequireAsync(TableName(table), id);
        return await RunAsync(source, trigger, cancellationToken);
    }

    /// <summary>Runs a saved import by id, for the scheduler.</summary>
    public async Task<ImportSourceRun> RunScheduledAsync(long id, CancellationToken cancellationToken)
    {
        var source = (await LoadAsync(null, id)).SingleOrDefault() ?? throw new ApiNotFoundException($"Saved import {id} not found.");
        return await RunAsync(source, "schedule", cancellationToken);
    }

    private async Task<ImportSourceRun> RunAsync(Source source, string trigger, CancellationToken cancellationToken)
    {
        // One run per saved import at a time: "Run now" during a scheduled run waits for it.
        var gate = _locks.GetOrAdd(source.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await RunLockedAsync(source, trigger, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<ImportSourceRun> RunLockedAsync(Source source, string trigger, CancellationToken cancellationToken)
    {
        long runId;
        using (var conn = await _systemPool.GetConnectionAsync())
        {
            runId = await conn.ExecuteScalarAsync<long>(
                $"INSERT INTO {Runs} (workspace_id, source_id, run_trigger, started_at, status) VALUES (@Ws, @Source, @Trigger, {NowParam}, 'running') RETURNING id",
                new { Source = source.Id, Trigger = trigger, Now = UtcNowIso(), Ws });
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        ImportRunResponse? result = null;
        var categorised = 0;
        long bytes = 0;
        string? error = null;
        try
        {
            var config = source.Config;
            if (_schemaCache.GetTable(source.TableName) is null)
                throw new ApiNotFoundException($"The table '{source.TableName}' no longer exists.");
            var (content, fileName) = await ReadAsync(source, cancellationToken);
            bytes = content.Length;
            var request = new ImportRunRequest
            {
                Content = Convert.ToBase64String(content),
                FileName = fileName,
                Settings = config.Settings,
                Mapping = config.Mapping,
                KeyColumns = config.KeyColumns,
                UpdateChanged = config.UpdateChanged
            };
            if (config.CategoryColumn is { } column)
            {
                var assign = await CategoriseAsync(source.TableName, column, request, cancellationToken);
                categorised = assign.Count;
                if (assign.Count > 0)
                    request = request with { Assign = new ImportAssign { Column = column, Values = assign } };
            }
            result = await _import.RunAsync(source.TableName, request, dryRun: false);
            if (result.Inserted + result.Updated > 0)
                _recalculation?.Request();
        }
        catch (ApiException e)
        {
            error = e.Message;
        }
        catch (Exception e) when (!cancellationToken.IsCancellationRequested)
        {
            error = $"The import failed: {e.Message}";
            _logger?.LogWarning(e, "Saved import {Source} failed.", source.Name);
        }

        using (var conn = await _systemPool.GetConnectionAsync())
        {
            await conn.ExecuteAsync(
                $"""
                UPDATE {Runs} SET finished_at = {NowParam}, status = @Status, total = @Total, inserted = @Inserted, updated = @Updated,
                       duplicates = @Duplicates, invalid = @Invalid, categorised = @Categorised, bytes = @Bytes, error = @Error
                WHERE id = @Id
                """,
                new
                {
                    Id = runId,
                    Now = UtcNowIso(),
                    Status = error is null ? "ok" : "failed",
                    Total = result?.Total ?? 0,
                    Inserted = result?.Inserted ?? 0,
                    Updated = result?.Updated ?? 0,
                    Duplicates = result?.Duplicates ?? 0,
                    Invalid = result?.Invalid ?? 0,
                    Categorised = error is null ? categorised : 0,
                    Bytes = bytes,
                    Error = error
                });
            // The log keeps the newest runs per import.
            await conn.ExecuteAsync(
                $"DELETE FROM {Runs} WHERE source_id = @Source AND id NOT IN (SELECT id FROM {Runs} WHERE source_id = @Source ORDER BY id DESC LIMIT {KeptRuns})",
                new { Source = source.Id });
        }
        // Admins also find failed imports under Settings → Problems.
        if (error is not null)
            await _problems.RecordAsync("import", $"The saved import '{source.Name}' into {source.TableName} failed ({trigger}): {error}",
                source.Config.Function is { } function ? $"Function: {function}" : $"URL: {source.Config.Url}", watch.ElapsedMilliseconds);
        if (_rules is not null)
        {
            // Rules (Step 5.3): a failed scheduled run speaks, a run that works clears it; new rows are looked at.
            if (trigger == "schedule" || error is null)
                await _rules.OnEventAsync("import.failed", $"import:{source.Id}", error is not null,
                    new Notification("import.failed", $"Import failed: {source.Name}", error ?? "",
                        _rules.Link($"/data/{Uri.EscapeDataString(source.TableName)}/import"),
                        [new NotificationFact("Import", source.Name), new NotificationFact("Into", _schemaCache.GetTable(source.TableName)?.DisplayName ?? source.TableName)]),
                    cancellationToken);
            if (result is { } done && done.Inserted + done.Updated > 0)
                await _rules.OnDataChangedAsync(cancellationToken);
        }
        return (await LoadRunsAsync(source.Id, 1)).Single();
    }

    /// <summary>The file of a run: fetched from the URL, or what the function returns (it gets when the import last worked).</summary>
    private async Task<(byte[] Content, string FileName)> ReadAsync(Source source, CancellationToken cancellationToken)
    {
        if (source.Config.Function is { } name)
        {
            if (_functions is null)
                throw new InvalidOperationException("Functions cannot be used as an import source here.");
            string? lastOk;
            using (var conn = await _systemPool.GetConnectionAsync())
                lastOk = await conn.ExecuteScalarAsync<string?>(
                    $"SELECT {Ts("started_at")} FROM {Runs} WHERE source_id = @Id AND status = 'ok' ORDER BY started_at DESC LIMIT 1", new { source.Id });
            var file = await _functions.RunAsync(name, lastOk, cancellationToken);
            return (file.Bytes, file.FileName);
        }
        var fetched = await _fetcher.FetchAsync(source.Config.Url, source.Config.Headers, cancellationToken);
        return (fetched.Bytes, fetched.FileName);
    }

    private bool UsesCredentials(ImportSourceConfig config) =>
        ImportFetcher.CredentialNames(config.Url, config.Headers).Count > 0 || _functions?.UsesCredentials(config.Function) == true;

    /// <summary>
    /// New rows without a value in <paramref name="column"/>: what the rules and
    /// earlier rows give them, by file line (the import page's flow, without AI).
    /// </summary>
    private async Task<Dictionary<string, string?>> CategoriseAsync(string tableName, string column, ImportRunRequest request, CancellationToken cancellationToken)
    {
        var preview = await _import.RunAsync(tableName, request, dryRun: true);
        var open = preview.Rows
            .Where(r => r.Status == "new" && (!r.Display.TryGetValue(column, out var value) || string.IsNullOrEmpty(value)))
            .Select(r => new CategoriseService.SuggestRow
            {
                Key = r.Line.ToString(CultureInfo.InvariantCulture),
                Values = r.Display.Where(d => !string.Equals(d.Key, column, StringComparison.OrdinalIgnoreCase)).ToDictionary(d => d.Key, d => d.Value)
            })
            .ToList();
        var assign = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var chunk in open.Chunk(CategoriseService.MaxRows))
        {
            var answer = await _categorise.SuggestAsync(tableName, new CategoriseService.SuggestRequest { Column = column, Rows = chunk, Ai = false }, null, cancellationToken);
            foreach (var suggestion in answer.Suggestions)
                assign[suggestion.Key] = suggestion.Value;
        }
        return assign;
    }

    // ── Schedule ─────────────────────────────────────────────────────────

    /// <summary>Enabled saved imports whose time has come.</summary>
    public async Task<IReadOnlyList<long>> DueAsync()
    {
        var due = new List<long>();
        var now = DateTimeOffset.UtcNow;
        foreach (var source in (await LoadAsync()).Where(s => s.Enabled))
        {
            if (await NextRunAsync(source) is { } next && next <= now)
                due.Add(source.Id);
        }
        return due;
    }

    /// <summary>
    /// When the next scheduled run is due: daily at the time (today if it has
    /// not run since today's time, else tomorrow); every n hours after the last
    /// scheduled run (when it was saved if there was none: it starts right away). Null for
    /// manual imports.
    /// </summary>
    private async Task<DateTimeOffset?> NextRunAsync(Source source)
    {
        var schedule = source.Config.Schedule;
        if (schedule?.Kind is not ("daily" or "hours"))
            return null;
        string? lastText;
        using (var conn = await _systemPool.GetConnectionAsync())
            lastText = await conn.ExecuteScalarAsync<string?>(
                $"SELECT {Ts("started_at")} FROM {Runs} WHERE source_id = @Id AND run_trigger = 'schedule' ORDER BY started_at DESC LIMIT 1",
                new { source.Id });
        DateTimeOffset? last = lastText is null ? null : DateTimeOffset.Parse(lastText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        var now = DateTimeOffset.UtcNow;

        var created = DateTimeOffset.Parse(source.CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        if (schedule.Kind == "hours")
            return last is null ? created : last.Value.AddHours(schedule.Every ?? 24);

        var zone = schedule.TimeZone is { } own && TimeZoneInfo.TryFindSystemTimeZoneById(own, out var found)
            ? found
            : (_workspace is null ? WorkspaceCalendar.Default : await _workspace.CalendarAsync()).Zone;
        var at = TimeOnly.ParseExact(schedule.At ?? "06:00", "HH:mm", CultureInfo.InvariantCulture);
        var localNow = TimeZoneInfo.ConvertTime(now, zone);
        DateTimeOffset At(DateTime day)
        {
            var local = day.Date + at.ToTimeSpan();
            return new DateTimeOffset(local, zone.GetUtcOffset(local));
        }
        var today = At(localNow.DateTime);
        // Today's time has passed without a scheduled run since: due now. Not for a time
        // that passed before the import was saved: that one starts with the next time.
        var since = last ?? created;
        if (today <= now && since < today)
            return today;
        return today > now ? today : At(localNow.DateTime.AddDays(1));
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private string TableName(string table) =>
        _schemaCache.GetTable(table)?.Name
        ?? throw new ApiNotFoundException($"Table '{table}' does not exist.", new Dictionary<string, object?> { ["table"] = table });

    private async Task EnsureNameFreeAsync(System.Data.IDbConnection conn, string tableName, string name, long? exceptId)
    {
        var taken = await conn.ExecuteScalarAsync<long>(
            $"SELECT COUNT(*) FROM {Sources} WHERE workspace_id = @Ws AND table_name = @TableName AND LOWER(name) = LOWER(@Name) AND (@ExceptId IS NULL OR id <> @ExceptId)",
            new { TableName = tableName, Name = name, ExceptId = exceptId, Ws });
        if (taken > 0)
            throw new ApiConflictException($"A saved import named '{name}' already exists for this table.", new Dictionary<string, object?> { ["field"] = "name" });
    }

    private static string RequireName(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            throw Invalid("name", "Give the saved import a name.");
        if (trimmed.Length > 255)
            throw Invalid("name", "The name is too long.");
        return trimmed;
    }

    private static string UtcNowIso() => DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

    private static ApiValidationException Invalid(string field, string message) =>
        new(message, new Dictionary<string, object?> { ["field"] = field });
}
