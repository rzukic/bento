using Repere.Api.Db;

var builder = WebApplication.CreateBuilder(args);

// Support loading additional config files via ASPNETCORE_ENVIRONMENT
// e.g. ASPNETCORE_ENVIRONMENT=Postgres loads appsettings.Postgres.json
var env = builder.Environment.EnvironmentName;
builder.Configuration.AddJsonFile($"appsettings.{env}.json", optional: true, reloadOnChange: false);

// Database configuration
var dbConfig = new DatabaseConfig();
builder.Configuration.GetSection("Database").Bind(dbConfig);
builder.Services.AddSingleton(dbConfig);

// Dual connection pools
var systemPool = new SystemPool(dbConfig);
var dataPool = new DataPool(dbConfig);
builder.Services.AddSingleton<ISystemPool>(systemPool);
builder.Services.AddSingleton<IDataPool>(dataPool);

// Query builder service
builder.Services.AddSingleton<QueryService>();

// Bootstrap service — creates schemas and system tables on first run
builder.Services.AddHostedService<DatabaseBootstrap>();

var app = builder.Build();

app.MapGet("/health", async (ISystemPool sys, IDataPool data) =>
{
    try
    {
        using var sysConn = await sys.GetConnectionAsync();
        using var dataConn = await data.GetConnectionAsync();
        return Results.Ok(new
        {
            status = "healthy",
            database = dbConfig.Provider.ToString().ToLower(),
            systemPool = sysConn.State.ToString().ToLower(),
            dataPool = dataConn.State.ToString().ToLower()
        });
    }
    catch (Exception ex)
    {
        return Results.Json(new
        {
            status = "unhealthy",
            error = ex.Message
        }, statusCode: 503);
    }
});

app.Run();
