using System.Globalization;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Bento.Api.Authorization;
using Bento.Api.Db;
using Bento.Api.Exceptions;
using Bento.Api.Models;
using Bento.Api.Services;

namespace Bento.Api.Controllers;

[ApiController]
[Route(ApiRoutes.Views)]
public sealed class ViewsController : ControllerBase
{
    private readonly ISystemPool _systemPool;
    private readonly SchemaCache _schemaCache;

    public ViewsController(ISystemPool systemPool, SchemaCache schemaCache)
    {
        _systemPool = systemPool;
        _schemaCache = schemaCache;
    }

    /// <summary>
    /// List all views visible to the current user for a given table:
    /// own views (any is_shared value) + shared views owned by others.
    /// Ordered: own views first (by created_at), then others' shared views.
    /// </summary>
    [HttpGet("{tableId}")]
    public async Task<IActionResult> List(string tableId)
    {
        var userId = RequireUserId();

        using var conn = await _systemPool.GetConnectionAsync();
        var views = await conn.QueryAsync<SystemView>(
            $"""
            SELECT
                id,
                name,
                table_name      AS TableName,
                owner_user_id   AS OwnerUserId,
                type,
                config,
                is_shared       AS IsShared,
                {CreatedAtText} AS CreatedAt,
                {UpdatedAtText} AS UpdatedAt
            FROM {ViewsTable}
            WHERE workspace_id = @Ws AND table_name = @TableName
              AND (owner_user_id = @UserId OR (is_shared AND owner_user_id <> @UserId))
            ORDER BY
                CASE WHEN owner_user_id = @UserId THEN 0 ELSE 1 END,
                created_at ASC
            """,
            new { TableName = tableId, UserId = userId, Ws });

        var pref = await conn.QuerySingleOrDefaultAsync<UserTableExplorerPreferences>(
            $"""
            SELECT
                user_id         AS UserId,
                table_name      AS TableName,
                default_view_id AS DefaultViewId,
                auto_save       AS AutoSave,
                {CreatedAtText} AS CreatedAt,
                {UpdatedAtText} AS UpdatedAt
            FROM {PreferencesTable}
            WHERE workspace_id = @Ws AND user_id = @UserId
              AND table_name = @TableName
            """,
            new { UserId = userId, TableName = tableId, Ws });

        // Ensure we never report a default_view_id that isn't currently visible
        // to the user (e.g. deleted view on SQLite where FK constraints may be off).
        var defaultViewId = pref?.DefaultViewId;
        if (defaultViewId is not null && !views.Any(v => v.Id == defaultViewId.Value))
        {
            defaultViewId = null;
        }

        return Ok(new
        {
            data = views.Select(MapToResponse),
            preferences = new
            {
                default_view_id = defaultViewId,
                auto_save = pref?.AutoSave ?? false
            }
        });
    }

    /// <summary>
    /// Create a new view owned by the current user.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateViewRequest request)
    {
        var userId = RequireUserId();

        // Validate type
        var type = ValidateType(request.Type);

        // Validate config is a JSON object and normalize to include config.type
        var configJson = NormalizeConfig(type, request.Config);

        // Validate table exists in the current schema (lenient — POST only)
        var tableNames = _schemaCache.GetTableNames();
        if (!tableNames.Any(t => string.Equals(t, request.TableName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ApiValidationException(
                $"Table '{request.TableName}' does not exist in the current schema.");
        }

        var now = UtcNowIso();

        using var conn = await _systemPool.GetConnectionAsync();
        var id = await conn.ExecuteScalarAsync<int>(
            $"""
            INSERT INTO {ViewsTable}
                (workspace_id, name, table_name, owner_user_id, type, config, is_shared, created_at, updated_at)
            VALUES
                (@Ws, @Name, @TableName, @OwnerId, @Type, {ConfigParam}, @IsShared, {NowParam}, {NowParam})
            RETURNING id
            """,
            new
            {
                Name      = request.Name!.Trim(),
                TableName = request.TableName,
                OwnerId   = userId,
                Type      = type,
                Config    = configJson,
                IsShared  = request.IsShared,
                Now       = now,
                Ws
            });

        var created = await GetByIdAsync(conn, id);
        return StatusCode(StatusCodes.Status201Created, MapToResponse(created!));
    }

    /// <summary>
    /// Update a view. Only the owner may update. table_name is immutable.
    /// </summary>
    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateViewRequest request)
    {
        var userId = RequireUserId();

        using var conn = await _systemPool.GetConnectionAsync();
        var existing = await GetByIdAsync(conn, id);

        if (existing is null)
            throw new ApiNotFoundException($"View {id} not found.");

        if (existing.OwnerUserId != userId)
            throw new ApiForbiddenException("Only the view owner can update this view.");

        var newName     = request.Name is not null ? request.Name.Trim() : existing.Name;
        var newType     = request.Type is not null ? ValidateType(request.Type) : existing.Type;
        var newConfig   = request.Config.HasValue
            ? NormalizeConfig(newType, request.Config)
            : NormalizeConfigString(newType, existing.Config);
        var newIsShared = request.IsShared ?? existing.IsShared;
        var now         = UtcNowIso();

        await conn.ExecuteAsync(
            $"""
            UPDATE {ViewsTable}
            SET name       = @Name,
                type       = @Type,
                config     = {ConfigParam},
                is_shared  = @IsShared,
                updated_at = {NowParam}
            WHERE id = @Id
            """,
            new { Name = newName, Type = newType, Config = newConfig, IsShared = newIsShared, Now = now, Id = id });

        var updated = await GetByIdAsync(conn, id);
        return Ok(MapToResponse(updated!));
    }

    /// <summary>
    /// Delete a view. Only the owner may delete. Returns 204 No Content.
    /// </summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var userId = RequireUserId();

        using var conn = await _systemPool.GetConnectionAsync();
        var existing = await GetByIdAsync(conn, id);

        if (existing is null)
            throw new ApiNotFoundException($"View {id} not found.");

        if (existing.OwnerUserId != userId)
            throw new ApiForbiddenException("Only the view owner can delete this view.");

        await conn.ExecuteAsync(
            $"DELETE FROM {ViewsTable} WHERE id = @Id",
            new { Id = id });

        return NoContent();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static long Ws => Services.Workspaces.WorkspaceContext.Id;

    private string ViewsTable =>
        _systemPool.Provider == DatabaseProvider.Postgres ? "_system.system_views" : "system_views";

    private string PreferencesTable =>
        _systemPool.Provider == DatabaseProvider.Postgres
            ? "_system.user_table_explorer_preferences"
            : "user_table_explorer_preferences";

    // Postgres stores config as JSONB and the timestamps as TIMESTAMPTZ; SQLite as TEXT.
    private string ConfigParam => SystemSqlDialect.JsonParam(_systemPool.Provider, "@Config");
    private string NowParam => SystemSqlDialect.TimestampParam(_systemPool.Provider, "@Now");
    private string CreatedAtText => SystemSqlDialect.TimestampText(_systemPool.Provider, "created_at");
    private string UpdatedAtText => SystemSqlDialect.TimestampText(_systemPool.Provider, "updated_at");

    private int RequireUserId()
    {
        var userId = HttpContext.User.GetUserId();
        if (userId is null)
            throw new ApiUnauthorizedException("Authentication is required.");
        return userId.Value;
    }

    private static string ValidateType(string? type)
    {
        if (type is not ("table" or "kanban"))
            throw new ApiValidationException(
                "type must be 'table' or 'kanban'.",
                new Dictionary<string, object?> { ["field"] = "type" });
        return type;
    }

    private static string NormalizeConfig(string type, JsonElement? config)
    {
        if (config is null || config.Value.ValueKind == JsonValueKind.Undefined)
            throw new ApiValidationException("config is required.");

        if (config.Value.ValueKind != JsonValueKind.Object)
            throw new ApiValidationException(
                "config must be a JSON object.",
                new Dictionary<string, object?> { ["field"] = "config" });

        return NormalizeConfigObject(type, config.Value);
    }

    private static string NormalizeConfigString(string type, string configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson))
        {
            return NormalizeConfigObject(type, JsonSerializer.Deserialize<JsonElement>("{}"));
        }

