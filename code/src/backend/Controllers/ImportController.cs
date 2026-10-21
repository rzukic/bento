using System.Globalization;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Bento.Api.Authorization;
using Bento.Api.Db;
using Bento.Api.Exceptions;
using Bento.Api.Models;
using Bento.Api.Services;
using Bento.Api.Services.Import;

namespace Bento.Api.Controllers;

/// <summary>
/// CSV import (Step 3.1): analyse a file, check it against a table (dry run),
/// import it, and keep named mapping profiles per table. Importing writes
/// rows, so every route needs <c>editor</c> or above, or <c>api</c>.
/// </summary>
[ApiController]
[Route(ApiRoutes.Import)]
[RequireRole("editor", "api")]
public sealed class ImportController : ControllerBase
{
    private static readonly JsonSerializerOptions ConfigJson = new(JsonSerializerDefaults.Web);

    private readonly ImportService _importService;
    private readonly ISystemPool _systemPool;
    private readonly SchemaCache _schemaCache;
    private readonly ICustomValueRecalculation? _recalculation;
    private readonly ImportFetcher _fetcher;

    public ImportController(ImportService importService, ISystemPool systemPool, SchemaCache schemaCache, ImportFetcher fetcher, ICustomValueRecalculation? recalculation = null)
    {
        _importService = importService;
        _systemPool = systemPool;
        _schemaCache = schemaCache;
        _fetcher = fetcher;
        _recalculation = recalculation;
    }

    /// <summary>The largest file the page can fetch: it goes back to the browser as base64 and returns in the next request.</summary>
    public const long MaxPageFetchBytes = 7 * 1024 * 1024;

    /// <summary>
    /// Fetches a file from a URL for the import page (Step 5.1), which then
    /// analyses and checks it like an upload. Credentials from the vault
    /// (<c>{{name}}</c>) need <c>admin</c>.
    /// </summary>
    [HttpPost("fetch")]
    public async Task<IActionResult> Fetch([FromBody] ImportFetchRequest request)
    {
        if (ImportFetcher.CredentialNames(request.Url, request.Headers).Count > 0 && !User.IsInstallationAdmin())
            throw new ApiForbiddenException("Only installation admins can use credentials from the vault in an import.");
        var file = await _fetcher.FetchAsync(request.Url, request.Headers, HttpContext.RequestAborted);
        if (file.Bytes.Length > MaxPageFetchBytes)
            throw new ApiUnprocessableEntityException(
                $"The file is {file.Bytes.Length / 1024 / 1024} MB; the import page takes up to {MaxPageFetchBytes / 1024 / 1024} MB. Save it as a scheduled import, which reads up to {_fetcher.Options.MaxBytes / 1024 / 1024} MB.");
        return Ok(new { data = new ImportFetchResponse(Convert.ToBase64String(file.Bytes), file.FileName, file.ContentType, file.Bytes.Length, file.FinalUrl) });
    }

    /// <summary>Detects encoding, delimiter, header and locale, and returns columns with samples.</summary>
    [HttpPost("analyze")]
    public IActionResult Analyze([FromBody] ImportAnalyzeRequest request) =>
        Ok(new { data = _importService.Analyze(request) });

    /// <summary>Converts, de-duplicates and validates every row without writing anything.</summary>
    [HttpPost("{table}/preview")]
    public async Task<IActionResult> Preview(string table, [FromBody] ImportRunRequest request) =>
        Ok(new { data = await _importService.RunAsync(table, request, dryRun: true) });

    /// <summary>Imports the new, valid rows in one transaction; duplicates and invalid rows are skipped.</summary>
    [HttpPost("{table}")]
    public async Task<IActionResult> Run(string table, [FromBody] ImportRunRequest request)
    {
        var result = await _importService.RunAsync(table, request, dryRun: false);
        _recalculation?.Request();
        return Ok(new { data = result });
    }

    // ── Profiles ──────────────────────────────────────────────────────────

    [HttpGet("{table}/profiles")]
    public async Task<IActionResult> ListProfiles(string table)
    {
        var tableName = ResolveTableName(table);
        using var conn = await _systemPool.GetConnectionAsync();
        var rows = await conn.QueryAsync<ProfileRow>(
            $"""
            SELECT id, name, table_name AS TableName, config,
                   {CreatedAtText} AS CreatedAt, {UpdatedAtText} AS UpdatedAt
            FROM {ProfilesTable}
            WHERE workspace_id = @Ws AND table_name = @TableName
            ORDER BY name
            """,
            new { TableName = tableName, Ws });
        return Ok(new { data = rows.Select(ToResponse) });
    }

    [HttpPost("{table}/profiles")]
    public async Task<IActionResult> CreateProfile(string table, [FromBody] ImportProfileRequest request)
    {
        var tableName = ResolveTableName(table);
        var name = RequireName(request.Name);
        var config = SerializeConfig(request.Config);
        var now = UtcNowIso();

        using var conn = await _systemPool.GetConnectionAsync();
        await EnsureNameFreeAsync(conn, tableName, name, exceptId: null);
        var id = await conn.ExecuteScalarAsync<long>(
            $"""
            INSERT INTO {ProfilesTable} (workspace_id, name, table_name, config, created_by, created_at, updated_at)
            VALUES (@Ws, @Name, @TableName, {ConfigParam}, @CreatedBy, {NowParam}, {NowParam})
            RETURNING id
            """,
            new { Name = name, TableName = tableName, Config = config, CreatedBy = HttpContext.User.GetUserId(), Now = now, Ws });

        return StatusCode(StatusCodes.Status201Created, new { data = ToResponse(await GetProfileAsync(conn, tableName, id)) });
    }

