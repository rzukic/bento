using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Bento.Api.Authorization;
using Bento.Api.Db;
using Bento.Api.Exceptions;
using Bento.Api.Services;

namespace Bento.Api.Controllers;

/// <summary>
/// Dashboards (Step 3.3): Dashboard JSON (Part 9 R9.8) in
/// <c>_system.dashboards</c>, private or shared like saved views. Everyone
/// who can read data can create their own; only the owner changes or
/// deletes one. Widget data comes from the query API, not from here.
/// </summary>
[ApiController]
[Route(ApiRoutes.Dashboards)]
[RequireRole("viewer", "api")]
public sealed class DashboardsController : ControllerBase
{
    public static readonly IReadOnlySet<string> WidgetTypes =
        new HashSet<string>(StringComparer.Ordinal) { "kpi", "line", "area", "bar", "donut", "sankey", "table", "text", "target" };

    private const int GridColumns = 12;
    private const int MaxWidgets = 60;
    /// <summary>Older states kept per dashboard (Step 4.9).</summary>
    private const int MaxVersions = 100;

    private readonly ISystemPool _systemPool;

    public DashboardsController(ISystemPool systemPool)
    {
        _systemPool = systemPool;
    }

    public sealed record DashboardRequest
    {
        public string? Name { get; init; }
        public bool? IsShared { get; init; }
        public JsonElement? Dashboard { get; init; }
        /// <summary>Optional, for the history: e.g. what the AI was asked when it built a widget.</summary>
        public string? Note { get; init; }
    }

    /// <summary>
    /// The state a dashboard had before a change, and who made the change — the
    /// user in the app, or an app or key (<c>Via</c>) such as Claude over MCP.
    /// </summary>
    public sealed record VersionResponse(
        long Id,
        long DashboardId,
        string Name,
        int Widgets,
        string Action,
        string? ChangedBy,
        string? Via,
        string? Note,
        string CreatedAt);

    public sealed record DashboardResponse(
        long Id,
        string Name,
        bool IsShared,
        bool IsOwner,
        JsonElement Dashboard,
        string CreatedAt,
        string UpdatedAt);

    [HttpGet]
    public async Task<IActionResult> List()
    {
        var userId = UserId();
        using var conn = await _systemPool.GetConnectionAsync();
        var rows = await conn.QueryAsync<DashboardRow>(
            $"""
            SELECT {Columns}
            FROM {Table}
            WHERE owner_user_id = @UserId OR is_shared {SharedTrue}
            ORDER BY LOWER(name)
            """,
            new { UserId = userId });
        return Ok(new { data = rows.Select(r => ToResponse(r, userId)) });
    }

