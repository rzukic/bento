using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using Bento.Api.Db;
using Bento.Api.Db.Schema;
using Bento.Api.Services;
using SqlKata;

namespace Bento.Api.Formula;

/// <summary>A computed column compiled for queries on its table (alias = the quoted table name).</summary>
/// <param name="Error">Set when the stored formula does not check; the value is then empty.</param>
public sealed record ComputedColumn(ColumnDef Column, string Sql, FormulaType Type, string? Error)
{
    /// <summary>The column type the value behaves like (formatting, filters, aggregation).</summary>
    public ColumnType ValueType => FormulaTypes.ToColumnType(Type);
}

/// <summary>
/// Computed columns (Step 3.5) as SQL for every query that reads a table:
/// the list, a single row, related rows, filters, sorts and aggregation
/// queries. Always live — nothing is stored. Compiled once per schema version.
/// </summary>
public sealed class ComputedColumns
{
    private static readonly IReadOnlyDictionary<string, ComputedColumn> None =
        new Dictionary<string, ComputedColumn>(StringComparer.OrdinalIgnoreCase);

    private readonly SchemaCache _schemaCache;
    private readonly DatabaseConfig _dbConfig;
    private readonly ILogger<ComputedColumns>? _logger;
    private readonly ConditionalWeakTable<ModelJson, Compiled> _compiled = new();

    private sealed class Compiled(ModelJson model)
    {
        public FormulaSchema Schema { get; } = FormulaSchema.From(model);
        public ConcurrentDictionary<string, IReadOnlyDictionary<string, ComputedColumn>> Tables { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public ComputedColumns(SchemaCache schemaCache, DatabaseConfig dbConfig, ILogger<ComputedColumns>? logger = null)
    {
        _schemaCache = schemaCache;
        _dbConfig = dbConfig;
        _logger = logger;
    }

    public DatabaseProvider Provider => _dbConfig.Provider;

    /// <summary>The table's computed columns by name; empty when it has none.</summary>
    public IReadOnlyDictionary<string, ComputedColumn> For(TableDef table)
    {
        if (!table.Columns.Any(c => c.Type == ColumnType.Computed))
            return None;

        var model = _schemaCache.Current;
        var compiled = _compiled.GetValue(model, m => new Compiled(m));
        var current = compiled.Schema.Table(table.Name);
        if (current is null || !ReferenceEquals(current, table))
            return CompileTable(table, FormulaSchema.WithTable(model, table));
        return compiled.Tables.GetOrAdd(table.Name, _ => CompileTable(table, compiled.Schema));
    }

    private IReadOnlyDictionary<string, ComputedColumn> CompileTable(TableDef table, FormulaSchema schema)
    {
        var result = new Dictionary<string, ComputedColumn>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in table.Columns.Where(c => c.Type == ColumnType.Computed))
        {
            var declared = FormulaTypes.FromName(column.ResultType);
            var check = FormulaCompiler.Compile(column.Formula ?? "", table, schema, _dbConfig.Provider,
                alias: Quote(table.Name), expected: declared, column: column.Name);
            if (check.IsValid)
            {
                result[column.Name] = new ComputedColumn(column, check.Result!.Sql, check.Result.Type, null);
                continue;
            }

            var error = check.Errors.FirstOrDefault()?.Message ?? "The formula does not check.";
            _logger?.LogWarning("Computed column {Table}.{Column} is empty: {Error}", table.Name, column.Name, error);
            result[column.Name] = new ComputedColumn(column, "NULL", declared ?? FormulaType.Text, error);
        }
        return result;
    }

    // ── Query helpers ────────────────────────────────────────────────────

    /// <summary>
    /// Selects the table's columns and its computed ones. With <paramref name="fields"/>,
    /// only those (stored ones by name, computed ones by formula).
    /// </summary>
    public Query Select(Query query, TableDef table, IReadOnlyCollection<string>? fields = null)
    {
        var computed = For(table);
        if (fields is null)
        {
            query.Select($"{table.Name}.*");
            foreach (var column in computed.Values)
                query.SelectRaw($"{column.Sql} AS {Quote(column.Column.Name)}");
            return query;
        }

        foreach (var field in fields)
        {
            if (computed.TryGetValue(field, out var column))
                query.SelectRaw($"{column.Sql} AS {Quote(column.Column.Name)}");
            else
                query.Select($"{table.Name}.{field}");
        }
        return query;
    }

    /// <summary>Computed values in the API's shape (dates, UTC date-times, numbers without noise).</summary>
    public void Shape(TableDef table, IDictionary<string, object?> row)
    {
        foreach (var column in For(table).Values)
        {
            var key = row.Keys.FirstOrDefault(k => string.Equals(k, column.Column.Name, StringComparison.OrdinalIgnoreCase));
            if (key is not null)
                row[key] = ResultValues.Shape(row[key], column.Type);
        }
    }

    /// <summary>
    /// A filter value for a computed column, typed like the values the formula
    /// gives: SQLite date-times as "yyyy-MM-dd HH:mm:ss", Postgres date-times
    /// as timestamps without zone (the formula's UTC wall time).
    /// </summary>
    public static object? FilterValue(string? value, ComputedColumn column, DatabaseProvider provider)
    {
        if (value is null)
            return null;
        var pg = provider == DatabaseProvider.Postgres;
        switch (column.Type)
        {
            case FormulaType.Integer when long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n):
                return n;
            case FormulaType.Number or FormulaType.Currency when decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d):
                return pg ? d : (double)d;
            case FormulaType.Boolean when value.Trim().ToLowerInvariant() is "true" or "1":
                return true;
            case FormulaType.Boolean when value.Trim().ToLowerInvariant() is "false" or "0":
                return false;
            case FormulaType.Date when DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date):
                var day = DateOnly.FromDateTime(date.UtcDateTime);
                return pg ? day : day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            case FormulaType.DateTime when DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var stamp):
                var utc = stamp.UtcDateTime;
                return pg
                    ? DateTime.SpecifyKind(utc, DateTimeKind.Unspecified)
                    : utc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            default:
                return value;
        }
    }

    /// <summary>The value for comparisons: numbers rounded to 10 places, like the formula engine.</summary>
    public static string ComparableSql(ComputedColumn column, DatabaseProvider provider) =>
        column.Type is FormulaType.Number or FormulaType.Currency
            ? provider == DatabaseProvider.Postgres ? $"ROUND(CAST({column.Sql} AS NUMERIC), 10)" : $"ROUND({column.Sql}, 10)"
            : column.Sql;

    public static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";
}
