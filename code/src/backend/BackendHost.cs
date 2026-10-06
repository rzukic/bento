using Bento.Api.Configuration;
using Bento.Api.Db;
using Bento.Api.Db.Migrations;
using Bento.Api.Db.Schema;
using Bento.Api.Middleware;
using Bento.Api.Services;
using Bento.Api.Validation;

namespace Bento.Api;

public static class BackendHost
{
    public static IReadOnlyList<ReservedRouteArea> ReservedRouteAreas { get; } =
    [
        new(ApiRoutes.Auth, "Auth"),
        new(ApiRoutes.Schema, "Schema"),
        new(ApiRoutes.Admin, "Admin"),
        new(ApiRoutes.Data, "Data"),
        new(ApiRoutes.Query, "Query")
    ];

    public static void ConfigureConfiguration(WebApplicationBuilder builder)
    {
        // The default ASP.NET Core builder already loads appsettings.json plus
        // appsettings.{Environment}.json. This hook keeps backend-specific config
        // setup in one place for the later Step 1.3 configuration tasks.
        var overrides = BackendRuntimeConfiguration.BuildEnvironmentOverrides(Environment.GetEnvironmentVariable);
        if (overrides.Count > 0)
        {
            builder.Configuration.AddInMemoryCollection(overrides);
        }
    }

    public static void ConfigureServices(
        IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        _ = environment;

        if (configuration is ConfigurationManager configurationManager)
        {
            using var bootstrapLoggerFactory = LoggerFactory.Create(logging =>
            {
                logging.AddConfiguration(configuration.GetSection("Logging"));
                logging.AddJsonConsole(options =>
                {
                    options.IncludeScopes = true;
                    options.TimestampFormat = "O";
                });
            });
            var bootstrapLogger = bootstrapLoggerFactory.CreateLogger("Bento.Api.Configuration.PersistentRuntimeConfigBootstrap");
            var persistentOverrides = PersistentRuntimeConfigBootstrap.BuildOverrides(configurationManager, bootstrapLogger);
            if (persistentOverrides.Count > 0)
            {
                configurationManager.AddInMemoryCollection(persistentOverrides);
            }
        }

        services.AddControllers()
            .AddApplicationPart(typeof(Program).Assembly)
            .ConfigureApiBehaviorOptions(options =>
            {
                options.InvalidModelStateResponseFactory = ApiValidationResponseFactory.CreateActionResult;
            });

        var runtimeOptions = BackendRuntimeConfiguration.BindAndValidateRuntimeOptions(configuration, environment);
        services.AddSingleton(runtimeOptions);

        var dbConfig = new DatabaseConfig();
        configuration.GetSection("Database").Bind(dbConfig);
        BackendRuntimeConfiguration.ApplyDatabaseOverrides(dbConfig, runtimeOptions);
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
        DapperTypeHandlers.Register();
        services.AddSingleton<AuthService>();
        services.AddSingleton<AuthRateLimiter>();
        services.AddSingleton<ModelJsonStore>();
        services.AddSingleton<SchemaValidator>();
        services.AddSingleton<RelationResolver>();
        services.AddSingleton<SchemaDiffEngine>();
        services.AddSingleton<MigrationGenerator>();
        services.AddSingleton<MigrationRunner>();
        services.AddSingleton<SchemaService>();
        services.AddSingleton(BackendRuntimeInfo.CreateForCurrentProcess());
        services.AddSingleton<AdminHealthService>();
        services.AddSingleton<RequestBodyValidator>();
        services.AddSingleton<CrudService>();
        services.AddSingleton<IncludeService>();
        services.AddSingleton<Bento.Api.Services.Import.ImportService>();
        services.AddSingleton<AggregationQueryService>();
        services.AddSingleton<FormulaService>();
        services.AddSingleton<Bento.Api.Formula.ComputedColumns>();
        services.AddSingleton<CustomFunctionCatalog>();
        services.AddSingleton<Bento.Api.Formula.ICustomFunctionCatalog>(sp => sp.GetRequiredService<CustomFunctionCatalog>());
        services.AddSingleton<CustomFunctionService>();
        services.AddSingleton<CredentialService>();
        services.AddSingleton<PriceSourceService>();
        services.AddSingleton(sp => new Bento.Api.Formula.Sandbox.JsNetwork(new Bento.Api.Formula.Sandbox.JsNetworkOptions
        {
            AllowPrivateNetwork = sp.GetRequiredService<IConfiguration>().GetValue("Functions:AllowPrivateNetwork", false)
        }));
        services.AddSingleton<Bento.Api.Workers.StoredCustomValues>();
        services.AddSingleton<ICustomValueRecalculation>(sp => sp.GetRequiredService<Bento.Api.Workers.StoredCustomValues>());
        services.AddSingleton<ApiKeyService>();
        services.AddSingleton<OpenApiDocumentBuilder>();
        services.AddSingleton<SchemaCache>();

        services.AddHostedService<DatabaseBootstrap>();
        services.AddHostedService<CustomFunctionCatalogInitializer>();
        services.AddHostedService<SchemaAutoMigrationService>();
        services.AddHostedService<SchemaCacheInitializer>();
        services.AddHostedService(sp => sp.GetRequiredService<Bento.Api.Workers.StoredCustomValues>());
        services.AddHostedService<Bento.Api.Workers.PriceSourceScheduler>();
    }

    public static void ConfigureMiddleware(WebApplication app)
    {
        // --- Proxy awareness (TASK-146) ---------------------------------------------
        // Forwarded headers are honoured only for the explicit TRUSTED_PROXIES list; with
        // an empty list no X-Forwarded-* header is trusted. The cookie policy upgrades the
        // refresh cookie to Secure whenever the (possibly forwarded) request is HTTPS,
        // on top of the environment-aware default decided at configuration time.
        var runtimeOptions = app.Services.GetRequiredService<BackendRuntimeOptions>();
        if (runtimeOptions.HasTrustedProxies)
        {
            app.UseForwardedHeaders(TrustedProxyConfiguration.CreateForwardedHeadersOptions(runtimeOptions.TrustedProxies));
        }

        app.UseCookiePolicy(new CookiePolicyOptions
        {
            Secure = CookieSecurePolicy.SameAsRequest
        });
        // -----------------------------------------------------------------------------

        app.UseMiddleware<RequestIdMiddleware>();
        app.UseMiddleware<RequestLoggingMiddleware>();
        app.UseMiddleware<ErrorHandlingMiddleware>();
        app.UseMiddleware<CorsPolicyMiddleware>();
        app.UseMiddleware<BodySizeLimitMiddleware>();
        app.UseMiddleware<AuthRateLimitMiddleware>();
        app.UseMiddleware<AuthMiddleware>();
    }

    public static void ConfigureRoutes(WebApplication app)
    {
        app.MapControllers();

        // Reserve the versioned route areas that later Step 1.4/1.5 work fills in.
        foreach (var area in ReservedRouteAreas)
        {
            _ = app.MapGroup("/" + area.RoutePrefix)
                .WithTags(area.Tag);
        }
    }
}
