using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bento.Api.Db;
using Bento.Api.Db.Schema;
using Bento.Api.Exceptions;
using Bento.Api.Models;
using Bento.Api.Services.Workspaces;
using Dapper;

namespace Bento.Api.Services;

/// <summary>
/// Saved queries as tables (Step 5.8): a saved query's result kept as a table
/// of the model — read-only, its rows replaced from the query whenever the
/// workspace's data or model changed since, before anyone reads it. Being a
/// real table, it works everywhere one does: as the source of other queries,
/// in formulas and variables, dashboards, the API, MCP and share links.
/// </summary>
public sealed partial class QueryTables
{
    private const int MaxRows = 10_000;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IServiceProvider _services;
    private readonly ISystemPool _systemPool;
    private readonly SchemaCache _schemaCache;
    private readonly DataVersion _versions;
    private readonly ILogger<QueryTables>? _logger;

    /// <summary>The data version each table was last filled at, per workspace.</summary>
    private readonly ConcurrentDictionary<(long Workspace, string Table), (long Version, DateTime At, int Rows, string? Error)> _filled = new();
    private readonly ConcurrentDictionary<(long Workspace, string Table), SemaphoreSlim> _locks = new();
    /// <summary>Tables being filled in this flow: a query reading its own table does not loop.</summary>
    private static readonly AsyncLocal<HashSet<string>?> Filling = new();

    public QueryTables(IServiceProvider services, ISystemPool systemPool, SchemaCache schemaCache, DataVersion versions, ILogger<QueryTables>? logger = null)
    {
        _services = services;
        _systemPool = systemPool;
        _schemaCache = schemaCache;
        _versions = versions;
        _logger = logger;
    }

    public sealed record Status(string Table, long QueryId, string? RefreshedAt, int Rows, bool Current, string? Error);

    // ── Making one ───────────────────────────────────────────────────────

    /// <summary>Adds the saved query's result to the model as a table, and fills it.</summary>
    public async Task<TableDef> CreateAsync(long queryId, string? name)
    {
        var (queryName, query) = await LoadQueryAsync(queryId);
        var tableName = (string.IsNullOrWhiteSpace(name) ? Identifier(queryName) : name.Trim()).ToLowerInvariant();
        if (!TableName().IsMatch(tableName))
            throw Invalid("name", "A table name is lower-case letters, digits and underscores, starting with a letter.");
        if (_schemaCache.GetTable(tableName) is not null)
            throw Invalid("name", $"There is a table '{tableName}' already.");

        var result = await RunAsync(query);
        var table = Define(tableName, queryId, queryName, result.Meta.Columns);
        await ApplyAsync(model => model with { Tables = [.. model.Tables, table] });
        await FillAsync(tableName);
        return _schemaCache.GetTable(tableName)!;
    }

    /// <summary>Fills the table from its query now, whether or not anything changed.</summary>
    public async Task<Status> RefreshAsync(string tableName)
    {
        var table = _schemaCache.GetTable(tableName) ?? throw new ApiNotFoundException($"Table '{tableName}' does not exist.");
        if (CrudService.SourceQueryOf(table) is null)
            throw Invalid("table", $"'{table.Name}' is not made from a saved query.");
        await FillAsync(table.Name);
        return StatusOf(_schemaCache.GetTable(table.Name)!);
    }

    public Status StatusOf(TableDef table)
    {
        var key = (WorkspaceContext.Id, table.Name.ToLowerInvariant());
        var queryId = CrudService.SourceQueryOf(table) ?? 0;
        return _filled.TryGetValue(key, out var filled)
            ? new Status(table.Name, queryId, filled.At.ToString("o", CultureInfo.InvariantCulture), filled.Rows, filled.Version == _versions.Current, filled.Error)
            : new Status(table.Name, queryId, null, 0, false, null);
    }

