using System.Globalization;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Bento.Api.Authorization;
using Bento.Api.Db;
using Bento.Api.Exceptions;
using Bento.Api.Models;
using Bento.Api.Services;

namespace Bento.Api.Controllers;

/// <summary>
/// Aggregation queries (Step 3.2): <c>POST /api/v1/query</c> runs one. Reads
/// only, so every role that may read data may run them.
/// </summary>
[ApiController]
[Route(ApiRoutes.Query)]
[RequireRole("viewer", "api")]
public sealed class QueryController : ControllerBase
{
    private readonly AggregationQueryService _queries;

    public QueryController(AggregationQueryService queries)
    {
        _queries = queries;
    }

    [HttpPost]
    public async Task<IActionResult> Run([FromBody] AggregationQueryRequest request)
    {
        var result = await _queries.RunAsync(request);
        return Ok(new { data = result.Data, meta = result.Meta });
    }
}

/// <summary>
/// Saved queries (Step 3.2): named aggregation queries in
/// <c>_system.saved_queries</c>, private or shared like saved views. Only the
/// owner changes or deletes one; everyone who can see it can run it.
/// </summary>
[ApiController]
[Route(ApiRoutes.SavedQueries)]
[RequireRole("viewer", "api")]
public sealed class SavedQueriesController : ControllerBase
{
    private static readonly JsonSerializerOptions QueryJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly AggregationQueryService _queries;
    private readonly ISystemPool _systemPool;

    public SavedQueriesController(AggregationQueryService queries, ISystemPool systemPool)
    {
        _queries = queries;
        _systemPool = systemPool;
    }

