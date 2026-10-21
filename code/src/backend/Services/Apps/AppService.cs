using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bento.Api.Db;
using Bento.Api.Db.Schema;
using Bento.Api.Exceptions;
using Bento.Api.Models;
using Dapper;

namespace Bento.Api.Services.Apps;

/// <summary>
/// Apps (Step 5.4): doors from other programs to Bento's tables. The data stays
/// in the tables — a calendar app shows a table's rows as events to calendar
/// programs, which read and write them over CalDAV,
/// while the app, the API and MCP keep working on the same rows. Each device
/// signs in with its own app password, revocable on its own.
/// </summary>
public sealed partial class AppService
{
    public static readonly string[] Kinds = ["calendar"];
    public const string PasswordPrefix = "bento_app_";
    public const int MaxCalendars = 20;
    private static readonly string[] FilterOps = ["eq", "neq", "gt", "gte", "lt", "lte", "like", "in", "null", "notnull"];
    private static readonly TimeSpan LastUsedResolution = TimeSpan.FromMinutes(1);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ISystemPool _systemPool;
    private readonly SchemaCache _schemaCache;

    public AppService(ISystemPool systemPool, SchemaCache schemaCache)
    {
        _systemPool = systemPool;
        _schemaCache = schemaCache;
    }

    private bool Pg => _systemPool.Provider == DatabaseProvider.Postgres;
    private string Apps => Pg ? "_system.apps" : "apps";
    private string Passwords => Pg ? "_system.app_passwords" : "app_passwords";
    private string NowParam => SystemSqlDialect.TimestampParam(_systemPool.Provider, "@Now");
    private string Ts(string column) => SystemSqlDialect.TimestampText(_systemPool.Provider, column);

    // ── Apps ─────────────────────────────────────────────────────────────

    private sealed class AppRow
    {
        public long Id { get; init; }
        public string Name { get; init; } = "";
        public string Kind { get; init; } = "";
        public string Config { get; init; } = "{}";
        public bool Enabled { get; init; }
        public long? CreatedBy { get; init; }
        public string CreatedAt { get; init; } = "";
        public string UpdatedAt { get; init; } = "";
    }

    /// <summary>An app as the doors use it: its calendars and the user it acts for.</summary>
    public sealed record App(long Id, string Name, string Kind, AppConfig Config, bool Enabled, long? CreatedBy, string CreatedAt, string UpdatedAt)
    {
        public IReadOnlyList<AppCalendar> Calendars => Config.Calendars ?? [];
    }

    private async Task<List<App>> LoadAsync(long? id = null)
    {
        using var conn = await _systemPool.GetConnectionAsync();
        var rows = await conn.QueryAsync<AppRow>(
            $"""
            SELECT id, name, kind, CAST(config AS TEXT) AS Config, enabled, created_by AS CreatedBy,
                   {Ts("created_at")} AS CreatedAt, {Ts("updated_at")} AS UpdatedAt
            FROM {Apps}{(id is null ? "" : " WHERE id = @Id")} ORDER BY name
            """, new { Id = id });
        return rows.Select(r => new App(r.Id, r.Name, r.Kind,
            JsonSerializer.Deserialize<AppConfig>(r.Config, Json) ?? new AppConfig(), r.Enabled, r.CreatedBy, r.CreatedAt, r.UpdatedAt)).ToList();
    }

    public async Task<App> RequireAsync(long id) =>
        (await LoadAsync(id)).SingleOrDefault() ?? throw new ApiNotFoundException($"App {id} not found.");

    public async Task<IReadOnlyList<App>> ListAppsAsync() => await LoadAsync();

    public async Task<IReadOnlyList<AppResponse>> ListAsync()
    {
        var apps = await LoadAsync();
        var passwords = await PasswordsAsync(null);
        return apps.Select(a => ToResponse(a, passwords)).ToList();
    }

    public async Task<AppResponse> GetAsync(long id) => ToResponse(await RequireAsync(id), await PasswordsAsync(id));

    public async Task<AppResponse> CreateAsync(AppRequest request, long? userId)
    {
        var name = RequireName(request.Name);
        var kind = request.Kind?.Trim().ToLowerInvariant() ?? "calendar";
        if (!Kinds.Contains(kind))
            throw Invalid("kind", "An app is a calendar.");
        var config = Validate(request.Config ?? new AppConfig(), null);
        using var conn = await _systemPool.GetConnectionAsync();
        var id = await conn.ExecuteScalarAsync<long>(
            $"""
            INSERT INTO {Apps} (name, kind, config, enabled, created_by, created_at, updated_at)
            VALUES (@Name, @Kind, {SystemSqlDialect.JsonParam(_systemPool.Provider, "@Config")}, @Enabled, @CreatedBy, {NowParam}, {NowParam})
            RETURNING id
            """,
            new { Name = name, Kind = kind, Config = JsonSerializer.Serialize(config, Json), Enabled = request.Enabled ?? true, CreatedBy = userId, Now = UtcNowIso() });
        return await GetAsync(id);
    }