    /// <summary>The saved query changed: its tables are filled again when next read.</summary>
    public void Forget(long queryId)
    {
        foreach (var table in TablesOf(queryId))
            _filled.TryRemove((WorkspaceContext.Id, table.ToLowerInvariant()), out _);
    }

    /// <summary>The tables made from a saved query (a query with one cannot be deleted).</summary>
    public IReadOnlyList<string> TablesOf(long queryId) =>
        _schemaCache.Current.Tables.Where(t => CrudService.SourceQueryOf(t) == queryId).Select(t => t.Name).ToList();

    // ── Keeping it current ───────────────────────────────────────────────

    /// <summary>
    /// Before a table is read: when it is made from a saved query and the
    /// workspace's data or model changed since it was filled, it is filled
    /// again. A query that fails leaves the rows as they were.
    /// </summary>
    public async Task EnsureCurrentAsync(string? tableName)
    {
        if (string.IsNullOrWhiteSpace(tableName) || _schemaCache.GetTable(tableName.Trim()) is not { } table || CrudService.SourceQueryOf(table) is null)
            return;
        var key = (WorkspaceContext.Id, table.Name.ToLowerInvariant());
        if (_filled.TryGetValue(key, out var filled) && filled.Version == _versions.Current)
            return;
        if (Filling.Value?.Contains(key.Item2) == true)
            return;
        try
        {
            await FillAsync(table.Name);
        }
        catch (Exception e) when (e is ApiException or InvalidOperationException or System.Data.Common.DbException)
        {
            _logger?.LogWarning(e, "Table {Table} could not be filled from its saved query.", table.Name);
            _filled[key] = (_versions.Current, DateTime.UtcNow, filled.Rows, e.Message);
        }
    }

