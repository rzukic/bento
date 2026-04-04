using System.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Repere.Api;
using Repere.Api.Controllers;
using Repere.Api.Db;
using Repere.Api.Models;
using Repere.Api.Services;

namespace Repere.Tests;

public sealed class ApiHostTests
{
    [Fact]
    public async Task ConfigureRoutes_MapsCanonicalAdminHealthEndpointUnderApiV1()
    {
        using var root = new TemporarySqliteRoot();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(Program).Assembly.FullName,
            EnvironmentName = Environments.Development,
            ContentRootPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/backend"))
        });

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Provider"] = nameof(DatabaseProvider.Sqlite),
            ["Database:SystemConnection"] = root.Path,
            ["Database:DataConnection"] = root.Path,
            ["Schema:AutoMigrateOnStartup"] = "false"
        });

        BackendHost.ConfigureConfiguration(builder);
        BackendHost.ConfigureServices(builder.Services, builder.Configuration, builder.Environment);

        await using var app = builder.Build();
        BackendHost.ConfigureRoutes(app);

        var routeBuilder = (IEndpointRouteBuilder)app;

        var endpoints = routeBuilder.DataSources
            .SelectMany(dataSource => dataSource.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .Where(pattern => pattern is not null)
            .ToList();

        Assert.Contains(ApiRoutes.AdminHealth, endpoints);
        Assert.DoesNotContain("health", endpoints.Where(pattern => pattern != ApiRoutes.AdminHealth));
    }

    [Fact]
    public void ConfigureRoutes_ReservesDocumentedRouteAreas_IncludingSchema()
    {
        var reservedRoutes = BackendHost.ReservedRouteAreas
            .Select(area => area.RoutePrefix)
            .ToArray();

        Assert.Equal(
            [ApiRoutes.Auth, ApiRoutes.Schema, ApiRoutes.Admin, ApiRoutes.Data, ApiRoutes.Query],
            reservedRoutes);
    }

    [Fact]
    public async Task AdminController_ReturnsHealthyStatus_ForOpenPools()
    {
        var controller = new AdminController(new AdminHealthService(
            new SqliteTestPool(),
            new SqliteTestPool(),
            new BackendRuntimeInfo(DateTimeOffset.UtcNow.Subtract(TimeSpan.FromMinutes(5)), "1.2.0")));

        var result = await controller.GetHealthAsync(CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status200OK, objectResult.StatusCode);

        var response = Assert.IsType<AdminHealthResponse>(objectResult.Value);
        Assert.Equal("healthy", response.Status);
        Assert.Equal("connected", response.Database.SystemPool);
        Assert.Equal("connected", response.Database.DataPool);
        Assert.Equal("5m", response.Uptime);
        Assert.Equal("1.2.0", response.Version);
        Assert.Null(response.Error);
    }

    [Fact]
    public async Task AdminController_ReturnsServiceUnavailable_WhenHealthCheckFails()
    {
        var controller = new AdminController(new AdminHealthService(
            new FailingDbPool(),
            new FailingDbPool(),
            new BackendRuntimeInfo(DateTimeOffset.UtcNow.Subtract(TimeSpan.FromMinutes(1)), "1.2.0")));

        var result = await controller.GetHealthAsync(CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, objectResult.StatusCode);

        var response = Assert.IsType<AdminHealthResponse>(objectResult.Value);
        Assert.Equal("unhealthy", response.Status);
        Assert.Equal("unavailable", response.Database.SystemPool);
        Assert.Equal("unavailable", response.Database.DataPool);
        Assert.Equal("1m", response.Uptime);
        Assert.Equal("1.2.0", response.Version);
        Assert.Contains("boom", response.Error, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class TemporarySqliteRoot : IDisposable
    {
        public TemporarySqliteRoot()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "repere-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private sealed class SqliteTestPool : ISystemPool, IDataPool
    {
        public DatabaseProvider Provider => DatabaseProvider.Sqlite;

        public async Task<IDbConnection> GetConnectionAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            return connection;
        }
    }

    private sealed class FailingDbPool : ISystemPool, IDataPool
    {
        public DatabaseProvider Provider => DatabaseProvider.Sqlite;

        public Task<IDbConnection> GetConnectionAsync()
        {
            throw new InvalidOperationException("boom");
        }
    }
}
