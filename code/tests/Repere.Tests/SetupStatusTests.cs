using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Repere.Api.Models;

namespace Repere.Tests;

/// <summary>
/// Contract of the public first-run status probe consumed by the setup wizard.
/// </summary>
public sealed class SetupStatusTests
{
    private const string TinySchema = """
        {
            "tables": [
                {
                    "name": "notes",
                    "columns": [
                        { "name": "id", "type": "integer", "primaryKey": true },
                        { "name": "body", "type": "text" }
                    ]
                }
            ],
            "relations": []
        }
        """;

    [Fact]
    public async Task FreshStandardInstall_IsNotInitialized_AndBootstrapIsOpen()
    {
        await using var host = await StartStandardAsync();

        var status = await GetStatusAsync(host);

        Assert.False(status.Initialized);
        Assert.True(status.RegistrationBootstrapOpen);
        Assert.Equal("standard", status.AuthMode);
        Assert.True(status.AuthRequired);
        Assert.Equal("sqlite", status.DatabaseProvider);
        Assert.Equal(0, status.SchemaVersion);
        Assert.True(status.SampleDataAvailable);
        Assert.False(string.IsNullOrWhiteSpace(status.Version));
    }

    [Fact]
    public async Task Status_IsAnonymous_AndNeverLeaksSecretsOrPaths()
    {
        await using var host = await StartStandardAsync();

        using var response = await host.Client.GetAsync("/api/v1/setup/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var raw = await response.Content.ReadFromJsonAsync<JsonElement>();
        var keys = raw.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain("jwt_secret", keys);
        Assert.DoesNotContain("data_dir", keys);
        Assert.DoesNotContain("config_path", keys);
        Assert.DoesNotContain("jwt_configured", keys);
    }

    [Fact]
    public async Task BootstrapRegistration_FlipsInitialized_AndClosesBootstrap()
    {
        await using var host = await StartStandardAsync();

        using var register = await host.Client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            username = "first-admin",
            password = "supersecure"
        });
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);

        var status = await GetStatusAsync(host);

        Assert.True(status.Initialized);
        Assert.False(status.RegistrationBootstrapOpen);
        Assert.Equal(0, status.SchemaVersion);
        Assert.True(status.SampleDataAvailable);
    }

    [Fact]
    public async Task AppliedSchema_RaisesVersion_AndDisablesSampleData()
    {
        await using var host = await ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Backend:AuthMode"] = "open",
            ["Schema:AutoMigrateOnStartup"] = "false"
        });

        using var apply = await host.Client.PutAsync(
            "/api/v1/admin/schema",
            new StringContent(TinySchema, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, apply.StatusCode);

        var status = await GetStatusAsync(host);

        Assert.Equal(1, status.SchemaVersion);
        Assert.False(status.SampleDataAvailable);
    }

    [Fact]
    public async Task OpenMode_CountsAsInitialized_WithoutAnyUser()
    {
        await using var host = await ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Backend:AuthMode"] = "open",
            ["Schema:AutoMigrateOnStartup"] = "false"
        });

        var status = await GetStatusAsync(host);

        Assert.True(status.Initialized);
        Assert.Equal("open", status.AuthMode);
        Assert.False(status.AuthRequired);
        Assert.True(status.RegistrationBootstrapOpen);
    }

    [Fact]
    public async Task OtherSetupRoutes_StayBehindAuth_InStandardMode()
    {
        await using var host = await StartStandardAsync();

        // Only /setup/status is public; anything else under /setup requires a token.
        using var response = await host.Client.PostAsync("/api/v1/setup/sample-data", content: null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static Task<ApiIntegrationTestHost> StartStandardAsync()
    {
        return ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Backend:AuthMode"] = "standard",
            ["Backend:Jwt:Secret"] = "setup-status-test-secret",
            ["Schema:AutoMigrateOnStartup"] = "false"
        });
    }

    private static async Task<SetupStatusResponse> GetStatusAsync(ApiIntegrationTestHost host)
    {
        using var response = await host.Client.GetAsync("/api/v1/setup/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<SetupStatusResponse>();
        return Assert.IsType<SetupStatusResponse>(payload);
    }
}