    private async Task FillAsync(string tableName)
    {
        var key = (WorkspaceContext.Id, tableName.ToLowerInvariant());
        var gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        var filling = Filling.Value ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        filling.Add(key.Item2);
        try
        {
            var table = _schemaCache.GetTable(tableName) ?? throw new ApiNotFoundException($"Table '{tableName}' does not exist.");
            var queryId = CrudService.SourceQueryOf(table)!.Value;
            // What it is current with: the version before the query reads, so a change meanwhile is not missed.
            var version = _versions.Current;
            var (queryName, query) = await LoadQueryAsync(queryId);
            var result = await RunAsync(query);

            // The query's columns changed (an edited query, a renamed column): the table follows.
            var wanted = Define(table.Name, queryId, queryName, result.Meta.Columns) with { DisplayName = table.DisplayName ?? queryName };
            if (!SameColumns(table, wanted))
            {
                await ApplyAsync(model => model with { Tables = model.Tables.Select(t => string.Equals(t.Name, table.Name, StringComparison.OrdinalIgnoreCase) ? wanted : t).ToList() });
                table = _schemaCache.GetTable(table.Name)!;
            }

            var names = result.Meta.Columns.Select(c => (c.Name, Column: ColumnName(c.Name))).ToList();
            var bodies = result.Data.Select((row, index) =>
            {
                var values = new Dictionary<string, object?> { ["id"] = index + 1 };
                foreach (var (output, column) in names)
                    values[column] = row.TryGetValue(output, out var value) ? value : null;
                return JsonSerializer.SerializeToElement(values, Json);
            }).ToList();
            await _services.GetRequiredService<CrudService>().ReplaceAllAsync(table, bodies);
            _filled[key] = (version, DateTime.UtcNow, bodies.Count, result.Meta.Truncated ? $"Only the first {MaxRows} rows of the query are kept." : null);
            _logger?.LogInformation("Table {Table} filled from saved query {Query}: {Rows} rows.", table.Name, queryId, bodies.Count);
        }
        finally
        {
            filling.Remove(key.Item2);
            gate.Release();
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private async Task<(string Name, AggregationQueryRequest Query)> LoadQueryAsync(long id)
    {
        var table = _systemPool.Provider == DatabaseProvider.Postgres ? "_system.saved_queries" : "saved_queries";
        using var conn = await _systemPool.GetConnectionAsync();
        var row = await conn.QuerySingleOrDefaultAsync<(string Name, string Config)?>(
            $"SELECT name, CAST(config AS TEXT) FROM {table} WHERE id = @Id AND workspace_id = @Ws", new { Id = id, Ws = WorkspaceContext.Id })
            ?? throw new ApiNotFoundException($"Saved query {id} not found.");
        var query = JsonSerializer.Deserialize<AggregationQueryRequest>(row.Config, Json)
            ?? throw new ApiUnprocessableEntityException($"Saved query {id} cannot be read.");
        return (row.Name, query);
    }

    private async Task<AggregationQueryResponse> RunAsync(AggregationQueryRequest query) =>
        await _services.GetRequiredService<AggregationQueryService>().RunAsync(query with { Limit = Math.Min(query.Limit ?? MaxRows, MaxRows) });

    private async Task ApplyAsync(Func<ModelJson, ModelJson> change)
    {
        var schema = _services.GetRequiredService<SchemaService>();
        var result = await schema.ApplySchemaAsync(change(_schemaCache.Current), confirmDestructive: true);
        if (result.Errors.Count > 0)
            throw Invalid("table", string.Join(" ", result.Errors.Select(e => e.Message)));
        await _schemaCache.ReloadAsync();
    }

    /// <summary>The table for a query's result: a row number as its key, then a column per result column.</summary>
    private static TableDef Define(string name, long queryId, string queryName, IReadOnlyList<QueryResultColumn> columns)
    {
        var defined = new List<ColumnDef> { new() { Name = "id", Type = ColumnType.Integer, TypeSpecified = true, PrimaryKey = true } };
        foreach (var column in columns)
            defined.Add(new ColumnDef { Name = ColumnName(column.Name), Type = TypeOf(column.Type), TypeSpecified = true, Nullable = true });
        return new TableDef
        {
            Name = name,
            DisplayName = queryName,
            Description = $"From the saved query “{queryName}”: filled again whenever the data changes. Change the query, not its rows.",
            Columns = defined,
            AdditionalData = new Dictionary<string, JsonElement> { ["fromQuery"] = JsonSerializer.SerializeToElement(queryId) }
        };
    }

    private static bool SameColumns(TableDef table, TableDef wanted) =>
        table.Columns.Count == wanted.Columns.Count &&
        table.Columns.Zip(wanted.Columns).All(p => string.Equals(p.First.Name, p.Second.Name, StringComparison.OrdinalIgnoreCase) && p.First.Type == p.Second.Type);

    private static ColumnType TypeOf(string type) => type switch
    {
        "date" => ColumnType.Date,
        "datetime" => ColumnType.DateTime,
        "integer" or "reference" => ColumnType.Integer,
        "float" or "number" => ColumnType.Float,
        "currency" => ColumnType.Currency,
        "boolean" => ColumnType.Boolean,
        _ => ColumnType.Text
    };

    /// <summary>A result column as a column name: "id" is the row's key, so a result "id" becomes "id_value".</summary>
    private static string ColumnName(string output)
    {
        var name = Identifier(output);
        return name == "id" ? "id_value" : name;
    }

    private static string Identifier(string text)
    {
        var name = NotIdentifier().Replace(text.ToLowerInvariant(), "_").Trim('_');
        if (name.Length == 0 || !char.IsLetter(name[0]))
            name = "q_" + name;
        return name.Length > 60 ? name[..60] : name;
    }

    private static ApiValidationException Invalid(string field, string message) =>
        new(message, new Dictionary<string, object?> { ["field"] = field });

    [GeneratedRegex("^[a-z][a-z0-9_]{0,62}$")]
    private static partial Regex TableName();

    [GeneratedRegex("[^a-z0-9_]+")]
    private static partial Regex NotIdentifier();
}
