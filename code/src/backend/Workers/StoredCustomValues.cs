using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Bento.Api.Db;
using Bento.Api.Formula;
using Bento.Api.Formula.Sandbox;
using Bento.Api.Services;
using Dapper;

namespace Bento.Api.Workers;

public sealed record CustomValueFailure(string Table, string Column, string Function, int Rows, string Error);

public sealed record CustomValueStatus(bool Pending, string? LastRunAt, int LastUpdated, IReadOnlyList<CustomValueFailure> Failures);

/// <summary>
/// Stored values of computed columns that call a custom function (Step 4.1,
/// Part 5 R5.9 Tier 3). A formula reads the last stored value at once (empty
/// until it is first computed); this worker recalculates in the background
/// after data, schema or function changes, and every minute as a sweep.
///
/// Values live in <c>_bento_function_values</c> in the data database, one row
/// per table, column and row key, with the inputs and function version they
/// were computed from: a pass only runs the function where those changed.
/// </summary>
public sealed class StoredCustomValues : BackgroundService, ICustomValueRecalculation
{
    private const int MaxPasses = 3;
    private static readonly TimeSpan Sweep = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(150);

    private readonly IDataPool _dataPool;
    private readonly ISystemPool _systemPool;
    private readonly SchemaCache _schemaCache;
    private readonly ComputedColumns _computed;
    private readonly ILogger<StoredCustomValues>? _logger;
    private readonly Channel<bool> _requests = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly SemaphoreSlim _running = new(1, 1);
    // Per workspace (Step 5.5): its values table, whether a run is asked for, the last run.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, bool> _tableReady = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, bool> _pending = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, CustomValueStatus> _status = new();
    private readonly Services.Workspaces.WorkspaceService? _workspaces;

    private static long Ws => Services.Workspaces.WorkspaceContext.Id;

    private CustomValueStatus CurrentStatus
    {
        get => _status.GetValueOrDefault(Ws) ?? new CustomValueStatus(false, null, 0, []);
        set => _status[Ws] = value;
    }

    private readonly Services.DataVersion? _versions;

    public StoredCustomValues(
        IDataPool dataPool,
        ISystemPool systemPool,
        SchemaCache schemaCache,
        ComputedColumns computed,
        ILogger<StoredCustomValues>? logger = null,
        Services.Workspaces.WorkspaceService? workspaces = null,
        Services.DataVersion? versions = null)
    {
        _versions = versions;
        _workspaces = workspaces;
        _dataPool = dataPool;
        _systemPool = systemPool;
        _schemaCache = schemaCache;
        _computed = computed;
        _logger = logger;
        _schemaCache.OnSchemaChanged(_ => Request());
    }