        try
        {
            var element = JsonSerializer.Deserialize<JsonElement>(configJson);
            if (element.ValueKind != JsonValueKind.Object)
            {
                return NormalizeConfigObject(type, JsonSerializer.Deserialize<JsonElement>("{}"));
            }
            return NormalizeConfigObject(type, element, originalJson: configJson);
        }
        catch
        {
            return NormalizeConfigObject(type, JsonSerializer.Deserialize<JsonElement>("{}"));
        }
    }

    private static string NormalizeConfigObject(string type, JsonElement configObject, string? originalJson = null)
    {
        string? configType = null;
        var hasType = false;

        foreach (var prop in configObject.EnumerateObject())
        {
            if (!prop.NameEquals("type")) continue;
            hasType = true;
            if (prop.Value.ValueKind != JsonValueKind.String)
            {
                throw new ApiValidationException(
                    "config.type must be a string.",
                    new Dictionary<string, object?> { ["field"] = "config.type" });
            }
            configType = prop.Value.GetString();
            break;
        }

        if (hasType && !string.Equals(configType, type, StringComparison.Ordinal))
        {
            throw new ApiValidationException(
                $"config.type must match type ('{type}').",
                new Dictionary<string, object?> { ["field"] = "config.type" });
        }

        // Fast path: config already contains the correct type and we don't need
        // to rewrite it. Keeps JSON stable when possible.
        if (hasType && originalJson is not null)
        {
            return originalJson;
        }

        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();

        // Always emit type first for readability and to guarantee presence.
        writer.WriteString("type", type);

        foreach (var prop in configObject.EnumerateObject())
        {
            if (prop.NameEquals("type")) continue;
            writer.WritePropertyName(prop.Name);
            prop.Value.WriteTo(writer);
        }

        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string UtcNowIso() =>
        DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

    private async Task<SystemView?> GetByIdAsync(System.Data.IDbConnection conn, int id)
    {
        return await conn.QuerySingleOrDefaultAsync<SystemView>(
            $"""
            SELECT
                id,
                name,
                table_name      AS TableName,
                owner_user_id   AS OwnerUserId,
                type,
                config,
                is_shared       AS IsShared,
                {CreatedAtText} AS CreatedAt,
                {UpdatedAtText} AS UpdatedAt
            FROM {ViewsTable}
            WHERE id = @Id AND workspace_id = @Ws
            """,
            new { Id = id, Ws });
    }

    private static object MapToResponse(SystemView v)
    {
        JsonElement configJson;
        try
        {
            configJson = JsonSerializer.Deserialize<JsonElement>(v.Config);
        }
        catch
        {
            configJson = JsonSerializer.Deserialize<JsonElement>("{}");
        }

        return new
        {
            id           = v.Id,
            name         = v.Name,
            table_name   = v.TableName,
            owner_user_id = v.OwnerUserId,
            type         = v.Type,
            config       = configJson,
            is_shared    = v.IsShared,
            created_at   = v.CreatedAt,
            updated_at   = v.UpdatedAt
        };
    }
}
