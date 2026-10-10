using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Bento.Api.Db;
using Bento.Api.Formula;
using Dapper;

namespace Bento.Api.Services;

/// <summary>
/// Indexes for LOOKUP_ASOF on Postgres. Each lookup is a search per row and
/// period ("the latest price of this ISIN on or before the end of this week");
/// without an index every search reads the whole table. A weekly portfolio
/// chart over three years makes thousands of them and ran past the 30 s
/// limit. The index is created before the first query that needs it, on the
/// same expression the formula compiles to (<c>lower(isin), date</c>).
/// SQLite is left alone: its lower-casing is Bento's own function, which an
/// index would make every other program writing the file depend on.
/// </summary>
public sealed class LookupIndexes
{
    private readonly ISystemPool _systemPool;
    private readonly SchemaCache _schemaCache;
    private readonly ILogger<LookupIndexes> _logger;
    private readonly ConcurrentDictionary<string, Lazy<Task>> _ensured = new(StringComparer.Ordinal);

    public LookupIndexes(ISystemPool systemPool, SchemaCache schemaCache, ILogger<LookupIndexes> logger)
    {
        _systemPool = systemPool;
        _schemaCache = schemaCache;
        _logger = logger;
    }

    /// <summary>Creates the indexes the lookups need, once per schema version; failures are logged, not thrown.</summary>
    public Task EnsureAsync(IEnumerable<LookupTarget> lookups)
    {
        if (_systemPool.Provider != DatabaseProvider.Postgres)
            return Task.CompletedTask;
        var version = _schemaCache.Current.Version;
        var pending = lookups.Distinct()
            .Select(lookup => _ensured.GetOrAdd($"{version}|{IndexName(lookup)}", _ => new Lazy<Task>(() => CreateAsync(lookup))).Value)
            .ToList();
        return pending.Count == 0 ? Task.CompletedTask : Task.WhenAll(pending);
    }

    private async Task CreateAsync(LookupTarget lookup)
    {
        var name = IndexName(lookup);
        var match = lookup.Lower ? $"lower({Q(lookup.Match)})" : Q(lookup.Match);
        try
        {
            // The system role owns the tables (migrations run as it).
            using var connection = await _systemPool.GetConnectionAsync();
            await connection.ExecuteAsync(
                $"CREATE INDEX IF NOT EXISTS {Q(name)} ON public.{Q(lookup.Table)} ({match}, {Q(lookup.Date)})",
                commandTimeout: 300);
            _logger.LogInformation("Lookup index {Index} on {Table} ready.", name, lookup.Table);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Lookup index {Index} on {Table} could not be created; LOOKUP_ASOF on it stays slow.", name, lookup.Table);
        }
    }

    /// <summary><c>bento_lookup_prices_isin_date</c>; long names are cut and keep a hash (Postgres allows 63 bytes).</summary>
    public static string IndexName(LookupTarget lookup)
    {
        var name = $"bento_lookup_{lookup.Table}_{lookup.Match}_{lookup.Date}".ToLowerInvariant();
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