    public async Task<AppResponse> UpdateAsync(long id, AppRequest request)
    {
        var existing = await RequireAsync(id);
        if (request.Kind is not null && !string.Equals(request.Kind.Trim(), existing.Kind, StringComparison.OrdinalIgnoreCase))
            throw Invalid("kind", "An app keeps its kind.");
        var name = request.Name is null ? existing.Name : RequireName(request.Name);
        var config = request.Config is null ? existing.Config : Validate(request.Config, existing.Config);
        using var conn = await _systemPool.GetConnectionAsync();
        await conn.ExecuteAsync(
            $"""
            UPDATE {Apps} SET name = @Name, config = {SystemSqlDialect.JsonParam(_systemPool.Provider, "@Config")}, enabled = @Enabled, updated_at = {NowParam}
            WHERE id = @Id
            """,
            new { Id = id, Name = name, Config = JsonSerializer.Serialize(config, Json), Enabled = request.Enabled ?? existing.Enabled, Now = UtcNowIso() });
        return await GetAsync(id);
    }

    public async Task DeleteAsync(long id)
    {
        await RequireAsync(id);
        using var conn = await _systemPool.GetConnectionAsync();
        await conn.ExecuteAsync($"DELETE FROM {Passwords} WHERE app_id = @Id", new { Id = id });
        await conn.ExecuteAsync($"DELETE FROM {Apps} WHERE id = @Id", new { Id = id });
    }

    // ── Validation ───────────────────────────────────────────────────────

    /// <summary>A column's value type; a computed column counts as its result.</summary>
    public static ColumnType ValueType(ColumnDef column) =>
        column.Type == ColumnType.Computed && Enum.TryParse<ColumnType>(column.ResultType, ignoreCase: true, out var result) ? result : column.Type;

    private AppConfig Validate(AppConfig config, AppConfig? existing)
    {
        var calendars = config.Calendars ?? [];
        if (calendars.Count == 0)
            throw Invalid("config.calendars", "Add a calendar: the table whose rows are its events.");
        if (calendars.Count > MaxCalendars)
            throw Invalid("config.calendars", $"An app has at most {MaxCalendars} calendars.");
        var slugs = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<AppCalendar>();
        for (var i = 0; i < calendars.Count; i++)
        {
            var field = $"config.calendars[{i}]";
            var calendar = calendars[i];
            var name = calendar.Name?.Trim();
            if (string.IsNullOrEmpty(name))
                throw Invalid($"{field}.name", "Give the calendar a name, e.g. Appointments.");
            if (name.Length > 100)
                throw Invalid($"{field}.name", "The name is too long.");
            // A calendar keeps its address when renamed: its slug stays.
            var slug = !string.IsNullOrWhiteSpace(calendar.Slug) && existing?.Calendars?.Any(c => c.Slug == calendar.Slug) == true
                ? calendar.Slug!
                : Slug(name);
            var unique = slug;
            for (var n = 2; !slugs.Add(unique); n++)
                unique = $"{slug}-{n}";

            var table = _schemaCache.GetTable(calendar.Table?.Trim() ?? "")
                ?? throw Invalid($"{field}.table", "Choose the table whose rows are the events.");
            ColumnDef? Column(string? name, string what, Func<ColumnType, bool>? fits = null, string? needs = null, bool required = false)
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    if (required)
                        throw Invalid($"{field}.{what}", $"Choose the column for the {what}.");
                    return null;
                }
                var column = table.Columns.FirstOrDefault(c => string.Equals(c.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
                    ?? throw Invalid($"{field}.{what}", $"{table.DisplayName ?? table.Name} has no column {name}.");
                if (fits is not null && !fits(ValueType(column)))
                    throw Invalid($"{field}.{what}", $"{column.DisplayName ?? column.Name} is not {needs}.");
                return column;
            }
            static bool Temporal(ColumnType t) => t is ColumnType.Date or ColumnType.DateTime;
            var title = Column(calendar.Title, "title", required: true)!;
            var start = Column(calendar.Start, "start", Temporal, "a date or date-time column", required: true)!;
            var end = Column(calendar.End, "end", Temporal, "a date or date-time column");
            var allDay = Column(calendar.AllDay, "allDay", t => t == ColumnType.Boolean, "a yes/no column");
            var description = Column(calendar.Description, "description");
            var location = Column(calendar.Location, "location");
            var recurrence = Column(calendar.Recurrence, "recurrence", t => t == ColumnType.Text, "a text column");
            if (calendar.Duration is < 1 or > 60 * 24 * 31)
                throw Invalid($"{field}.duration", "The duration is 1 minute to 31 days, in minutes.");
            var color = calendar.Color?.Trim();
            if (!string.IsNullOrEmpty(color) && !ColorCode().IsMatch(color))
                throw Invalid($"{field}.color", "The colour is like #3B82F6.");
            var filters = new List<AppFilter>();
            foreach (var (filter, j) in (calendar.Filters ?? []).Select((f, j) => (f, j)))
            {
                var column = Column(filter.Column, $"filters[{j}].column", required: true)!;
                var op = filter.Op?.Trim().ToLowerInvariant() ?? "eq";
                if (!FilterOps.Contains(op))
                    throw Invalid($"{field}.filters[{j}].op", "Filter with eq, neq, gt, gte, lt, lte, like, in, null or notnull.");
                filters.Add(new AppFilter(column.Name, op, filter.Value));
            }
            result.Add(new AppCalendar
            {
                Name = name,
                Slug = unique,
                Table = table.Name,
                Title = title.Name,
                Start = start.Name,
                End = end?.Name,
                AllDay = allDay?.Name,
                Description = description?.Name,
                Location = location?.Name,
                Recurrence = recurrence?.Name,
                Duration = calendar.Duration,
                Color = string.IsNullOrEmpty(color) ? null : color.ToUpperInvariant(),
                Filters = filters.Count > 0 ? filters : null
            });
        }
        return new AppConfig { Calendars = result };
    }

