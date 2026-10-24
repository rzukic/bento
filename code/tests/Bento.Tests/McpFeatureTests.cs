using System.Text.Json;

namespace Bento.Tests;

/// <summary>
/// Step 5.14: what a connection may do with Phase 5's features — rules and
/// reports, calendar apps (never device passwords), share links, workspace
/// settings, categorisation rules — each behind its own permission.
/// </summary>
public sealed class McpFeatureTests
{
    [Fact]
    public async Task Connection_SetsUpRules_Apps_Shares_AndSettings() => await FeaturesAsync(await McpTests.CreateHostAsync("https://bento.example"));

    internal static async Task FeaturesAsync(ApiIntegrationTestHost host)
    {
        await using var disposable = host;
        var admin = await ApiKeyTests.AdminTokenAsync(host);
        var channel = await Data(await ApiKeyTests.SendAsync(host, HttpMethod.Post, "/api/v1/admin/notifications/channels", admin,
            new { name = "Team chat", kind = "webhook", config = new { url = "https://hooks.example/secret-path-123" } }));
        var channelId = channel.GetProperty("id").GetInt64();

        var full = await McpTests.ConnectAsync(host, admin, "read-write",
            ["data.read", "data.write", "dashboards.write", "automation.write", "apps.write", "shares.write", "settings.write"]);
        async Task<(string Text, bool IsError)> Api(string token, string method, string path, object? body = null) =>
            await McpTests.CallToolAsync(host, token, "call_api", body is null ? new { method, path } : new { method, path, body });

        // Channels by name only: no address, no secret.
        var channels = await Api(full, "GET", "/api/v1/admin/notifications/rules/channels");
        Assert.False(channels.IsError, channels.Text);
        Assert.Contains("Team chat", channels.Text);
        Assert.DoesNotContain("secret-path", channels.Text);
        Assert.True((await Api(full, "GET", "/api/v1/admin/notifications/channels")).IsError);

        // A rule on new customers.
        var rule = await Api(full, "POST", "/api/v1/admin/notifications/rules",
            new { name = "New customer", kind = "event", config = new { @event = "row.created", source = "customers" }, channels = new[] { channelId } });
        Assert.False(rule.IsError, rule.Text);
        Assert.Contains("A row is added to Customers", rule.Text);

        // A calendar app: the answer brings the page where the user adds a device; the password is never made here.
        var app = await Api(full, "POST", "/api/v1/admin/apps", new
        {
            name = "Kunden", kind = "calendar",
            config = new { calendars = new[] { new { name = "Joined", table = "customers", title = "name", start = "joined_on" } } }
        });
        Assert.False(app.IsError, app.Text);
        var created = JsonDocument.Parse(app.Text).RootElement;
        var appId = created.GetProperty("data").GetProperty("id").GetInt64();
        Assert.Equal($"https://bento.example/apps?app={appId}", created.GetProperty("setupLink").GetString());
        var password = await Api(full, "POST", $"/api/v1/admin/apps/{appId}/passwords", new { name = "iPhone" });
        Assert.True(password.IsError);
        Assert.Contains("cannot use this part", password.Text);

        // A share link needs its own permission.
        var dashboard = JsonDocument.Parse((await McpTests.CallToolAsync(host, full, "create_dashboard", new { name = "Kunden", dashboard = new { widgets = Array.Empty<object>() } })).Text).RootElement;
        var dashboardId = (dashboard.TryGetProperty("data", out var d) ? d : dashboard).GetProperty("id").GetInt64();
        var share = await Api(full, "POST", $"/api/v1/dashboards/{dashboardId}/shares", new { name = "Family" });
        Assert.False(share.IsError, share.Text);
        Assert.Contains("/s/bento_s_", share.Text);

        // Settings and categorisation rules.
        Assert.False((await Api(full, "PUT", "/api/v1/admin/settings", new { locale = "de-AT", currency = "EUR" })).IsError);
        Assert.Contains("de-AT", (await Api(full, "GET", "/api/v1/admin/settings")).Text);
        Assert.False((await Api(full, "GET", "/api/v1/categorise/customers/rules")).IsError);

        // A connection without those permissions reads, but changes none of it.
        var limited = await McpTests.ConnectAsync(host, admin, "read-write", ["data.read", "dashboards.write"]);
        Assert.False((await Api(limited, "GET", "/api/v1/admin/notifications/rules")).IsError);
        Assert.Contains("automation.write", (await Api(limited, "POST", "/api/v1/admin/notifications/rules",
            new { name = "x", kind = "event", config = new { @event = "import.failed" }, channels = new[] { channelId } })).Text);
        Assert.Contains("shares.write", (await Api(limited, "POST", $"/api/v1/dashboards/{dashboardId}/shares", new { name = "x" })).Text);
        Assert.Contains("apps.write", (await Api(limited, "DELETE", $"/api/v1/admin/apps/{appId}")).Text);
        Assert.Contains("settings.write", (await Api(limited, "PUT", "/api/v1/admin/settings", new { locale = "en", currency = "USD" })).Text);

        // The guide explains it.
        Assert.Contains("row.created", (await McpTests.CallToolAsync(host, full, "bento_guide", new { topic = "automation" })).Text);
        Assert.Contains("setupLink", (await McpTests.CallToolAsync(host, full, "bento_guide", new { topic = "apps" })).Text);
    }

    private static async Task<JsonElement> Data(HttpResponseMessage response)
    {
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, $"{response.RequestMessage?.RequestUri} {(int)response.StatusCode}: {text}");
            return JsonDocument.Parse(text).RootElement.GetProperty("data").Clone();
        }
    }
}

[Collection(PostgresCollection.Name)]
public sealed class PostgresMcpFeatureTests
{
    [PostgresFact]
    public async Task Connection_SetsUpRules_Apps_Shares_AndSettings_OnPostgres()
    {
        var host = await ApiIntegrationTestHost.StartPostgresAsync(new Dictionary<string, string?>
        {
            ["Backend:AuthMode"] = "standard",
            ["Backend:Jwt:Secret"] = "super-secret",
            ["Backend:AuthRateLimitPerMinute"] = "100",
            ["Backend:PublicUrl"] = "https://bento.example",
            ["Schema:AutoMigrateOnStartup"] = "false"
        });
        await host.ApplySchemaAsync(ApiKeyTests.Schema);
        await McpFeatureTests.FeaturesAsync(host);
    }
}
