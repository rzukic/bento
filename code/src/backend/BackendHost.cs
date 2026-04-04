using Repere.Api.Db;
using Repere.Api.Db.Migrations;
using Repere.Api.Db.Schema;
using Repere.Api.Middleware;
using Repere.Api.Services;

namespace Repere.Api;

public static class BackendHost
{
    public static void ConfigureConfiguration(WebApplicationBuilder builder)
    {
        _ = builder;

        // The default ASP.NET Core builder already loads appsettings.json plus
        // appsettings.{Environment}.json. This hook keeps backend-specific config
        // setup in one place for the later Step 1.3 configuration tasks.
    }

    public static void ConfigureServices(
        IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        _ = environment;

        services.AddControllers()
            .AddApplicationPart(typeof(Program).Assembly);

        var dbConfig = new DatabaseConfig();
        configuration.GetSection("Database").Bind(dbConfig);
        services.AddSingleton(dbConfig);

        var schemaOptions = new SchemaRuntimeOptions();
        configuration.GetSection("Schema").Bind(schemaOptions);
        services.AddSingleton(schemaOptions);

        services.AddLogging(logging =>
        {
            logging.AddConfiguration(configuration.GetSection("Logging"));
            logging.AddJsonConsole(options =>
            {
                options.IncludeScopes = true;
                options.TimestampFormat = "O";
            });
        });

        services.AddSingleton<ISystemPool>(serviceProvider =>
            new SystemPool(
                dbConfig,
                serviceProvider.GetRequiredService<ILogger<SystemPool>>()));

        services.AddSingleton<IDataPool>(serviceProvider =>
            new DataPool(
                dbConfig,
                serviceProvider.GetRequiredService<ILogger<DataPool>>()));

        services.AddSingleton<QueryService>();
        services.AddSingleton<ModelJsonStore>();
        services.AddSingleton<SchemaValidator>();
        services.AddSingleton<RelationResolver>();
        services.AddSingleton<SchemaDiffEngine>();
        services.AddSingleton<MigrationGenerator>();
        services.AddSingleton<MigrationRunner>();
        services.AddSingleton<SchemaService>();
        services.AddSingleton<AdminHealthService>();

        services.AddHostedService<DatabaseBootstrap>();
        services.AddHostedService<SchemaAutoMigrationService>();
    }

    public static void ConfigureMiddleware(WebApplication app)
    {
        app.UseMiddleware<RequestIdMiddleware>();
        app.UseMiddleware<RequestLoggingMiddleware>();
    }

    public static void ConfigureRoutes(WebApplication app)
    {
        app.MapControllers();

        // Reserve the versioned route areas that later Step 1.3/1.5 work fills in.
        ConfigureAuthRoutes(app);
        ConfigureAdminRoutes(app);
        ConfigureDataRoutes(app);
        ConfigureQueryRoutes(app);
    }

    private static void ConfigureAuthRoutes(IEndpointRouteBuilder app)
    {
        _ = app.MapGroup("/" + ApiRoutes.Auth)
            .WithTags("Auth");
    }

    private static void ConfigureAdminRoutes(IEndpointRouteBuilder app)
    {
        _ = app.MapGroup("/" + ApiRoutes.Admin)
            .WithTags("Admin");
    }

    private static void ConfigureDataRoutes(IEndpointRouteBuilder app)
    {
        _ = app.MapGroup("/" + ApiRoutes.Data)
            .WithTags("Data");
    }

    private static void ConfigureQueryRoutes(IEndpointRouteBuilder app)
    {
        _ = app.MapGroup("/" + ApiRoutes.Query)
            .WithTags("Query");
    }
}
