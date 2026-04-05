using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Repere.Api;

namespace Repere.Tests;

public sealed class ApiIntegrationTestHost : IAsyncDisposable
{
    private readonly string _sqliteRootPath;
    private readonly WebApplication _app;

    private ApiIntegrationTestHost(string sqliteRootPath, WebApplication app, HttpClient client)
    {
        _sqliteRootPath = sqliteRootPath;
        _app = app;
        Client = client;
    }

    public HttpClient Client { get; }

    public static async Task<ApiIntegrationTestHost> StartAsync(
        IReadOnlyDictionary<string, string?>? overrides = null,
        string environmentName = "Development")
    {
        var sqliteRootPath = Path.Combine(Path.GetTempPath(), "repere-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sqliteRootPath);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(Program).Assembly.FullName,
            EnvironmentName = environmentName,
            ContentRootPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/backend"))
        });

        BackendHost.ConfigureConfiguration(builder);

        var configOverrides = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Backend:Port"] = "5000",
            ["Backend:LogLevel"] = "Information",
            ["Backend:MaxBodySize"] = "10mb",
            ["Backend:AuthMode"] = "standard",
            ["Backend:Jwt:Issuer"] = "repere-api",
            ["Backend:Jwt:Audience"] = "repere-clients",
            ["Backend:Jwt:AccessTokenLifetimeMinutes"] = "15",
            ["Backend:Jwt:RefreshTokenLifetimeDays"] = "7",
            ["Backend:AuthCookie:RefreshCookieName"] = "repere_refresh",
            ["Backend:AuthCookie:HttpOnly"] = "true",
            ["Backend:AuthCookie:RequireSecure"] = "true",
            ["Backend:AuthCookie:SameSite"] = "Strict",
            ["Backend:AuthCookie:Path"] = "/api/v1/auth",
            ["Backend:AuthRateLimitPerMinute"] = "5",
            ["Backend:DataDir"] = sqliteRootPath,
            ["Database:Provider"] = "Sqlite",
            ["Database:SystemConnection"] = sqliteRootPath,
            ["Database:DataConnection"] = sqliteRootPath,
            ["Schema:AutoMigrateOnStartup"] = "false",
            ["urls"] = "http://127.0.0.1:0"
        };

        if (overrides is not null)
        {
            foreach (var pair in overrides)
            {
                configOverrides[pair.Key] = pair.Value;
            }
        }

        builder.Configuration.AddInMemoryCollection(configOverrides);
        BackendHost.ConfigureServices(builder.Services, builder.Configuration, builder.Environment);

        var app = builder.Build();
        BackendHost.ConfigureMiddleware(app);
        BackendHost.ConfigureRoutes(app);

        await app.StartAsync();

        var server = app.Services.GetRequiredService<IServer>();
        var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses
            ?? throw new InvalidOperationException("Server addresses were not available after startup.");
        var baseAddress = addresses.Single();

        var client = new HttpClient
        {
            BaseAddress = new Uri(baseAddress)
        };

        return new ApiIntegrationTestHost(sqliteRootPath, app, client);
    }

    public async Task<T> GetJsonAsync<T>(string path, CancellationToken cancellationToken = default)
    {
        var response = await Client.GetAsync(path, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("Expected JSON response body.");
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();

        if (Directory.Exists(_sqliteRootPath))
        {
            Directory.Delete(_sqliteRootPath, recursive: true);
        }
    }
}
