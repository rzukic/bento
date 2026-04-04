using Repere.Api.Db;
using Repere.Api.Db.Migrations;
using Repere.Api.Db.Schema;

var builder = WebApplication.CreateBuilder(args);

// Support loading additional config files via ASPNETCORE_ENVIRONMENT
// e.g. ASPNETCORE_ENVIRONMENT=Postgres loads appsettings.Postgres.json
var env = builder.Environment.EnvironmentName;
builder.Configuration.AddJsonFile($"appsettings.{env}.json", optional: true, reloadOnChange: false);

// Database configuration
var dbConfig = new DatabaseConfig();
builder.Configuration.GetSection("Database").Bind(dbConfig);
builder.Services.AddSingleton(dbConfig);

var schemaOptions = new SchemaRuntimeOptions();
builder.Configuration.GetSection("Schema").Bind(schemaOptions);
builder.Services.AddSingleton(schemaOptions);

// Dual connection pools
var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
var systemPool = new SystemPool(dbConfig, loggerFactory.CreateLogger<SystemPool>());
var dataPool = new DataPool(dbConfig, loggerFactory.CreateLogger<DataPool>());
builder.Services.AddSingleton<ISystemPool>(systemPool);
builder.Services.AddSingleton<IDataPool>(dataPool);

// Query builder service
builder.Services.AddSingleton<QueryService>();
builder.Services.AddSingleton<ModelJsonStore>();
builder.Services.AddSingleton<SchemaValidator>();
builder.Services.AddSingleton<RelationResolver>();
builder.Services.AddSingleton<SchemaDiffEngine>();
builder.Services.AddSingleton<MigrationGenerator>();
builder.Services.AddSingleton<MigrationRunner>();
builder.Services.AddSingleton<SchemaService>();

// Bootstrap service — creates schemas and system tables on first run
builder.Services.AddHostedService<DatabaseBootstrap>();
builder.Services.AddHostedService<SchemaAutoMigrationService>();

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
