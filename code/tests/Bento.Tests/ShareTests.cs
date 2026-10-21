using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Bento.Tests;

/// <summary>
/// Step 5.6: share links — a dashboard for anyone with the link, showing what
/// its widgets show and nothing else of the workspace.
/// </summary>
public sealed class ShareTests
{
    internal const string Schema = """
        { "tables": [
            { "name": "transactions", "columns": [
                { "name": "id", "type": "integer", "primaryKey": true },
                { "name": "amount", "type": "currency" },
                { "name": "booked_on", "type": "date" },
                { "name": "category", "type": "text" },
                { "name": "counterparty", "type": "text" } ] },
            { "name": "secrets", "columns": [
                { "name": "id", "type": "integer", "primaryKey": true },
                { "name": "note", "type": "text" } ] } ] }
        """;

    private static readonly object Spending = new
    {
        source = "transactions",
        filter = new { amount = new { lt = 0 } },
        measures = new[] { new { name = "spent", fn = "sum", column = "amount" } }
    };

    [Fact]
    public async Task ShareLinks_ShowTheDashboardAndNothingElse() => await SharesAsync(await HostAsync());

    internal static async Task<ApiIntegrationTestHost> HostAsync()
    {
        var host = await ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Backend:AuthMode"] = "standard",
            ["Backend:Jwt:Secret"] = "super-secret",
            ["Backend:AuthRateLimitPerMinute"] = "100",
            ["Backend:PublicUrl"] = "https://bento.example",
            ["Schema:AutoMigrateOnStartup"] = "false"
        });
        await host.ApplySchemaAsync(Schema);
        return host;
    }

    internal static async Task SharesAsync(ApiIntegrationTestHost host)
    {
        await using var _ = host;
        var admin = await ApiKeyTests.AdminTokenAsync(host);
        foreach (var (amount, category, day) in new[] { (-10.0, "food", "2026-03-01"), (-30.0, "rent", "2026-03-02"), (-5.0, "food", "2026-04-01"), (100.0, "salary", "2026-03-03") })
            await Ok(await ApiKeyTests.SendAsync(host, HttpMethod.Post, "/api/v1/data/transactions", admin, new { amount, category, booked_on = day, counterparty = "X" }));
        await Ok(await ApiKeyTests.SendAsync(host, HttpMethod.Post, "/api/v1/data/secrets", admin, new { note = "private" }));

        var dashboard = await Data(await ApiKeyTests.SendAsync(host, HttpMethod.Post, "/api/v1/dashboards", admin, new
        {
            name = "Spending",
            dashboard = new
            {
                filters = new[] { new { column = "category" } },
                widgets = new object[]
                {
                    new { id = "w1", type = "kpi", title = "Spent", position = new { x = 0, y = 0, w = 3, h = 2 }, data = new { query = Spending, dateColumn = "booked_on" } }
                }
            }
        }));
        var id = dashboard.GetProperty("id").GetInt64();
        var other = await Data(await ApiKeyTests.SendAsync(host, HttpMethod.Post, "/api/v1/dashboards", admin, new { name = "Other", dashboard = new { widgets = Array.Empty<object>() } }));

        await Status(await ApiKeyTests.SendAsync(host, HttpMethod.Post, $"/api/v1/dashboards/{id}/shares", admin, new { filters = new { counterparty = new[] { "X" } } }),
            HttpStatusCode.BadRequest, "not a filter of this dashboard");
        string token;
        using (var created = await ApiKeyTests.SendAsync(host, HttpMethod.Post, $"/api/v1/dashboards/{id}/shares", admin,
                   new { name = "For the tax advisor", expiresInDays = 7, filters = new { category = new[] { "food" } }, embed = true }))
        {
            var text = await created.Content.ReadAsStringAsync();
            Assert.True(created.StatusCode == HttpStatusCode.Created, text);
            var body = JsonDocument.Parse(text).RootElement;
            token = body.GetProperty("token").GetString()!;
            Assert.StartsWith("https://bento.example/s/bento_s_", body.GetProperty("url").GetString());
            Assert.Contains("<iframe", body.GetProperty("embed").GetString());
        }

        // The visitor: the link's info, the dashboard, its widget's query.
        var info = await Data(await Share(host, HttpMethod.Get, "/api/v1/share", token));
        Assert.Equal(id, info.GetProperty("dashboardId").GetInt64());
        Assert.True(info.GetProperty("embed").GetBoolean());
        Assert.Equal("food", info.GetProperty("filters").GetProperty("category")[0].GetString());
        await Ok(await Share(host, HttpMethod.Get, $"/api/v1/dashboards/{id}", token));
        await Status(await Share(host, HttpMethod.Get, $"/api/v1/dashboards/{other.GetProperty("id").GetInt64()}", token), HttpStatusCode.Forbidden);

        var range = new { gte = "2026-01-01", lt = "2027-01-01" };
        var asWidget = await Data(await Share(host, HttpMethod.Post, "/api/v1/query", token, new
        {
            source = "transactions",
            filter = new { amount = new { lt = 0 }, booked_on = range, category = new { eq = "food" } },
            measures = new[] { new { name = "spent", fn = "sum", column = "amount" } }
        }));
        Assert.Equal(-15m, asWidget[0].GetProperty("spent").GetDecimal());
        // Without the fixed filter the link still applies it.
        var withoutFilter = await Data(await Share(host, HttpMethod.Post, "/api/v1/query", token, new
        {
            source = "transactions",
            filter = new { amount = new { lt = 0 }, booked_on = range },
            measures = new[] { new { name = "spent", fn = "sum", column = "amount" } }
        }));
        Assert.Equal(-15m, withoutFilter[0].GetProperty("spent").GetDecimal());
        // A month per row (the interval switch) is fine.
        var monthly = await Data(await Share(host, HttpMethod.Post, "/api/v1/query", token, new
        {
            source = "transactions",
            filter = new { amount = new { lt = 0 }, booked_on = range },
            groupBy = new[] { new { column = "booked_on", bucket = "month", @as = "month" } },
            measures = new[] { new { name = "spent", fn = "sum", column = "amount" } }
        }));
        Assert.Equal(2, monthly.GetArrayLength());

        // Anything else is refused.
        await Status(await Share(host, HttpMethod.Post, "/api/v1/query", token, new
        {
            source = "transactions",
            filter = new { amount = new { lt = 0 } },
            groupBy = new[] { new { column = "counterparty" } },
            measures = new[] { new { name = "spent", fn = "sum", column = "amount" } }
        }), HttpStatusCode.Forbidden);
        await Status(await Share(host, HttpMethod.Post, "/api/v1/query", token, new
        {
            source = "transactions",
            measures = new[] { new { name = "spent", fn = "sum", column = "amount" } }
        }), HttpStatusCode.Forbidden);
        await Status(await Share(host, HttpMethod.Post, "/api/v1/query", token, new
        {
            source = "transactions",
            filter = new { amount = new { lt = 0 }, category = new { eq = "rent" } },
            measures = new[] { new { name = "spent", fn = "sum", column = "amount" } }
        }), HttpStatusCode.Forbidden);
        await Status(await Share(host, HttpMethod.Post, "/api/v1/query", token, new { source = "secrets", measures = new[] { new { name = "n", fn = "count" } } }), HttpStatusCode.Forbidden);
        await Status(await Share(host, HttpMethod.Get, "/api/v1/data/secrets", token), HttpStatusCode.Forbidden);
        await Status(await Share(host, HttpMethod.Get, "/api/v1/data/transactions", token), HttpStatusCode.Forbidden);
        await Status(await Share(host, HttpMethod.Get, "/api/v1/dashboards", token), HttpStatusCode.Forbidden);
        await Status(await Share(host, HttpMethod.Post, "/api/v1/data/transactions", token, new { amount = 1 }), HttpStatusCode.Forbidden);
        var schema = await Share(host, HttpMethod.Get, "/api/v1/admin/schema", token);
        var tables = (await schema.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("tables");
        Assert.Equal("transactions", Assert.Single(tables.EnumerateArray()).GetProperty("name").GetString());

        // The owner's list, then revoked: the link stops.
        var links = await Data(await ApiKeyTests.SendAsync(host, HttpMethod.Get, $"/api/v1/dashboards/{id}/shares", admin));
        var link = Assert.Single(links.EnumerateArray());
        Assert.Equal(1, link.GetProperty("views").GetInt32());
        using (var revoked = await ApiKeyTests.SendAsync(host, HttpMethod.Delete, $"/api/v1/shares/{link.GetProperty("id").GetInt64()}", admin))
            Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        await Status(await Share(host, HttpMethod.Get, "/api/v1/share", token), HttpStatusCode.NotFound, "revoked");
        await Status(await Share(host, HttpMethod.Get, "/api/v1/share", "bento_s_made-up"), HttpStatusCode.NotFound);
    }

    private static async Task<HttpResponseMessage> Share(ApiIntegrationTestHost host, HttpMethod method, string url, string token, object? body = null)
    {
        var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Add("X-Bento-Share", token);
        return await host.Client.SendAsync(request);
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

    private static async Task Ok(HttpResponseMessage response)
    {
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, $"{response.RequestMessage?.RequestUri} {(int)response.StatusCode}: {text}");
        }
    }

    private static async Task Status(HttpResponseMessage response, HttpStatusCode expected, string? contains = null)
    {
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == expected, $"{response.RequestMessage?.RequestUri}: expected {(int)expected}, got {(int)response.StatusCode}: {text}");
            if (contains is not null)
                Assert.Contains(contains, text);
        }
    }
}

[Collection(PostgresCollection.Name)]
public sealed class PostgresShareTests
{
    [PostgresFact]
    public async Task ShareLinks_OnPostgres()
    {
        var host = await ApiIntegrationTestHost.StartPostgresAsync(new Dictionary<string, string?>
        {
            ["Backend:AuthMode"] = "standard",
            ["Backend:Jwt:Secret"] = "super-secret",
            ["Backend:AuthRateLimitPerMinute"] = "100",
            ["Backend:PublicUrl"] = "https://bento.example",
            ["Schema:AutoMigrateOnStartup"] = "false"
        });
        await host.ApplySchemaAsync(ShareTests.Schema);
        await ShareTests.SharesAsync(host);
    }
}