    /// <summary>Own queries and others' shared ones; <c>?source=table</c> narrows to one table.</summary>
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? source)
    {
        var userId = UserId();
        using var conn = await _systemPool.GetConnectionAsync();
        var rows = await conn.QueryAsync<SavedQueryRow>(
            $"""
            SELECT {Columns}
            FROM {Table}
            WHERE (owner_user_id = @UserId OR is_shared {SharedTrue})
              AND (@Source IS NULL OR LOWER(source_table) = LOWER(@Source))
            ORDER BY LOWER(name)
            """,
            new { UserId = userId, Source = string.IsNullOrWhiteSpace(source) ? null : source.Trim() });
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
    public async Task<IActionResult> Create([FromBody] SavedQueryRequest request)
    {
        var userId = UserId();
        var name = RequireName(request.Name);
        var query = RequireQuery(request.Query);
        var now = UtcNowIso();

        using var conn = await _systemPool.GetConnectionAsync();
        var id = await conn.ExecuteScalarAsync<long>(
            $"""
            INSERT INTO {Table} (name, source_table, config, owner_user_id, is_shared, created_at, updated_at)
            VALUES (@Name, @Source, {ConfigParam}, @OwnerId, @IsShared, {NowParam}, {NowParam})
            RETURNING id
            """,
            new { Name = name, Source = query.Source!.Trim(), Config = Serialize(query), OwnerId = userId, IsShared = request.IsShared ?? false, Now = now });

        return StatusCode(StatusCodes.Status201Created, new { data = ToResponse(await GetVisibleAsync(conn, id, userId), userId) });
    }

    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] SavedQueryRequest request)
    {
        var userId = UserId();
        using var conn = await _systemPool.GetConnectionAsync();
        var existing = await GetVisibleAsync(conn, id, userId);
        RequireOwner(existing, userId);

        var name = request.Name is null ? existing.Name : RequireName(request.Name);
        var query = request.Query is null ? null : RequireQuery(request.Query);
        await conn.ExecuteAsync(
            $"""
            UPDATE {Table}
            SET name = @Name, source_table = @Source, config = {ConfigParam}, is_shared = @IsShared, updated_at = {NowParam}
            WHERE id = @Id
            """,
            new
            {
                Id = id,
                Name = name,
                Source = query?.Source!.Trim() ?? existing.SourceTable,
                Config = query is null ? existing.Config : Serialize(query),
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
        RequireOwner(await GetVisibleAsync(conn, id, userId), userId);
        await conn.ExecuteAsync($"DELETE FROM {Table} WHERE id = @Id", new { Id = id });
        return NoContent();
    }

    /// <summary>Runs a saved query; extra filters (e.g. a dashboard's date range) are added to its own.</summary>
    [HttpPost("{id:long}/run")]
    public async Task<IActionResult> Run(long id, [FromBody] SavedQueryRunRequest? request)
    {
        var userId = UserId();
        AggregationQueryRequest query;
        using (var conn = await _systemPool.GetConnectionAsync())
        {
            var row = await GetVisibleAsync(conn, id, userId);
            query = JsonSerializer.Deserialize<AggregationQueryRequest>(row.Config, QueryJson)
                ?? throw new ApiUnprocessableEntityException($"Saved query {id} cannot be read.");
        }

        var result = await _queries.RunAsync(query, request?.Filter);
        return Ok(new { data = result.Data, meta = result.Meta });
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private sealed class SavedQueryRow
    {
        public long Id { get; init; }
        public string Name { get; init; } = "";
        public string SourceTable { get; init; } = "";
        public string Config { get; init; } = "{}";
        public long? OwnerUserId { get; init; }
        public bool IsShared { get; init; }
        public string CreatedAt { get; init; } = "";
        public string UpdatedAt { get; init; } = "";
    }

    private string Columns =>
        $"id, name, source_table AS SourceTable, config, owner_user_id AS OwnerUserId, is_shared AS IsShared, " +
        $"{SystemSqlDialect.TimestampText(_systemPool.Provider, "created_at")} AS CreatedAt, " +
        $"{SystemSqlDialect.TimestampText(_systemPool.Provider, "updated_at")} AS UpdatedAt";

    private async Task<SavedQueryRow> GetVisibleAsync(System.Data.IDbConnection conn, long id, long? userId)
    {
        var row = await conn.QuerySingleOrDefaultAsync<SavedQueryRow>(
            $"SELECT {Columns} FROM {Table} WHERE id = @Id", new { Id = id });
        if (row is null || (row.OwnerUserId != userId && !row.IsShared))
            throw new ApiNotFoundException($"Saved query {id} not found.");
        return row;
    }

    private static void RequireOwner(SavedQueryRow row, long? userId)
    {
        if (row.OwnerUserId != userId)
            throw new ApiForbiddenException("Only the owner can change or delete this query.");
    }

    private AggregationQueryRequest RequireQuery(AggregationQueryRequest? query)
    {
        if (query is null)
            throw new ApiValidationException("query is required.", new Dictionary<string, object?> { ["field"] = "query" });
        _queries.Validate(query);
        return query;
    }

    private static string RequireName(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            throw new ApiValidationException("Give the query a name.", new Dictionary<string, object?> { ["field"] = "name" });
        if (trimmed.Length > 255)
            throw new ApiValidationException("The name is too long.", new Dictionary<string, object?> { ["field"] = "name" });
        return trimmed;
    }

    private static SavedQueryResponse ToResponse(SavedQueryRow row, long? userId)
    {
        using var config = JsonDocument.Parse(string.IsNullOrWhiteSpace(row.Config) ? "{}" : row.Config);
        return new SavedQueryResponse(row.Id, row.Name, row.SourceTable, row.IsShared, row.OwnerUserId == userId,
            config.RootElement.Clone(), row.CreatedAt, row.UpdatedAt);
    }

    private static string Serialize(AggregationQueryRequest query) => JsonSerializer.Serialize(query, QueryJson);

    private long? UserId() => HttpContext.User.GetUserId();

    private string Table => _systemPool.Provider == DatabaseProvider.Postgres ? "_system.saved_queries" : "saved_queries";
    private string SharedTrue => _systemPool.Provider == DatabaseProvider.Postgres ? "" : "= 1";
    private string ConfigParam => SystemSqlDialect.JsonParam(_systemPool.Provider, "@Config");
    private string NowParam => SystemSqlDialect.TimestampParam(_systemPool.Provider, "@Now");

    private static string UtcNowIso() => DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
}