    /// <summary>"Termine & Fristen" → "termine-fristen".</summary>
    public static string Slug(string name)
    {
        var normalized = name.ToLowerInvariant()
            .Replace("ä", "ae").Replace("ö", "oe").Replace("ü", "ue").Replace("ß", "ss");
        var slug = NotSlug().Replace(normalized, "-").Trim('-');
        return slug.Length == 0 ? "calendar" : slug.Length > 60 ? slug[..60].TrimEnd('-') : slug;
    }

    // ── App passwords ────────────────────────────────────────────────────

    private sealed class PasswordRow
    {
        public long Id { get; init; }
        public long AppId { get; init; }
        public string Name { get; init; } = "";
        public string Prefix { get; init; } = "";
        public string CreatedAt { get; init; } = "";
        public string? LastUsedAt { get; init; }
    }

    private async Task<List<PasswordRow>> PasswordsAsync(long? appId)
    {
        using var conn = await _systemPool.GetConnectionAsync();
        return (await conn.QueryAsync<PasswordRow>(
            $"""
            SELECT id, app_id AS AppId, name, prefix, {Ts("created_at")} AS CreatedAt, {Ts("last_used_at")} AS LastUsedAt
            FROM {Passwords} WHERE revoked_at IS NULL{(appId is null ? "" : " AND app_id = @AppId")} ORDER BY id
            """, new { AppId = appId })).ToList();
    }

