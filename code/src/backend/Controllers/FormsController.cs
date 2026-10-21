using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Bento.Api.Authorization;
using Bento.Api.Db;
using Bento.Api.Db.Schema;
using Bento.Api.Exceptions;
using Bento.Api.Services;

namespace Bento.Api.Controllers;

/// <summary>
/// Quick entry forms (Step 4.4, Part 10): Form JSON in <c>_system.forms</c>,
/// private or shared like dashboards. A form names a table and the columns
/// it asks for, in order, with sections, labels, help, hidden fields and
/// defaults. Submissions go through the data API and its validation; this
/// controller only keeps the definitions. Editors write forms (they can write
/// rows); everyone who reads data sees shared ones.
/// </summary>
[ApiController]
[Route(ApiRoutes.Forms)]
[RequireRole("viewer", "api")]
public sealed class FormsController : ControllerBase
{
    public static readonly IReadOnlySet<string> DefaultKinds = new HashSet<string>(StringComparer.Ordinal) { "today", "now", "last", "value" };
    private const int MaxFields = 60;

    private readonly ISystemPool _systemPool;
    private readonly SchemaCache _schemaCache;

    public FormsController(ISystemPool systemPool, SchemaCache schemaCache)
    {
        _systemPool = systemPool;
        _schemaCache = schemaCache;
    }

    public sealed record FormRequest
    {
        public string? Name { get; init; }
        public bool? IsShared { get; init; }
        public JsonElement? Form { get; init; }
    }

    public sealed record FormResponse(
        long Id,
        string Name,
        string Table,
        bool IsShared,
        bool IsOwner,
        JsonElement Form,
        string CreatedAt,
        string UpdatedAt);