    [HttpPut("{table}/profiles/{id:long}")]
    public async Task<IActionResult> UpdateProfile(string table, long id, [FromBody] ImportProfileRequest request)
    {
        var tableName = ResolveTableName(table);
        var name = RequireName(request.Name);
        var config = SerializeConfig(request.Config);

        using var conn = await _systemPool.GetConnectionAsync();
        await GetProfileAsync(conn, tableName, id);
        await EnsureNameFreeAsync(conn, tableName, name, exceptId: id);
        await conn.ExecuteAsync(
            $"UPDATE {ProfilesTable} SET name = @Name, config = {ConfigParam}, updated_at = {NowParam} WHERE id = @Id",
            new { Id = id, Name = name, Config = config, Now = UtcNowIso() });

        return Ok(new { data = ToResponse(await GetProfileAsync(conn, tableName, id)) });
    }

    [HttpDelete("{table}/profiles/{id:long}")]
    public async Task<IActionResult> DeleteProfile(string table, long id)
    {
        var tableName = ResolveTableName(table);
        using var conn = await _systemPool.GetConnectionAsync();
        await GetProfileAsync(conn, tableName, id);
        await conn.ExecuteAsync($"DELETE FROM {ProfilesTable} WHERE id = @Id", new { Id = id });
        return NoContent();
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private sealed class ProfileRow
    {
        public long Id { get; init; }
        public string Name { get; init; } = "";
        public string TableName { get; init; } = "";
        public string Config { get; init; } = "{}";
        public string CreatedAt { get; init; } = "";
        public string UpdatedAt { get; init; } = "";
    }

    private static ImportProfileResponse ToResponse(ProfileRow row)
    {
        using var config = JsonDocument.Parse(string.IsNullOrWhiteSpace(row.Config) ? "{}" : row.Config);
        return new ImportProfileResponse(row.Id, row.Name, row.TableName, config.RootElement.Clone(), row.CreatedAt, row.UpdatedAt);
    }

    private async Task<ProfileRow> GetProfileAsync(System.Data.IDbConnection conn, string tableName, long id)
    {
        return await conn.QuerySingleOrDefaultAsync<ProfileRow>(
            $"""
            SELECT id, name, table_name AS TableName, config,
                   {CreatedAtText} AS CreatedAt, {UpdatedAtText} AS UpdatedAt
            FROM {ProfilesTable}
            WHERE id = @Id AND workspace_id = @Ws AND table_name = @TableName
            """,
            new { Id = id, TableName = tableName, Ws })
            ?? throw new ApiNotFoundException($"Import profile {id} not found for '{tableName}'.");
    }

    private async Task EnsureNameFreeAsync(System.Data.IDbConnection conn, string tableName, string name, long? exceptId)
    {
        var taken = await conn.ExecuteScalarAsync<long>(
            $"SELECT COUNT(*) FROM {ProfilesTable} WHERE workspace_id = @Ws AND table_name = @TableName AND LOWER(name) = LOWER(@Name) AND (@ExceptId IS NULL OR id <> @ExceptId)",
            new { TableName = tableName, Name = name, ExceptId = exceptId, Ws });
        if (taken > 0)
            throw new ApiConflictException($"A profile named '{name}' already exists for this table.",
                new Dictionary<string, object?> { ["field"] = "name" });
    }

    private string ResolveTableName(string table) =>
        _schemaCache.GetTable(table)?.Name
        ?? throw new ApiNotFoundException($"Table '{table}' does not exist.",
            new Dictionary<string, object?> { ["table"] = table });

    private static string RequireName(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            throw new ApiValidationException("Give the profile a name.", new Dictionary<string, object?> { ["field"] = "name" });
        if (trimmed.Length > 255)
            throw new ApiValidationException("The name is too long.", new Dictionary<string, object?> { ["field"] = "name" });
        return trimmed;
    }

    private static string SerializeConfig(ImportProfileConfig? config) =>
        JsonSerializer.Serialize(config ?? new ImportProfileConfig(), ConfigJson);

    private static long Ws => Services.Workspaces.WorkspaceContext.Id;

    private string ProfilesTable =>
        _systemPool.Provider == DatabaseProvider.Postgres ? "_system.import_profiles" : "import_profiles";

    private string ConfigParam => SystemSqlDialect.JsonParam(_systemPool.Provider, "@Config");
    private string NowParam => SystemSqlDialect.TimestampParam(_systemPool.Provider, "@Now");
    private string CreatedAtText => SystemSqlDialect.TimestampText(_systemPool.Provider, "created_at");
    private string UpdatedAtText => SystemSqlDialect.TimestampText(_systemPool.Provider, "updated_at");

    private static string UtcNowIso() => DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
}