    /// <summary>A new password for one device; the secret is returned once and kept only as a hash.</summary>
    public async Task<(AppPasswordResponse Password, string Secret)> CreatePasswordAsync(long appId, AppPasswordRequest request)
    {
        await RequireAsync(appId);
        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name))
            throw Invalid("name", "Name the device, e.g. iPhone.");
        if (name.Length > 100)
            throw Invalid("name", "The name is too long.");
        // 24 random bytes: guessing is hopeless, so a plain hash is enough (as for API keys).
        var secret = PasswordPrefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var prefix = secret[..(PasswordPrefix.Length + 4)];
        using var conn = await _systemPool.GetConnectionAsync();
        var id = await conn.ExecuteScalarAsync<long>(
            $"""
            INSERT INTO {Passwords} (app_id, name, secret_hash, prefix, created_at)
            VALUES (@AppId, @Name, @Hash, @Prefix, {NowParam})
            RETURNING id
            """,
            new { AppId = appId, Name = name, Hash = Hash(secret), Prefix = prefix, Now = UtcNowIso() });
        var row = (await PasswordsAsync(appId)).Single(p => p.Id == id);
        return (ToResponse(row), secret);
    }

    public async Task RevokePasswordAsync(long appId, long passwordId)
    {
        using var conn = await _systemPool.GetConnectionAsync();
        var changed = await conn.ExecuteAsync(
            $"UPDATE {Passwords} SET revoked_at = {NowParam} WHERE id = @Id AND app_id = @AppId AND revoked_at IS NULL",
            new { Id = passwordId, AppId = appId, Now = UtcNowIso() });
        if (changed == 0)
            throw new ApiNotFoundException($"App password {passwordId} not found.");
    }

    /// <summary>The app a password belongs to, if it is valid, not revoked and the app is on; null otherwise.</summary>
    public async Task<App?> AuthenticateAsync(string? secret)
    {
        if (string.IsNullOrEmpty(secret) || !secret.StartsWith(PasswordPrefix, StringComparison.Ordinal) || secret.Length > 200)
            return null;
        using var conn = await _systemPool.GetConnectionAsync();
        var row = await conn.QuerySingleOrDefaultAsync<(long Id, long AppId, string? LastUsedAt)?>(
            $"SELECT id, app_id, {Ts("last_used_at")} FROM {Passwords} WHERE secret_hash = @Hash AND revoked_at IS NULL",
            new { Hash = Hash(secret) });
        if (row is not { } found)
            return null;
        var app = (await LoadAsync(found.AppId)).SingleOrDefault();
        if (app is null || !app.Enabled)
            return null;
        var now = DateTime.UtcNow;
        if (found.LastUsedAt is null || now - DateTime.Parse(found.LastUsedAt, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal) >= LastUsedResolution)
            await conn.ExecuteAsync($"UPDATE {Passwords} SET last_used_at = {NowParam} WHERE id = @Id", new { found.Id, Now = UtcNowIso() });
        return app;
    }

    /// <summary>The addresses a device uses for each calendar of the app.</summary>
    public static IReadOnlyList<AppLink> Links(App app, string secret, string baseUrl) =>
        [new AppLink(app.Name, "caldav", $"{baseUrl}/dav/")];

    private static string Hash(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();

    // ── For Claude (MCP) ─────────────────────────────────────────────────

    /// <summary>
    /// What MCP clients are told about the apps: which tables are calendars and how
    /// their columns are read, so a request like "add a dentist appointment on
    /// Friday at 10" lands in the right table and shows up on the phone.
    /// </summary>
    public async Task<string?> McpContextAsync()
    {
        var apps = (await LoadAsync()).Where(a => a.Enabled && a.Calendars.Count > 0).ToList();
        if (apps.Count == 0)
            return null;
        var text = new StringBuilder("Calendars (Apps): these tables are calendars on the user's devices — a row added, changed or deleted there shows up in their calendar apps.");
        foreach (var calendar in apps.SelectMany(a => a.Calendars))
        {
            text.Append($"\n- “{calendar.Name}”: table `{calendar.Table}`, title `{calendar.Title}`, start `{calendar.Start}`");
            if (calendar.End is { } end)
                text.Append($", end `{end}`");
            if (calendar.AllDay is { } allDay)
                text.Append($", all-day `{allDay}`");
            if (calendar.Location is { } location)
                text.Append($", location `{location}`");
            if (calendar.Description is { } description)
                text.Append($", notes `{description}`");
            if (calendar.Recurrence is { } recurrence)
                text.Append($", repeats `{recurrence}` (an iCal RRULE like FREQ=WEEKLY;BYDAY=TU)");
            if (calendar.Filters is { Count: > 0 } filters)
                text.Append($"; only rows with {string.Join(" and ", filters.Select(f => $"{f.Column} {f.Op} {f.Value}"))}");
        }
        text.Append("\nDate-times without an offset are in the workspace's time zone.");
        return text.ToString();
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private AppResponse ToResponse(App app, IEnumerable<PasswordRow> passwords) =>
        new(app.Id, app.Name, app.Kind, app.Config, app.Enabled, passwords.Where(p => p.AppId == app.Id).Select(ToResponse).ToList(), app.CreatedAt, app.UpdatedAt);

    private static AppPasswordResponse ToResponse(PasswordRow row) => new(row.Id, row.Name, row.Prefix, row.CreatedAt, row.LastUsedAt);

    private static string RequireName(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            throw Invalid("name", "Give the app a name, e.g. My calendar.");
        if (trimmed.Length > 255)
            throw Invalid("name", "The name is too long.");
        return trimmed;
    }

    private static string UtcNowIso() => DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

    private static ApiValidationException Invalid(string field, string message) =>
        new(message, new Dictionary<string, object?> { ["field"] = field });

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex ColorCode();

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NotSlug();
}