    [HttpGet]
    public async Task<IActionResult> List()
    {
        var userId = UserId();
        using var conn = await _systemPool.GetConnectionAsync();
        var rows = await conn.QueryAsync<FormRow>(
            $"SELECT {Columns} FROM {Table} WHERE workspace_id = @Ws AND (owner_user_id = @UserId OR is_shared {SharedTrue}) ORDER BY LOWER(name)",
            new { UserId = userId, Ws });
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
    [RequireRole("editor", "api")]
    public async Task<IActionResult> Create([FromBody] FormRequest request)
    {
        var userId = UserId();
        var name = RequireName(request.Name);
        if (request.Form is null)
            throw Invalid("form", "form is required: the table and its fields.");
        var (table, json) = Validate(request.Form.Value);

        using var conn = await _systemPool.GetConnectionAsync();
        var id = await conn.ExecuteScalarAsync<long>(
            $"""
            INSERT INTO {Table} (workspace_id, name, table_name, form_json, owner_user_id, is_shared, created_at, updated_at)
            VALUES (@Ws, @Name, @TableName, {JsonParam}, @OwnerId, @IsShared, {NowParam}, {NowParam})
            RETURNING id
            """,
            new { Name = name, TableName = table, Json = json, OwnerId = userId, IsShared = request.IsShared ?? false, Now = UtcNowIso(), Ws });
        return StatusCode(StatusCodes.Status201Created, new { data = ToResponse(await GetVisibleAsync(conn, id, userId), userId) });
    }

    [HttpPut("{id:long}")]
    [RequireRole("editor", "api")]
    public async Task<IActionResult> Update(long id, [FromBody] FormRequest request)
    {
        var userId = UserId();
        using var conn = await _systemPool.GetConnectionAsync();
        var existing = await GetVisibleAsync(conn, id, userId);
        if (existing.OwnerUserId != userId)
            throw new ApiForbiddenException("Only the owner can change this form.");
        var (table, json) = request.Form is { } form ? Validate(form) : (existing.TableName, existing.FormJson);

        await conn.ExecuteAsync(
            $"""
            UPDATE {Table}
            SET name = @Name, table_name = @TableName, form_json = {JsonParam}, is_shared = @IsShared, updated_at = {NowParam}
            WHERE id = @Id
            """,
            new
            {
                Id = id,
                Name = request.Name is null ? existing.Name : RequireName(request.Name),
                TableName = table,
                Json = json,
                IsShared = request.IsShared ?? existing.IsShared,
                Now = UtcNowIso()
            });
        return Ok(new { data = ToResponse(await GetVisibleAsync(conn, id, userId), userId) });
    }

    [HttpDelete("{id:long}")]
    [RequireRole("editor", "api")]
    public async Task<IActionResult> Delete(long id)
    {
        var userId = UserId();
        using var conn = await _systemPool.GetConnectionAsync();
        var existing = await GetVisibleAsync(conn, id, userId);
        if (existing.OwnerUserId != userId)
            throw new ApiForbiddenException("Only the owner can delete this form.");
        await conn.ExecuteAsync($"DELETE FROM {Table} WHERE id = @Id", new { Id = id });
        return NoContent();
    }

    // ── Validation ───────────────────────────────────────────────────────

    /// <summary>Columns a form can fill: not computed, and not a key the database assigns.</summary>
    public static bool IsWritable(ColumnDef column) =>
        column.Type != ColumnType.Computed && !(column.PrimaryKey && column.Type is ColumnType.Integer or ColumnType.Uuid);

    /// <summary>Columns a new row must get: not nullable, without a default.</summary>
    public static bool IsRequired(ColumnDef column) =>
        IsWritable(column) && !column.Nullable && column.DefaultValue is null && column.InternalDefaultSql is null;

    /// <summary>
    /// The form must fit its table as it is now: known writable columns, each
    /// once; a hidden field has a default; every column a new row needs is
    /// asked for or filled by a default. Returns the table and the JSON to store.
    /// </summary>
    private (string Table, string Json) Validate(JsonElement form)
    {
        if (form.ValueKind != JsonValueKind.Object || JsonNode.Parse(form.GetRawText()) is not JsonObject json)
            throw Invalid("form", "form must be an object.");
        var tableName = json["table"] is JsonValue t && t.TryGetValue<string>(out var name) ? name.Trim() : null;
        if (string.IsNullOrEmpty(tableName))
            throw Invalid("form.table", "Choose the table the form adds rows to.");
        var table = _schemaCache.GetTable(tableName)
            ?? throw Invalid("form.table", $"There is no table '{tableName}'.");
        json["table"] = table.Name;

        if (json["fields"] is not JsonArray fields || fields.Count == 0)
            throw Invalid("form.fields", "A form needs at least one field.");
        if (fields.Count > MaxFields)
            throw Invalid("form.fields", $"A form holds at most {MaxFields} fields and sections.");

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var filled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < fields.Count; i++)
        {
            var path = $"form.fields[{i}]";
            if (fields[i] is not JsonObject field)
                throw Invalid(path, "Each field must be an object.");
            if (field["section"] is JsonValue section)
            {
                if (!section.TryGetValue<string>(out var title) || title.Length > 120)
                    throw Invalid($"{path}.section", "A section has a title of at most 120 characters.");
                continue;
            }

            var columnName = field["column"] is JsonValue c && c.TryGetValue<string>(out var value) ? value : null;
            var column = columnName is null ? null : table.Columns.FirstOrDefault(col => string.Equals(col.Name, columnName, StringComparison.OrdinalIgnoreCase));
            if (column is null)
                throw Invalid($"{path}.column", columnName is null
                    ? "Each field names a column, or is a section."
                    : $"{table.DisplayName ?? table.Name} has no column '{columnName}'.");
            if (!IsWritable(column))
                throw Invalid($"{path}.column", $"{column.DisplayName ?? column.Name} is computed or assigned by the database; a form cannot fill it.");
            if (!used.Add(column.Name))
                throw Invalid($"{path}.column", $"{column.DisplayName ?? column.Name} is in the form twice.");
            field["column"] = column.Name;

            foreach (var (key, max) in new[] { ("label", 120), ("help", 500) })
            {
                if (field[key] is { } text && (text is not JsonValue v || !v.TryGetValue<string>(out var s) || s.Length > max))
                    throw Invalid($"{path}.{key}", $"{key} is text of at most {max} characters.");
            }

            var hasDefault = false;
            if (field["default"] is JsonObject fallback)
            {
                var kind = fallback["kind"] is JsonValue k && k.TryGetValue<string>(out var kindName) ? kindName : null;
                if (kind is null || !DefaultKinds.Contains(kind))
                    throw Invalid($"{path}.default.kind", "default.kind is today, now, last (the value used last time) or value.");
                if (kind is "today" or "now" && column.Type is not (ColumnType.Date or ColumnType.DateTime))
                    throw Invalid($"{path}.default.kind", $"{column.DisplayName ?? column.Name} is not a date; it cannot default to {kind}.");
                if (kind == "value" && fallback["value"] is null)
                    throw Invalid($"{path}.default.value", "A fixed default needs a value.");
                // "last" has nothing the first time: it fills a required column only together with a visible field.
                hasDefault = kind != "last";
            }
            else if (field["default"] is not null)
            {
                throw Invalid($"{path}.default", "default is an object: { \"kind\": \"today\" }.");
            }

            if (field["sign"] is { } sign)
            {
                if (sign is not JsonValue sv || !sv.TryGetValue<string>(out var signName) || signName is not ("negative" or "positive"))
                    throw Invalid($"{path}.sign", "sign is negative or positive: the field shows a +/− switch starting there.");
                if (column.Type is not (ColumnType.Integer or ColumnType.Float or ColumnType.Currency))
                    throw Invalid($"{path}.sign", $"{column.DisplayName ?? column.Name} is not a number; it has no sign.");
            }

            var hidden = field["hidden"] is JsonValue h && h.TryGetValue<bool>(out var isHidden) && isHidden;
            if (hidden && !hasDefault)
                throw Invalid($"{path}.hidden", $"A hidden field needs a default (today, now or a fixed value): {column.DisplayName ?? column.Name}.");
            filled.Add(column.Name);
        }

        if (filled.Count == 0)
            throw Invalid("form.fields", "A form needs at least one field.");
        var missing = table.Columns.Where(c => IsRequired(c) && !filled.Contains(c.Name)).Select(c => c.DisplayName ?? c.Name).ToList();
        if (missing.Count > 0)
            throw Invalid("form.fields", $"{table.DisplayName ?? table.Name} needs a value for {string.Join(", ", missing)}; add {(missing.Count == 1 ? "it" : "them")} to the form.");

        if (json["pinned"] is { } pinned && (pinned is not JsonValue p || !p.TryGetValue<bool>(out _)))
            throw Invalid("form.pinned", "pinned is true or false.");

        return (table.Name, json.ToJsonString());
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private sealed class FormRow
    {
        public long Id { get; init; }
        public string Name { get; init; } = "";
        public string TableName { get; init; } = "";
        public string FormJson { get; init; } = "{}";
        public long? OwnerUserId { get; init; }
        public bool IsShared { get; init; }
        public string CreatedAt { get; init; } = "";
        public string UpdatedAt { get; init; } = "";
    }

    private string Columns =>
        "id, name, table_name AS TableName, form_json AS FormJson, owner_user_id AS OwnerUserId, is_shared AS IsShared, " +
        $"{SystemSqlDialect.TimestampText(_systemPool.Provider, "created_at")} AS CreatedAt, " +
        $"{SystemSqlDialect.TimestampText(_systemPool.Provider, "updated_at")} AS UpdatedAt";

    private async Task<FormRow> GetVisibleAsync(System.Data.IDbConnection conn, long id, long? userId)
    {
        var row = await conn.QuerySingleOrDefaultAsync<FormRow>($"SELECT {Columns} FROM {Table} WHERE id = @Id AND workspace_id = @Ws", new { Id = id, Ws });
        if (row is null || (row.OwnerUserId != userId && !row.IsShared))
            throw new ApiNotFoundException($"Form {id} not found.");
        return row;
    }

    private static FormResponse ToResponse(FormRow row, long? userId)
    {
        using var json = JsonDocument.Parse(string.IsNullOrWhiteSpace(row.FormJson) ? "{}" : row.FormJson);
        return new FormResponse(row.Id, row.Name, row.TableName, row.IsShared, row.OwnerUserId == userId, json.RootElement.Clone(), row.CreatedAt, row.UpdatedAt);
    }

    private static string RequireName(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            throw Invalid("name", "Give the form a name, e.g. \"Add transaction\".");
        if (trimmed.Length > 255)
            throw Invalid("name", "The name is too long.");
        return trimmed;
    }

    private long? UserId() => HttpContext.User.GetUserId();

    private static long Ws => Services.Workspaces.WorkspaceContext.Id;
    private string Table => _systemPool.Provider == DatabaseProvider.Postgres ? "_system.forms" : "forms";
    private string SharedTrue => _systemPool.Provider == DatabaseProvider.Postgres ? "" : "= 1";
    private string JsonParam => SystemSqlDialect.JsonParam(_systemPool.Provider, "@Json");
    private string NowParam => SystemSqlDialect.TimestampParam(_systemPool.Provider, "@Now");

    private static string UtcNowIso() => DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

    private static ApiValidationException Invalid(string field, string message) =>
        new(message, new Dictionary<string, object?> { ["field"] = field });
}