    /// <summary>The values table must exist before the first query reads a CUSTOM column.</summary>
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var workspace in await WorkspacesAsync())
        {
            using var _ = Services.Workspaces.WorkspaceContext.Use(workspace);
            await EnsureTableAsync();
        }
        await base.StartAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<Services.Workspaces.Workspace>> WorkspacesAsync()
    {
        if (_workspaces is null)
            return [Services.Workspaces.WorkspaceContext.Current];
        try
        {
            return await _workspaces.ReloadAsync();
        }
        catch (Exception e) when (e is System.Data.Common.DbException or InvalidOperationException)
        {
            return _workspaces.All;
        }
    }

    /// <summary>The current workspace's last run.</summary>
    public CustomValueStatus Status => CurrentStatus with { Pending = _pending.GetValueOrDefault(Ws) || CurrentStatus.Pending };

    /// <summary>Asks for a run in the current workspace.</summary>
    public void Request()
    {
        // Every write and model change asks for this: tables made from saved queries see it too (Step 5.8).
        _versions?.Bump();
        _pending[Ws] = true;
        _requests.Writer.TryWrite(true);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Request();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var sweep = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                sweep.CancelAfter(Sweep);
                try
                {
                    await _requests.Reader.ReadAsync(sweep.Token);
                    await Task.Delay(Debounce, stoppingToken);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    // The minute passed without a request: sweep anyway.
                }
                // The workspaces asked for, or all of them on the minute's sweep.
                var all = await WorkspacesAsync();
                var asked = all.Where(w => _pending.GetValueOrDefault(w.Id)).ToList();
                foreach (var workspace in asked.Count > 0 ? asked : all)
                {
                    using var _ = Services.Workspaces.WorkspaceContext.Use(workspace);
                    await RunAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                _logger?.LogError(e, "Recalculating stored custom function values failed.");
            }
        }
    }

    public async Task<bool> RecalculateNowAsync(Db.Schema.TableDef table)
    {
        if (!_computed.For(table).Values.Any(c => c.Custom is not null && c.Error is null))
        {
            Request();
            return false;
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await RunAsync(timeout.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            Request();
            return false;
        }
    }

    /// <summary>One recalculation of every CUSTOM column (also called directly by tests).</summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        await _running.WaitAsync(cancellationToken);
        try
        {
            _pending[Ws] = false;
            CurrentStatus = CurrentStatus with { Pending = true };
            await EnsureTableAsync();

            var updated = 0;
            var failures = new List<CustomValueFailure>();
            var columns = CustomColumns();
            using var conn = await _dataPool.GetConnectionAsync();
            await RemoveOrphansAsync(conn, columns);

            // Columns may build on each other (b = CUSTOM("f", a) with a CUSTOM too): repeat while values change.
            for (var pass = 0; pass < MaxPasses; pass++)
            {
                failures.Clear();
                var changed = 0;
                foreach (var (table, column) in columns)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var (count, failure) = await RecalculateAsync(conn, table, column);
                    changed += count;
                    if (failure is not null)
                        failures.Add(failure);
                }
                updated += changed;
                if (changed == 0)
                    break;
            }

            CurrentStatus = new CustomValueStatus(false, DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture), updated, failures);
            if (updated > 0)
                _logger?.LogInformation("Stored custom function values updated: {Count}.", updated);
        }
        finally
        {
            if (CurrentStatus.Pending)
                CurrentStatus = CurrentStatus with { Pending = false };
            _running.Release();
        }
    }

    private List<(Db.Schema.TableDef Table, ComputedColumn Column)> CustomColumns() =>
        _schemaCache.Current.Tables
            .SelectMany(t => _computed.For(t).Values
                .Where(c => c.Custom is not null && c.Error is null)
                .Select(c => (t, c)))
            .ToList();

    private bool Pg => _dataPool.Provider == DatabaseProvider.Postgres;

    private static string Values => ComputedColumns.Quote(FormulaCompiler.StoredValuesTable);

    /// <summary>Runs the function where a row's inputs or the function changed since its value was stored.</summary>
    private async Task<(int Changed, CustomValueFailure? Failure)> RecalculateAsync(
        System.Data.IDbConnection conn, Db.Schema.TableDef table, ComputedColumn column)
    {
        var custom = column.Custom!;
        if (custom.OverRows)
            return await RecalculateRowsAsync(conn, table, column);
        var function = custom.Function;
        var key = table.Columns.First(c => c.PrimaryKey);
        var source = ComputedColumns.Quote(table.Name);
        var keySql = $"CAST({source}.{ComputedColumns.Quote(key.Name)} AS TEXT)";
        var arguments = string.Concat(custom.Arguments.Select((a, i) => $", {a.Sql} AS {ComputedColumns.Quote($"__a{i}")}"));

        var rows = (await conn.QueryAsync(
            $"""
            SELECT {keySql} AS "__key"{arguments}, v.inputs AS "__inputs", v.function_version AS "__version"
            FROM {source}
            LEFT JOIN {Values} v ON v.table_name = @Table AND v.column_name = @Column AND v.row_key = {keySql}
            """,
            new { Table = table.Name, Column = column.Column.Name }))
            .Cast<IDictionary<string, object?>>()
            .ToList();

        var work = new List<(string Key, List<object?> Values, string Inputs)>();
        foreach (var row in rows)
        {
            var values = custom.Arguments.Select((a, i) => ResultValues.Shape(row[$"__a{i}"], a.Type)).ToList();
            var inputs = JsonSerializer.Serialize(values);
            var version = row["__version"] is { } v and not DBNull ? System.Convert.ToInt32(v, CultureInfo.InvariantCulture) : (int?)null;
            if (version == function.Version && Equals(row["__inputs"] as string, inputs))
                continue;
            work.Add((System.Convert.ToString(row["__key"], CultureInfo.InvariantCulture)!, values, inputs));
        }
        if (work.Count == 0)
            return (0, await StoredFailureAsync(conn, table, column));

        var failed = 0;
        string? firstError = null;
        var (instance, syntax) = JsFunction.Create(function);
        using var transaction = conn.BeginTransaction();
        try
        {
            var now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            foreach (var (rowKey, values, inputs) in work)
            {
                var result = instance?.Call(values) ?? new JsCallResult(null, syntax, [], 0);
                if (!result.Ok)
                {
                    failed++;
                    firstError ??= result.Error;
                }
                await conn.ExecuteAsync(
                    $"""
                    INSERT INTO {Values} (table_name, column_name, row_key, value, inputs, function_version, error, computed_at)
                    VALUES (@Table, @Column, @Key, @Value, @Inputs, @Version, @Error, @Now)
                    ON CONFLICT (table_name, column_name, row_key) DO UPDATE SET
                        value = excluded.value, inputs = excluded.inputs, function_version = excluded.function_version,
                        error = excluded.error, computed_at = excluded.computed_at
                    """,
                    new
                    {
                        Table = table.Name,
                        Column = column.Column.Name,
                        Key = rowKey,
                        Value = result.Ok ? CustomFunctionValues.StorageText(result.Value, function.Result, Pg) : null,
                        Inputs = inputs,
                        function.Version,
                        result.Error,
                        Now = now
                    },
                    transaction);
            }
            transaction.Commit();
        }
        finally
        {
            instance?.Dispose();
        }

        return (work.Count, await StoredFailureAsync(conn, table, column));
    }

    /// <summary>
    /// CUSTOM_ROWS: a group is computed again as a whole when any of its rows,
    /// their order or the function changed — each row's value may depend on the rows before it.
    /// </summary>
    private async Task<(int Changed, CustomValueFailure? Failure)> RecalculateRowsAsync(
        System.Data.IDbConnection conn, Db.Schema.TableDef table, ComputedColumn column)
    {
        var custom = column.Custom!;
        var function = custom.Function;
        var key = ComputedColumns.Quote(table.Columns.First(c => c.PrimaryKey).Name);
        var source = ComputedColumns.Quote(table.Name);
        var sql = CustomRows.SelectSql(custom, source, key,
            extraSelect: "v.inputs AS \"__inputs\", v.function_version AS \"__version\"",
            join: $"LEFT JOIN {Values} v ON v.table_name = @Table AND v.column_name = @Column AND v.row_key = CAST({source}.{key} AS TEXT)");
        var rows = (await conn.QueryAsync(sql, new { Table = table.Name, Column = column.Column.Name }))
            .Cast<IDictionary<string, object?>>()
            .ToList();
        var stored = rows.ToDictionary(
            r => System.Convert.ToString(r["__key"], CultureInfo.InvariantCulture)!,
            r => (Inputs: r["__inputs"] as string, Version: r["__version"] is { } v and not DBNull ? System.Convert.ToInt32(v, CultureInfo.InvariantCulture) : (int?)null));

        var work = CustomRows.Groups(custom, rows)
            .Where(g => g.Keys.Any(k => stored[k].Version != function.Version || stored[k].Inputs != g.Inputs))
            .ToList();
        if (work.Count == 0)
            return (0, await StoredFailureAsync(conn, table, column));

        var changed = 0;
        var (instance, syntax) = JsFunction.Create(function);
        using var transaction = conn.BeginTransaction();
        try
        {
            var now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            foreach (var group in work)
            {
                foreach (var result in CustomRows.Run(instance, syntax, custom, group))
                {
                    changed++;
                    await conn.ExecuteAsync(
                        $"""
                        INSERT INTO {Values} (table_name, column_name, row_key, value, inputs, function_version, error, computed_at)
                        VALUES (@Table, @Column, @Key, @Value, @Inputs, @Version, @Error, @Now)
                        ON CONFLICT (table_name, column_name, row_key) DO UPDATE SET
                            value = excluded.value, inputs = excluded.inputs, function_version = excluded.function_version,
                            error = excluded.error, computed_at = excluded.computed_at
                        """,
                        new
                        {
                            Table = table.Name,
                            Column = column.Column.Name,
                            result.Key,
                            Value = result.Error is null ? CustomFunctionValues.StorageText(result.Value, function.Result, Pg) : null,
                            result.Inputs,
                            function.Version,
                            result.Error,
                            Now = now
                        },
                        transaction);
                }
            }
            transaction.Commit();
        }
        finally
        {
            instance?.Dispose();
        }
        return (changed, await StoredFailureAsync(conn, table, column));
    }

    /// <summary>The rows whose stored value is an error, as one failure per column.</summary>
    private static async Task<CustomValueFailure?> StoredFailureAsync(System.Data.IDbConnection conn, Db.Schema.TableDef table, ComputedColumn column)
    {
        var errors = (await conn.QueryAsync<string>(
            $"SELECT error FROM {Values} WHERE table_name = @Table AND column_name = @Column AND error IS NOT NULL",
            new { Table = table.Name, Column = column.Column.Name })).ToList();
        return errors.Count == 0
            ? null
            : new CustomValueFailure(table.Name, column.Column.Name, column.Custom!.Function.Name, errors.Count, errors[0]);
    }

    /// <summary>Values of deleted rows, and of columns that no longer call a function.</summary>
    private static async Task RemoveOrphansAsync(System.Data.IDbConnection conn, List<(Db.Schema.TableDef Table, ComputedColumn Column)> columns)
    {
        foreach (var (table, column) in columns)
        {
            var key = table.Columns.First(c => c.PrimaryKey);
            var source = ComputedColumns.Quote(table.Name);
            await conn.ExecuteAsync(
                $"""
                DELETE FROM {Values}
                WHERE table_name = @Table AND column_name = @Column
                  AND row_key NOT IN (SELECT CAST({source}.{ComputedColumns.Quote(key.Name)} AS TEXT) FROM {source})
                """,
                new { Table = table.Name, Column = column.Column.Name });
        }

        var kept = columns.Select(c => $"{c.Table.Name}\u0001{c.Column.Column.Name}").ToHashSet(StringComparer.Ordinal);
        var stored = await conn.QueryAsync<(string Table, string Column)>($"SELECT DISTINCT table_name, column_name FROM {Values}");
        foreach (var (table, column) in stored.Where(s => !kept.Contains($"{s.Table}\u0001{s.Column}")))
            await conn.ExecuteAsync($"DELETE FROM {Values} WHERE table_name = @Table AND column_name = @Column", new { Table = table, Column = column });
    }

    /// <summary>
    /// Creates the values table in the data database: on Postgres as the
    /// system role (the data role only reads and writes rows), on SQLite in data.db.
    /// </summary>
    public async Task EnsureTableAsync()
    {
        if (_tableReady.GetValueOrDefault(Ws))
            return;
        var ddl = $"""
            CREATE TABLE IF NOT EXISTS {(Pg ? "\"" + Services.Workspaces.WorkspaceContext.Current.DataSchema + "\"." : "")}{Values} (
                table_name       TEXT NOT NULL,
                column_name      TEXT NOT NULL,
                row_key          TEXT NOT NULL,
                value            TEXT,
                inputs           TEXT,
                function_version INTEGER,
                error            TEXT,
                computed_at      TEXT,
                PRIMARY KEY (table_name, column_name, row_key)
            );
            """;
        if (Pg)
        {
            using var conn = await _systemPool.GetConnectionAsync();
            await conn.ExecuteAsync(ddl);
        }
        else
        {
            using var conn = await _dataPool.GetConnectionAsync();
            await conn.ExecuteAsync(ddl);
        }
        _tableReady[Ws] = true;
    }
}