    [HttpGet("{id:long}")]
    public async Task<IActionResult> Get(long id)
    {
        var userId = UserId();
        using var conn = await _systemPool.GetConnectionAsync();
        return Ok(new { data = ToResponse(await GetVisibleAsync(conn, id, userId), userId) });
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] DashboardRequest request)
    {
        var userId = UserId();
        var name = RequireName(request.Name);
        var json = SerializeDashboard(request.Dashboard ?? JsonDocument.Parse("""{ "widgets": [] }""").RootElement);
        var now = UtcNowIso();

        using var conn = await _systemPool.GetConnectionAsync();
        var id = await conn.ExecuteScalarAsync<long>(
            $"""
            INSERT INTO {Table} (name, dashboard_json, owner_user_id, is_shared, created_at, updated_at)
            VALUES (@Name, {JsonParam}, @OwnerId, @IsShared, {NowParam}, {NowParam})
            RETURNING id
            """,
            new { Name = name, Json = json, OwnerId = userId, IsShared = request.IsShared ?? false, Now = now });

        return StatusCode(StatusCodes.Status201Created, new { data = ToResponse(await GetVisibleAsync(conn, id, userId), userId) });
    }

    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] DashboardRequest request)
    {
        var userId = UserId();
        using var conn = await _systemPool.GetConnectionAsync();
        var existing = await GetVisibleAsync(conn, id, userId);
        if (existing.OwnerUserId != userId)
            throw new ApiForbiddenException("Only the owner can change this dashboard.");

        await RecordVersionAsync(conn, existing, "update", request.Note);
        await conn.ExecuteAsync(
            $"""
            UPDATE {Table}
            SET name = @Name, dashboard_json = {JsonParam}, is_shared = @IsShared, updated_at = {NowParam}
            WHERE id = @Id
            """,
            new
            {
                Id = id,
                Name = request.Name is null ? existing.Name : RequireName(request.Name),
                Json = request.Dashboard is null ? existing.DashboardJson : SerializeDashboard(request.Dashboard.Value),
                IsShared = request.IsShared ?? existing.IsShared,
                Now = UtcNowIso()
            });

        return Ok(new { data = ToResponse(await GetVisibleAsync(conn, id, userId), userId) });
    }

    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Delete(long id)
    {
        var userId = UserId();
        using var conn = await _systemPool.GetConnectionAsync();
        var existing = await GetVisibleAsync(conn, id, userId);
        if (existing.OwnerUserId != userId)
            throw new ApiForbiddenException("Only the owner can delete this dashboard.");
        await RecordVersionAsync(conn, existing, "delete", null);
        await conn.ExecuteAsync($"DELETE FROM {Table} WHERE id = @Id", new { Id = id });
        return NoContent();
    }

    // ── History (Step 4.9) ───────────────────────────────────────────────

    /// <summary>The dashboard's earlier states, newest first: who changed it, through which app, when.</summary>
    [HttpGet("{id:long}/versions")]
    public async Task<IActionResult> Versions(long id)
    {
        var userId = UserId();
        using var conn = await _systemPool.GetConnectionAsync();
        await GetVisibleAsync(conn, id, userId);
        return Ok(new { data = await ListVersionsAsync(conn, "v.dashboard_id = @Id", new { Id = id }, MaxVersions) });
    }

    /// <summary>One earlier state with its Dashboard JSON, to look at before restoring it.</summary>
    [HttpGet("{id:long}/versions/{versionId:long}")]
    public async Task<IActionResult> Version(long id, long versionId)
    {
        var userId = UserId();
        using var conn = await _systemPool.GetConnectionAsync();
        await GetVisibleAsync(conn, id, userId);
        var version = await GetVersionAsync(conn, versionId);
        if (version.DashboardId != id)
            throw new ApiNotFoundException($"Version {versionId} not found.");
        using var json = JsonDocument.Parse(DashboardUpgrade.Apply(version.DashboardJson));
        return Ok(new { data = new { version.Id, version.Name, dashboard = json.RootElement.Clone() } });
    }

    /// <summary>Back to an earlier state; the state before the restore is kept, so it can be undone too.</summary>
    [HttpPost("{id:long}/versions/{versionId:long}/restore")]
    public async Task<IActionResult> Restore(long id, long versionId)
    {
        var userId = UserId();
        using var conn = await _systemPool.GetConnectionAsync();
        var existing = await GetVisibleAsync(conn, id, userId);
        if (existing.OwnerUserId != userId)
            throw new ApiForbiddenException("Only the owner can restore this dashboard.");
        var version = await GetVersionAsync(conn, versionId);
        if (version.DashboardId != id)
            throw new ApiNotFoundException($"Version {versionId} not found.");

        await RecordVersionAsync(conn, existing, "restore", $"Restored the state of {version.CreatedAt}");
        await conn.ExecuteAsync(
            $"UPDATE {Table} SET name = @Name, dashboard_json = {JsonParam}, is_shared = @IsShared, updated_at = {NowParam} WHERE id = @Id",
            new { Id = id, version.Name, Json = DashboardUpgrade.Apply(version.DashboardJson), version.IsShared, Now = UtcNowIso() });
        return Ok(new { data = ToResponse(await GetVisibleAsync(conn, id, userId), userId) });
    }

    /// <summary>The user's deleted dashboards that can be brought back.</summary>
    [HttpGet("deleted")]
    public async Task<IActionResult> Deleted()
    {
        using var conn = await _systemPool.GetConnectionAsync();
        var versions = await ListVersionsAsync(conn,
            $"v.action = 'delete' AND v.owner_user_id = @UserId AND NOT EXISTS (SELECT 1 FROM {Table} d WHERE d.id = v.dashboard_id)",
            new { UserId = UserId() }, 50);
        return Ok(new { data = versions });
    }

    /// <summary>Brings a deleted dashboard back under its old id.</summary>
    [HttpPost("deleted/{versionId:long}/restore")]
    public async Task<IActionResult> RestoreDeleted(long versionId)
    {
        var userId = UserId();
        using var conn = await _systemPool.GetConnectionAsync();
        var version = await GetVersionAsync(conn, versionId);
        if (version.Action != "delete" || version.OwnerUserId != userId)
            throw new ApiNotFoundException($"Deleted dashboard {versionId} not found.");
        if (await conn.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {Table} WHERE id = @Id", new { Id = version.DashboardId }) > 0)
            throw new ApiConflictException("This dashboard exists again already.");

        await conn.ExecuteAsync(
            $"""
            INSERT INTO {Table} (id, name, dashboard_json, owner_user_id, is_shared, created_at, updated_at)
            VALUES (@Id, @Name, {JsonParam}, @OwnerId, @IsShared, {NowParam}, {NowParam})
            """,
            new { Id = version.DashboardId, version.Name, Json = DashboardUpgrade.Apply(version.DashboardJson), OwnerId = userId, version.IsShared, Now = UtcNowIso() });
        if (_systemPool.Provider == DatabaseProvider.Postgres)
            await conn.ExecuteAsync($"SELECT setval(pg_get_serial_sequence('{Table}', 'id'), GREATEST((SELECT MAX(id) FROM {Table}), 1))");
        return StatusCode(StatusCodes.Status201Created, new { data = ToResponse(await GetVisibleAsync(conn, version.DashboardId, userId), userId) });
    }

    /// <summary>
    /// Recent changes to the user's dashboards made by apps and keys (Claude,
    /// ChatGPT, scripts) — the AI page lists them with an undo.
    /// </summary>
    [HttpGet("changes")]
    public async Task<IActionResult> Changes([FromQuery] int limit = 30)
    {
        using var conn = await _systemPool.GetConnectionAsync();
        var versions = await ListVersionsAsync(conn, "v.via IS NOT NULL AND v.owner_user_id = @UserId", new { UserId = UserId() }, Math.Clamp(limit, 1, 100));
        return Ok(new { data = versions });
    }

    private sealed class VersionRow
    {
        public long Id { get; init; }
        public long DashboardId { get; init; }
        public string Name { get; init; } = "";
        public string DashboardJson { get; init; } = "{}";
        public bool IsShared { get; init; }
        public long? OwnerUserId { get; init; }
        public string Action { get; init; } = "";
        public string? ChangedBy { get; init; }
        public string? Via { get; init; }
        public string? Note { get; init; }
        public string CreatedAt { get; init; } = "";
    }

    private string VersionColumns =>
        "v.id, v.dashboard_id AS DashboardId, v.name, v.dashboard_json AS DashboardJson, v.is_shared AS IsShared, v.owner_user_id AS OwnerUserId, " +
        $"v.action, u.username AS ChangedBy, v.via, v.note, {SystemSqlDialect.TimestampText(_systemPool.Provider, "v.created_at")} AS CreatedAt";

    private async Task<IReadOnlyList<VersionResponse>> ListVersionsAsync(System.Data.IDbConnection conn, string where, object parameters, int limit)
    {
        var rows = await conn.QueryAsync<VersionRow>(
            $"SELECT {VersionColumns} FROM {VersionsTable} v LEFT JOIN {Users} u ON u.id = v.changed_by WHERE {where} ORDER BY v.created_at DESC, v.id DESC LIMIT {limit}",
            parameters);
        return rows.Select(r => new VersionResponse(r.Id, r.DashboardId, r.Name, CountWidgets(r.DashboardJson), r.Action, r.ChangedBy, r.Via, r.Note, r.CreatedAt)).ToList();
    }

    private async Task<VersionRow> GetVersionAsync(System.Data.IDbConnection conn, long versionId) =>
        await conn.QuerySingleOrDefaultAsync<VersionRow>(
            $"SELECT {VersionColumns} FROM {VersionsTable} v LEFT JOIN {Users} u ON u.id = v.changed_by WHERE v.id = @Id", new { Id = versionId })
        ?? throw new ApiNotFoundException($"Version {versionId} not found.");

    /// <summary>Keeps the state before a change, with who changed it and through which app.</summary>
    private async Task RecordVersionAsync(System.Data.IDbConnection conn, DashboardRow existing, string action, string? note)
    {
        var via = User.FindFirstValue("auth_method") switch
        {
            "oauth" => User.FindFirstValue("oauth_client") ?? "MCP client",
            "api_key" => "API key #" + User.FindFirstValue("api_key_id"),
            _ => null
        };
        await conn.ExecuteAsync(
            $"""
            INSERT INTO {VersionsTable} (dashboard_id, name, dashboard_json, is_shared, owner_user_id, action, changed_by, via, note, created_at)
            VALUES (@DashboardId, @Name, {JsonParam}, @IsShared, @OwnerId, @Action, @ChangedBy, @Via, @Note, {NowParam})
            """,
            new
            {
                DashboardId = existing.Id, existing.Name, Json = existing.DashboardJson, existing.IsShared, OwnerId = existing.OwnerUserId,
                Action = action, ChangedBy = UserId(), Via = via, Note = note is { Length: > 500 } ? note[..500] : note, Now = UtcNowIso()
            });
        await conn.ExecuteAsync(
            $"""
            DELETE FROM {VersionsTable} WHERE dashboard_id = @Id AND action <> 'delete' AND id NOT IN (
                SELECT id FROM {VersionsTable} WHERE dashboard_id = @Id ORDER BY created_at DESC, id DESC LIMIT {MaxVersions})
            """,
            new { Id = existing.Id });
    }

    private static int CountWidgets(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("widgets", out var widgets) && widgets.ValueKind == JsonValueKind.Array ? widgets.GetArrayLength() : 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }


    // ── Validation ───────────────────────────────────────────────────────

    /// <summary>
    /// The layout must hold together: widgets with unique ids, a known type,
    /// a position inside the 12-column grid and known display and data options
    /// (<see cref="WidgetOptions"/>). Widget queries are checked when they run
    /// (a renamed column shows on the widget, not here).
    /// </summary>
    private static string SerializeDashboard(JsonElement dashboard)
    {
        // Old widgets (budget, the money Sankey) from older clients or states become today's.
        dashboard = DashboardUpgrade.Apply(dashboard);
        if (dashboard.ValueKind != JsonValueKind.Object)
            throw Invalid("dashboard", "dashboard must be an object.");
        if (!dashboard.TryGetProperty("widgets", out var widgets) || widgets.ValueKind != JsonValueKind.Array)
            throw Invalid("dashboard.widgets", "dashboard.widgets must be a list.");
        if (widgets.GetArrayLength() > MaxWidgets)
            throw Invalid("dashboard.widgets", $"A dashboard holds at most {MaxWidgets} widgets.");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var widget in widgets.EnumerateArray())
        {
            var path = $"dashboard.widgets[{index++}]";
            if (widget.ValueKind != JsonValueKind.Object)
                throw Invalid(path, "Each widget must be an object.");
            var id = widget.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String ? idElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(id) || !ids.Add(id))
                throw Invalid($"{path}.id", "Each widget needs its own id.");
            var type = widget.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String ? typeElement.GetString() : null;
            if (type is null || !WidgetTypes.Contains(type))
                throw Invalid($"{path}.type", $"Unknown widget type '{type}'.");
            if (!widget.TryGetProperty("position", out var position) || position.ValueKind != JsonValueKind.Object)
                throw Invalid($"{path}.position", "Each widget needs a position.");
            int Read(string name) =>
                position.TryGetProperty(name, out var value) && value.TryGetInt32(out var number)
                    ? number
                    : throw Invalid($"{path}.position.{name}", $"position.{name} must be a whole number.");
            var (x, y, w, h) = (Read("x"), Read("y"), Read("w"), Read("h"));
            if (x < 0 || y < 0 || w < 1 || h < 1 || x + w > GridColumns || h > 24)
                throw Invalid($"{path}.position", $"The widget does not fit the {GridColumns}-column grid.");
            WidgetOptions.Check(widget, path);
        }

        WidgetOptions.CheckVariables(dashboard);
        return dashboard.GetRawText();
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private sealed class DashboardRow
    {
        public long Id { get; init; }
        public string Name { get; init; } = "";
        public string DashboardJson { get; init; } = "{}";
        public long? OwnerUserId { get; init; }
        public bool IsShared { get; init; }
        public string CreatedAt { get; init; } = "";
        public string UpdatedAt { get; init; } = "";
    }

    private string Columns =>
        "id, name, dashboard_json AS DashboardJson, owner_user_id AS OwnerUserId, is_shared AS IsShared, " +
        $"{SystemSqlDialect.TimestampText(_systemPool.Provider, "created_at")} AS CreatedAt, " +
        $"{SystemSqlDialect.TimestampText(_systemPool.Provider, "updated_at")} AS UpdatedAt";

    private async Task<DashboardRow> GetVisibleAsync(System.Data.IDbConnection conn, long id, long? userId)
    {
        var row = await conn.QuerySingleOrDefaultAsync<DashboardRow>($"SELECT {Columns} FROM {Table} WHERE id = @Id", new { Id = id });
        if (row is null || (row.OwnerUserId != userId && !row.IsShared))
            throw new ApiNotFoundException($"Dashboard {id} not found.");
        return row;
    }

    private static DashboardResponse ToResponse(DashboardRow row, long? userId)
    {
        using var json = JsonDocument.Parse(string.IsNullOrWhiteSpace(row.DashboardJson) ? "{}" : row.DashboardJson);
        return new DashboardResponse(row.Id, row.Name, row.IsShared, row.OwnerUserId == userId, json.RootElement.Clone(), row.CreatedAt, row.UpdatedAt);
    }

    private static string RequireName(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            throw Invalid("name", "Give the dashboard a name.");
        if (trimmed.Length > 255)
            throw Invalid("name", "The name is too long.");
        return trimmed;
    }

    private long? UserId() => HttpContext.User.GetUserId();

    private string Table => _systemPool.Provider == DatabaseProvider.Postgres ? "_system.dashboards" : "dashboards";
    private string VersionsTable => _systemPool.Provider == DatabaseProvider.Postgres ? "_system.dashboard_versions" : "dashboard_versions";
    private string Users => _systemPool.Provider == DatabaseProvider.Postgres ? "_system.users" : "users";
    private string SharedTrue => _systemPool.Provider == DatabaseProvider.Postgres ? "" : "= 1";
    private string JsonParam => SystemSqlDialect.JsonParam(_systemPool.Provider, "@Json");
    private string NowParam => SystemSqlDialect.TimestampParam(_systemPool.Provider, "@Now");

    private static string UtcNowIso() => DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

    private static ApiValidationException Invalid(string field, string message) =>
        new(message, new Dictionary<string, object?> { ["field"] = field });
}
