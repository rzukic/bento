using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Bento.Api.Db;
using Bento.Api.Formula;
using Dapper;

namespace Bento.Api.Services;

/// <summary>
/// Indexes for the searches formulas make per row and period. Without them,
/// every search reads the whole table:
/// <list type="bullet">
/// <item><b>Related rows</b> (<c>SUM(trades.amount)</c> on accounts):
///   <c>bento_ref_trades_account_id</c> on the link column.</item>
/// <item><b>LOOKUP_ASOF</b> ("the latest price of this ISIN on or before the end of
///   this week"): <c>bento_lookup_prices_isin_date</c> on <c>(lower(isin), date)</c>,
///   the expression the formula compiles to. A weekly portfolio chart over three
///   years made thousands of these and ran past the 30 s limit.</item>
/// </list>
/// Each index is created before the first query that needs it, once per schema
/// version. SQLite gets the plain ones only: its lower-casing is Bento's own
/// function, which an index would make every other program writing the file
/// depend on.
/// </summary>
public sealed class QueryIndexes
{
    private readonly ISystemPool _systemPool;
    private readonly IDataPool _dataPool;
    private readonly SchemaCache _schemaCache;
    private readonly ILogger<QueryIndexes> _logger;
    private readonly ConcurrentDictionary<string, Lazy<Task>> _ensured = new(StringComparer.Ordinal);

    public QueryIndexes(ISystemPool systemPool, IDataPool dataPool, SchemaCache schemaCache, ILogger<QueryIndexes> logger)
    {
        _systemPool = systemPool;
        _dataPool = dataPool;
        _schemaCache = schemaCache;
        _logger = logger;
    }

    private bool Postgres => _systemPool.Provider == DatabaseProvider.Postgres;

    /// <summary>Creates the indexes the searches need; failures are logged, not thrown.</summary>
    public Task EnsureAsync(IEnumerable<IndexTarget> targets)
    {
        var version = _schemaCache.Current.Version;
        var pending = targets.Distinct()
            .Where(target => Postgres || !target.Lower)
            .Select(target => _ensured.GetOrAdd($"{version}|{IndexName(target)}", _ => new Lazy<Task>(() => CreateAsync(target))).Value)
            .ToList();
        return pending.Count == 0 ? Task.CompletedTask : Task.WhenAll(pending);
    }

    private async Task CreateAsync(IndexTarget target)
    {
        var name = IndexName(target);
        var columns = (target.Lower ? $"lower({Q(target.Column)})" : Q(target.Column)) + (target.Then is null ? "" : $", {Q(target.Then)}");
        try
        {
            // On Postgres the system role owns the tables (migrations run as it).
            using var connection = Postgres ? await _systemPool.GetConnectionAsync() : await _dataPool.GetConnectionAsync();
            var table = Postgres ? $"public.{Q(target.Table)}" : Q(target.Table);
            await connection.ExecuteAsync($"CREATE INDEX IF NOT EXISTS {Q(name)} ON {table} ({columns})", commandTimeout: 300);
            _logger.LogInformation("Index {Index} on {Table} ready.", name, target.Table);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Index {Index} on {Table} could not be created; searches on it stay slow.", name, target.Table);
        }
    }

    /// <summary>
    /// <c>bento_lookup_prices_isin_date</c>, <c>bento_ref_trades_security_id</c>;
    /// long names are cut and keep a hash (Postgres allows 63 bytes).
    /// </summary>
    public static string IndexName(IndexTarget target)
    {
        var name = (target.Then is null
            ? $"bento_ref_{target.Table}_{target.Column}"
            : $"bento_lookup_{target.Table}_{target.Column}_{target.Then}").ToLowerInvariant();
        if (Encoding.UTF8.GetByteCount(name) <= 63)
            return name;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..8].ToLowerInvariant();
        var cut = name;
        while (Encoding.UTF8.GetByteCount(cut) > 54)
            cut = cut[..^1];
        return $"{cut}_{hash}";
    }

    private static string Q(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";
}
