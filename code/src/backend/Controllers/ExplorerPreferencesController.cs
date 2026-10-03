using System.Globalization;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Repere.Api.Authorization;
using Repere.Api.Db;
using Repere.Api.Exceptions;
using Repere.Api.Models;

namespace Repere.Api.Controllers;

[ApiController]
[Route(ApiRoutes.ExplorerPreferences)]
public sealed class ExplorerPreferencesController : ControllerBase
{
    private readonly ISystemPool _systemPool;

    public ExplorerPreferencesController(ISystemPool systemPool)
    {
        _systemPool = systemPool;
    }

    [HttpGet("{tableId}")]
    public async Task<IActionResult> Get(string tableId)
    {
        var userId = RequireUserId();

        using var conn = await _systemPool.GetConnectionAsync();
        var existing = await conn.QuerySingleOrDefaultAsync<UserTableExplorerPreferences>(
            $"""
            SELECT
                user_id         AS UserId,
                table_name      AS TableName,
                default_view_id AS DefaultViewId,
                auto_save       AS AutoSave,
                {CreatedAtText} AS CreatedAt,
                {UpdatedAtText} AS UpdatedAt
            FROM {PreferencesTable}
            WHERE user_id = @UserId
              AND table_name = @TableName
            """,
            new { UserId = userId, TableName = tableId });

        return Ok(new
        {
            default_view_id = existing?.DefaultViewId,
            auto_save = existing?.AutoSave ?? false
        });
    }

    /// <summary>
    /// Update preferences for a given table (PATCH-like). Properties omitted in the
    /// body are left unchanged; default_view_id supports explicit null to clear.
    /// </summary>
    [HttpPut("{tableId}")]
    public async Task<IActionResult> Upsert(string tableId, [FromBody] UpdateExplorerTablePreferencesRequest request)
    {
        var userId = RequireUserId();

        using var conn = await _systemPool.GetConnectionAsync();

        var existing = await conn.QuerySingleOrDefaultAsync<UserTableExplorerPreferences>(
            $"""
            SELECT
                user_id         AS UserId,
                table_name      AS TableName,
                default_view_id AS DefaultViewId,
                auto_save       AS AutoSave,
                {CreatedAtText} AS CreatedAt,
                {UpdatedAtText} AS UpdatedAt
            FROM {PreferencesTable}
            WHERE user_id = @UserId
              AND table_name = @TableName
            """,
            new { UserId = userId, TableName = tableId });

        var nextDefaultViewId = existing?.DefaultViewId;
        if (request.DefaultViewId.HasValue)
        {
            nextDefaultViewId = request.DefaultViewId.Value;
        }

        var nextAutoSave = request.AutoSave ?? existing?.AutoSave ?? false;

        // Validate default view id (when set) refers to an owned view for this table.
        if (nextDefaultViewId is not null)
        {
            var count = await conn.ExecuteScalarAsync<int>(
                $"""
                SELECT COUNT(1)
                FROM {ViewsTable}
                WHERE id = @Id
                  AND table_name = @TableName
                  AND owner_user_id = @UserId
                """,
                new { Id = nextDefaultViewId.Value, TableName = tableId, UserId = userId });

            if (count == 0)
            {
                throw new ApiValidationException(
                    "default_view_id must refer to a view you own for this table (or null).",
                    new Dictionary<string, object?> { ["field"] = "default_view_id" });
            }
        }

        var now = UtcNowIso();

        // Update first; insert if missing (portable across providers).
        var updated = await conn.ExecuteAsync(
            $"""
            UPDATE {PreferencesTable}
            SET default_view_id = @DefaultViewId,
                auto_save = @AutoSave,
                updated_at = {NowParam}
            WHERE user_id = @UserId
              AND table_name = @TableName
            """,
            new
            {
                DefaultViewId = nextDefaultViewId,
                AutoSave = nextAutoSave,
                Now = now,
                UserId = userId,
                TableName = tableId
            });

        if (updated == 0)
        {
            await conn.ExecuteAsync(
                $"""
                INSERT INTO {PreferencesTable}
                    (user_id, table_name, default_view_id, auto_save, created_at, updated_at)
                VALUES
                    (@UserId, @TableName, @DefaultViewId, @AutoSave, {NowParam}, {NowParam})
                """,
                new
                {
                    UserId = userId,
                    TableName = tableId,
                    DefaultViewId = nextDefaultViewId,
                    AutoSave = nextAutoSave,
                    Now = now
                });
        }

        return Ok(new
        {
            default_view_id = nextDefaultViewId,
            auto_save = nextAutoSave
        });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string PreferencesTable =>
        _systemPool.Provider == DatabaseProvider.Postgres
            ? "_system.user_table_explorer_preferences"
            : "user_table_explorer_preferences";

    // Postgres stores the timestamps as TIMESTAMPTZ; SQLite as TEXT.
    private string NowParam => SystemSqlDialect.TimestampParam(_systemPool.Provider, "@Now");
    private string CreatedAtText => SystemSqlDialect.TimestampText(_systemPool.Provider, "created_at");
    private string UpdatedAtText => SystemSqlDialect.TimestampText(_systemPool.Provider, "updated_at");

    private string ViewsTable =>
        _systemPool.Provider == DatabaseProvider.Postgres
            ? "_system.system_views"
            : "system_views";

    private int RequireUserId()
    {
        var userId = HttpContext.User.GetUserId();
        if (userId is null)
            throw new ApiUnauthorizedException("Authentication is required.");
        return userId.Value;
    }

    private static string UtcNowIso() =>
        DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
}
