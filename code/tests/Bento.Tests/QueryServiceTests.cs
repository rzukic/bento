using Microsoft.Data.Sqlite;
using SqlKata;
using SqlKata.Compilers;
using SqlKata.Execution;
using Bento.Api.Db;

namespace Bento.Tests;

public class QueryServiceTests : IDisposable
{
    private readonly SqliteConnection _sysConn;
    private readonly SqliteConnection _dataConn;
    private readonly QueryService _queryService;

    public QueryServiceTests()
    {
        // In-memory SQLite databases for fast, isolated tests
        _sysConn = new SqliteConnection("Data Source=:memory:");
        _sysConn.Open();

        _dataConn = new SqliteConnection("Data Source=:memory:");
        _dataConn.Open();

        // Create a test table in the data connection
        using var cmd = _dataConn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE products (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL,
                price REAL NOT NULL,
                category TEXT,
                in_stock INTEGER NOT NULL DEFAULT 1
            );
            INSERT INTO products (name, price, category, in_stock) VALUES ('Widget', 9.99, 'tools', 1);
            INSERT INTO products (name, price, category, in_stock) VALUES ('Gadget', 24.99, 'electronics', 1);
            INSERT INTO products (name, price, category, in_stock) VALUES ('Doohickey', 4.50, 'tools', 0);
            INSERT INTO products (name, price, category, in_stock) VALUES ('Thingamajig', 15.00, 'electronics', 1);
            INSERT INTO products (name, price, category, in_stock) VALUES ('Whatchamacallit', 7.25, 'misc', 1);
            """;
        cmd.ExecuteNonQuery();

        // Set up pools using test helper
        var config = new DatabaseConfig { Provider = DatabaseProvider.Sqlite };
        var systemPool = new InMemoryPool(_sysConn, DatabaseProvider.Sqlite);
        var dataPool = new InMemoryPool(_dataConn, DatabaseProvider.Sqlite);
        _queryService = new QueryService(systemPool, dataPool, config);
    }

    [Fact]
    public async Task Select_ReturnsAllRows()
    {
        await using var scope = await _queryService.UseDataAsync();
        var results = await scope.Db.Query("products").GetAsync<dynamic>();
        Assert.Equal(5, results.Count());
    }

    [Fact]
    public async Task Select_WithWhere_FiltersRows()
    {
        await using var scope = await _queryService.UseDataAsync();
        var results = await scope.Db.Query("products")
            .Where("category", "tools")
            .GetAsync<dynamic>();
        Assert.Equal(2, results.Count());
    }

    [Fact]
    public async Task Select_WithOrderBy_SortsRows()
    {
        await using var scope = await _queryService.UseDataAsync();
        var results = (await scope.Db.Query("products")
            .OrderBy("price")
            .Select("name", "price")
            .GetAsync<dynamic>())
            .ToList();

        Assert.Equal("Doohickey", (string)results[0].name);
        Assert.Equal("Gadget", (string)results[4].name);
    }

    [Fact]
    public async Task Select_WithLimitOffset_PaginatesRows()
    {
        await using var scope = await _queryService.UseDataAsync();
        var results = (await scope.Db.Query("products")
            .OrderBy("id")
            .Limit(2)
            .Offset(1)
            .GetAsync<dynamic>())
            .ToList();

        Assert.Equal(2, results.Count);
        Assert.Equal("Gadget", (string)results[0].name);
        Assert.Equal("Doohickey", (string)results[1].name);
    }

    [Fact]
    public async Task Select_WithGroupByHaving_Aggregates()
    {
        await using var scope = await _queryService.UseDataAsync();
        var results = (await scope.Db.Query("products")
            .Select("category")
            .SelectRaw("COUNT(*) as cnt")
            .GroupBy("category")
            .HavingRaw("COUNT(*) > 1")
            .GetAsync<dynamic>())
            .ToList();

        Assert.Equal(2, results.Count); // tools=2, electronics=2
    }

    [Fact]
    public async Task Insert_AddsRow()
    {
        await using var scope = await _queryService.UseDataAsync();
        var id = await scope.Db.Query("products").InsertGetIdAsync<int>(new
        {
            name = "Gizmo",
            price = 12.99,
            category = "tools",
            in_stock = 1
        });

        Assert.True(id > 0);

        var count = await scope.Db.Query("products").CountAsync<int>();
        Assert.Equal(6, count);
    }

    [Fact]
    public async Task Update_ModifiesRow()
    {
        await using var scope = await _queryService.UseDataAsync();
        var affected = await scope.Db.Query("products")
            .Where("name", "Widget")
            .UpdateAsync(new { price = 11.99 });

        Assert.Equal(1, affected);

        var updated = (await scope.Db.Query("products")
            .Where("name", "Widget")
            .Select("price")
            .GetAsync<dynamic>())
            .First();
        Assert.Equal(11.99, (double)updated.price);
    }

    [Fact]
    public async Task Delete_RemovesRow()
    {
        await using var scope = await _queryService.UseDataAsync();
        var affected = await scope.Db.Query("products")
            .Where("name", "Doohickey")
            .DeleteAsync();

        Assert.Equal(1, affected);

        var count = await scope.Db.Query("products").CountAsync<int>();
        Assert.Equal(4, count);
    }

    [Fact]
    public async Task RawQuery_ExecutesParameterizedSql()
    {
        var results = (await _queryService.RawQueryAsync<dynamic>(
            "SELECT name FROM products WHERE price > @price ORDER BY price",
            new { price = 10.0 })).ToList();

        Assert.Equal(2, results.Count);
        Assert.Equal("Thingamajig", (string)results[0].name);
        Assert.Equal("Gadget", (string)results[1].name);
    }

    [Fact]
    public async Task RawExecute_RunsParameterizedCommand()
    {
        var affected = await _queryService.RawExecuteAsync(
            "UPDATE products SET in_stock = @stock WHERE category = @cat",
            new { stock = 0, cat = "electronics" });

        Assert.Equal(2, affected);
    }

    [Fact]
    public async Task Select_WithSubquery_FiltersUsingInnerQuery()
    {
        await using var scope = await _queryService.UseDataAsync();

        // Subquery: get products whose price is above the average
        var avgSubquery = new Query("products").SelectRaw("AVG(price)");
        var results = (await scope.Db.Query("products")
            .Where("price", ">", avgSubquery)
            .OrderBy("price")
            .GetAsync<dynamic>())
            .ToList();

        // Average is (9.99 + 24.99 + 4.50 + 15.00 + 7.25) / 5 = 12.346
        // Above average: Thingamajig (15.00), Gadget (24.99)
        Assert.Equal(2, results.Count);
        Assert.Equal("Thingamajig", (string)results[0].name);
        Assert.Equal("Gadget", (string)results[1].name);
    }

    [Fact]
    public void SqlGeneration_Subquery_WorksBothDialects()
    {
        var pgCompiler = new PostgresCompiler();
        var sqliteCompiler = new SqliteCompiler();

        var subquery = new Query("products").SelectRaw("AVG(price)");
        var query = new Query("products").Where("price", ">", subquery);

        var pgResult = pgCompiler.Compile(query);
        var sqliteResult = sqliteCompiler.Compile(query);

        Assert.Contains("SELECT", pgResult.Sql.ToUpper());
        Assert.Contains("AVG", pgResult.Sql.ToUpper());
        Assert.Contains("SELECT", sqliteResult.Sql.ToUpper());
        Assert.Contains("AVG", sqliteResult.Sql.ToUpper());
    }

    [Fact]
    public void SqlGeneration_Postgres_ProducesCorrectDialect()
    {
        var compiler = new PostgresCompiler();
        var query = new Query("products")
            .Where("category", "tools")
            .OrderBy("price")
            .Limit(10)
            .Offset(5);

        var result = compiler.Compile(query);
        Assert.Contains("\"products\"", result.Sql);
        Assert.Contains("LIMIT", result.Sql.ToUpper());
        Assert.Contains("OFFSET", result.Sql.ToUpper());
        Assert.Single(result.Bindings, b => b.ToString() == "tools");
    }

    [Fact]
    public void SqlGeneration_Sqlite_ProducesCorrectDialect()
    {
        var compiler = new SqliteCompiler();
        var query = new Query("products")
            .Where("category", "tools")
            .OrderBy("price")
            .Limit(10)
            .Offset(5);

        var result = compiler.Compile(query);
        Assert.Contains("\"products\"", result.Sql);
        Assert.Contains("LIMIT", result.Sql.ToUpper());
        Assert.Contains("OFFSET", result.Sql.ToUpper());
        Assert.Single(result.Bindings, b => b.ToString() == "tools");
    }

    [Fact]
    public void SqlGeneration_SameBuilderCode_WorksBothDialects()
    {
        // The same Query object compiles validly for both dialects
        var pgCompiler = new PostgresCompiler();
        var sqliteCompiler = new SqliteCompiler();

        var query = new Query("orders")
            .Join("customers", "customers.id", "orders.customer_id")
            .Where("orders.total", ">", 100)
            .GroupBy("customers.name")
            .SelectRaw("customers.name, SUM(orders.total) as revenue")
            .HavingRaw("SUM(orders.total) > 500")
            .OrderByDesc("revenue")
            .Limit(20);

        var pgResult = pgCompiler.Compile(query);
        var sqliteResult = sqliteCompiler.Compile(query);

        // Both should produce valid SQL with the same parameter bindings
        Assert.Equal(pgResult.Bindings.Count, sqliteResult.Bindings.Count);
        Assert.Contains("JOIN", pgResult.Sql.ToUpper());
        Assert.Contains("JOIN", sqliteResult.Sql.ToUpper());
        Assert.Contains("GROUP BY", pgResult.Sql.ToUpper());
        Assert.Contains("GROUP BY", sqliteResult.Sql.ToUpper());
    }

    public void Dispose()
    {
        _sysConn.Dispose();
        _dataConn.Dispose();
    }
}

/// <summary>
/// Test helper: wraps an existing in-memory connection as a pool.
/// </summary>
internal class InMemoryPool : ISystemPool, IDataPool
{
    private readonly SqliteConnection _conn;
    public DatabaseProvider Provider { get; }

    public InMemoryPool(SqliteConnection conn, DatabaseProvider provider)
    {
        _conn = conn;
        Provider = provider;
    }

    public Task<System.Data.IDbConnection> GetConnectionAsync()
    {
        // Return the shared in-memory connection (don't dispose it per-use)
        return Task.FromResult<System.Data.IDbConnection>(_conn);
    }
}
