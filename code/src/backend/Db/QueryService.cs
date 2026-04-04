using System.Data;
using Dapper;
using SqlKata;
using SqlKata.Compilers;
using SqlKata.Execution;

namespace Repere.Api.Db;

/// <summary>
/// Provides scoped QueryFactory instances backed by the correct pool and dialect.
/// Consumers call UseSystem/UseData to get a factory, use it, then dispose.
/// </summary>
public class QueryService
{
    private readonly ISystemPool _systemPool;
    private readonly IDataPool _dataPool;
    private readonly Compiler _compiler;

    public QueryService(ISystemPool systemPool, IDataPool dataPool, DatabaseConfig config)
    {
        _systemPool = systemPool;
        _dataPool = dataPool;
        _compiler = config.Provider switch
        {
            DatabaseProvider.Postgres => new PostgresCompiler(),
            DatabaseProvider.Sqlite => new SqliteCompiler(),
            _ => throw new InvalidOperationException($"Unsupported provider: {config.Provider}")
        };
    }

    /// <summary>
    /// Get a QueryFactory connected to the system pool (migrations, auth, config).
    /// Caller must dispose the returned scope.
    /// </summary>
    public async Task<QueryScope> UseSystemAsync()
    {
        var conn = await _systemPool.GetConnectionAsync();
        return new QueryScope(conn, _compiler);
    }

    /// <summary>
    /// Get a QueryFactory connected to the data pool (user queries, CRUD).
    /// Caller must dispose the returned scope.
    /// </summary>
    public async Task<QueryScope> UseDataAsync()
    {
        var conn = await _dataPool.GetConnectionAsync();
        return new QueryScope(conn, _compiler);
    }

    /// <summary>
    /// Execute raw parameterized SQL against the data pool.
    /// For custom SQL endpoints and escape-hatch queries.
    /// </summary>
    public async Task<IEnumerable<T>> RawQueryAsync<T>(string sql, object? parameters = null)
    {
        using var conn = await _dataPool.GetConnectionAsync();
        return await conn.QueryAsync<T>(sql, parameters);
    }

    /// <summary>
    /// Execute raw parameterized SQL against the data pool (non-query).
    /// </summary>
    public async Task<int> RawExecuteAsync(string sql, object? parameters = null)
    {
        using var conn = await _dataPool.GetConnectionAsync();
        return await conn.ExecuteAsync(sql, parameters);
    }

    /// <summary>
    /// Execute raw parameterized SQL against the system pool.
    /// </summary>
    public async Task<IEnumerable<T>> RawSystemQueryAsync<T>(string sql, object? parameters = null)
    {
        using var conn = await _systemPool.GetConnectionAsync();
        return await conn.QueryAsync<T>(sql, parameters);
    }

    /// <summary>
    /// Execute raw parameterized SQL against the system pool (non-query).
    /// </summary>
    public async Task<int> RawSystemExecuteAsync(string sql, object? parameters = null)
    {
        using var conn = await _systemPool.GetConnectionAsync();
        return await conn.ExecuteAsync(sql, parameters);
    }

}

/// <summary>
/// A scoped connection + QueryFactory. Dispose when done to return the connection.
/// </summary>
public sealed class QueryScope : IDisposable, IAsyncDisposable
{
    private readonly IDbConnection _connection;

    public QueryFactory Db { get; }

    public QueryScope(IDbConnection connection, Compiler compiler)
    {
        _connection = connection;
        Db = new QueryFactory(connection, compiler);
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        _connection.Dispose();
        return ValueTask.CompletedTask;
    }
}
