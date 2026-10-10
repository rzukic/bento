using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Bento.Tests;

/// <summary>Step 3.3: Dashboard JSON storage, layout validation, ownership and renames.</summary>
public sealed class DashboardApiTests
{
    private static object Widget(string id, string type, int x, int y, int w, int h, object? data = null) =>
        new { id, type, title = id, position = new { x, y, w, h }, data };

    private static object MonthlyQuery(string source = "transactions") => new
    {
        source,
        groupBy = new[] { new { column = "booked_on", bucket = "month", @as = "month" } },
        measures = new[] { new { name = "net", fn = "sum", column = "amount" } }
    };

    [Fact]
    public async Task Dashboards_CreateLoadUpdateDelete()
    {
        await using var host = await CreateHostAsync();

        var created = await SendAsync(host, HttpMethod.Post, "/api/v1/dashboards", new
        {
            name = "Monthly Cash Flow",
            dashboard = new
            {
                dateRange = new { preset = "last-12-months" },
                widgets = new[] { Widget("w1", "line", 0, 0, 8, 4, new { query = MonthlyQuery(), dateColumn = "booked_on" }) }
            }
        }, HttpStatusCode.Created);
        var id = created.GetProperty("id").GetInt64();
        Assert.True(created.GetProperty("isOwner").GetBoolean());
        Assert.False(created.GetProperty("isShared").GetBoolean());
        Assert.Equal("last-12-months", created.GetProperty("dashboard").GetProperty("dateRange").GetProperty("preset").GetString());

        var updated = await SendAsync(host, HttpMethod.Put, $"/api/v1/dashboards/{id}", new
        {
            name = "Cash Flow",
            isShared = true,
            dashboard = new { widgets = new[] { Widget("w1", "kpi", 0, 0, 3, 2), Widget("w2", "text", 3, 0, 9, 2) } }
        }, HttpStatusCode.OK);
        Assert.Equal("Cash Flow", updated.GetProperty("name").GetString());
        Assert.True(updated.GetProperty("isShared").GetBoolean());
        Assert.Equal(2, updated.GetProperty("dashboard").GetProperty("widgets").GetArrayLength());

        var list = await host.Client.GetFromJsonAsync<JsonElement>("/api/v1/dashboards");
        Assert.Equal("Cash Flow", Assert.Single(list.GetProperty("data").EnumerateArray()).GetProperty("name").GetString());

        using var deleted = await host.Client.DeleteAsync($"/api/v1/dashboards/{id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        using var gone = await host.Client.GetAsync($"/api/v1/dashboards/{id}");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Theory]
    [InlineData("""{ "name": "x", "dashboard": [] }""", "dashboard")]
    [InlineData("""{ "name": "x", "dashboard": { } }""", "dashboard.widgets")]
    [InlineData("""{ "name": "x", "dashboard": { "widgets": [ { "id": "a", "type": "pie", "position": { "x": 0, "y": 0, "w": 2, "h": 2 } } ] } }""", "dashboard.widgets[0].type")]
    [InlineData("""{ "name": "x", "dashboard": { "widgets": [ { "id": "a", "type": "kpi", "position": { "x": 10, "y": 0, "w": 4, "h": 2 } } ] } }""", "dashboard.widgets[0].position")]
    [InlineData("""{ "name": "x", "dashboard": { "widgets": [ { "id": "a", "type": "kpi", "position": { "x": 0, "y": 0, "w": 2, "h": 2 } }, { "id": "a", "type": "kpi", "position": { "x": 2, "y": 0, "w": 2, "h": 2 } } ] } }""", "dashboard.widgets[1].id")]
    // Widget options (beta feedback: "sankey": "xyz" was saved without a word).
    [InlineData("""{ "name": "x", "dashboard": { "widgets": [ { "id": "a", "type": "sankey", "position": { "x": 0, "y": 0, "w": 4, "h": 3 }, "display": { "sankey": "xyz" } } ] } }""", "dashboard.widgets[0].display.sankey")]
    [InlineData("""{ "name": "x", "dashboard": { "widgets": [ { "id": "a", "type": "table", "position": { "x": 0, "y": 0, "w": 4, "h": 3 }, "display": { "format": "euro" } } ] } }""", "dashboard.widgets[0].display.format")]
    [InlineData("""{ "name": "x", "dashboard": { "widgets": [ { "id": "a", "type": "bar", "position": { "x": 0, "y": 0, "w": 4, "h": 3 }, "display": { "stacked": "yes" } } ] } }""", "dashboard.widgets[0].display.stacked")]
    [InlineData("""{ "name": "x", "dashboard": { "widgets": [ { "id": "a", "type": "bar", "position": { "x": 0, "y": 0, "w": 4, "h": 3 }, "display": { "intervals": ["month", "decade"] } } ] } }""", "dashboard.widgets[0].display.intervals[1]")]
    [InlineData("""{ "name": "x", "dashboard": { "widgets": [ { "id": "a", "type": "bar", "position": { "x": 0, "y": 0, "w": 4, "h": 3 }, "display": { "referenceLines": [ { "label": "x" } ] } } ] } }""", "dashboard.widgets[0].display.referenceLines[0]")]
    [InlineData("""{ "name": "x", "dashboard": { "widgets": [ { "id": "a", "type": "line", "position": { "x": 0, "y": 0, "w": 4, "h": 3 }, "data": { "timeShift": "lastyear" } } ] } }""", "dashboard.widgets[0].data.timeShift")]
    [InlineData("""{ "name": "x", "dashboard": { "widgets": [ { "id": "a", "type": "budget", "position": { "x": 0, "y": 0, "w": 4, "h": 3 }, "data": { "budget": { "source": "t", "amount": "a", "date": "d", "category": "c" } } } ] } }""", "dashboard.widgets[0].data.budget.limit")]
    [InlineData("""{ "name": "x", "dashboard": { "widgets": [ { "id": "a", "type": "budget", "position": { "x": 0, "y": 0, "w": 4, "h": 3 }, "data": { "budget": { "source": "t", "amount": "a", "date": "d", "category": "c", "limit": "l", "period": "week" } } } ] } }""", "dashboard.widgets[0].data.budget.period")]
    [InlineData("""{ "name": "x", "dashboard": { "widgets": [ { "id": "a", "type": "table", "position": { "x": 0, "y": 0, "w": 4, "h": 3 }, "data": { "rows": { "columns": ["a"], "limit": 5000 } } } ] } }""", "dashboard.widgets[0].data.rows.limit")]
    [InlineData("""{ "name": "x", "dashboard": { "widgets": [ { "id": "a", "type": "line", "position": { "x": 0, "y": 0, "w": 4, "h": 3 }, "data": { "query": { "source": "t", "measures": [ { "name": "x", "fn": "sum", "column": "a", "axis": "left" } ] } } } ] } }""", "dashboard.widgets[0].data.query.measures[0].axis")]
    [InlineData("""{ "name": "x", "dashboard": { "widgets": [ { "id": "a", "type": "line", "position": { "x": 0, "y": 0, "w": 4, "h": 3 }, "display": { "yAxis": { "min": "data" } } } ] } }""", "dashboard.widgets[0].display.yAxis.min")]
    [InlineData("""{ "name": "x", "dashboard": { "widgets": [ { "id": "a", "type": "line", "position": { "x": 0, "y": 0, "w": 4, "h": 3 }, "display": { "emptyPeriods": "skip" } } ] } }""", "dashboard.widgets[0].display.emptyPeriods")]
    [InlineData("""{ "name": "x", "dashboard": { "widgets": [ { "id": "a", "type": "table", "position": { "x": 0, "y": 0, "w": 4, "h": 3 }, "display": { "columns": { "xirr": { "format": "pct" } } } } ] } }""", "dashboard.widgets[0].display.columns.xirr.format")]
    [InlineData("""{ "name": "x", "dashboard": { "widgets": [ { "id": "a", "type": "table", "position": { "x": 0, "y": 0, "w": 4, "h": 3 }, "display": { "columns": { "xirr": { "signed": "yes" } } } } ] } }""", "dashboard.widgets[0].display.columns.xirr.signed")]
    [InlineData("""{ "name": "x", "dashboard": { "widgets": [ { "id": "a", "type": "line", "position": { "x": 0, "y": 0, "w": 4, "h": 3 }, "data": { "range": { "preset": "next-100-years" } } } ] } }""", "dashboard.widgets[0].data.range.preset")]
    [InlineData("""{ "name": "x", "dashboard": { "widgets": [ { "id": "a", "type": "line", "position": { "x": 0, "y": 0, "w": 4, "h": 3 }, "data": { "range": { "from": "2026-01-01", "to": "2045" } } } ] } }""", "dashboard.widgets[0].data.range")]
    [InlineData("""{ "name": " ", "dashboard": { "widgets": [] } }""", "name")]
    public async Task Invalid_DashboardsNameTheField(string body, string field)
    {
        await using var host = await CreateHostAsync();

        using var response = await host.Client.PostAsync("/api/v1/dashboards", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(field, payload.GetProperty("error").GetProperty("details").GetProperty("field").GetString());
    }

    [Fact]
    public async Task DisplayOptions_FromBetaFeedback_AreStored()
    {
        await using var host = await CreateHostAsync();
        const string body = """
            { "name": "Forecast", "dashboard": { "widgets": [
              { "id": "f", "type": "line", "position": { "x": 0, "y": 0, "w": 6, "h": 4 },
                "data": { "range": { "preset": "next-20-years" }, "asOf": true },
                "display": { "yAxis": { "min": "auto" }, "emptyPeriods": "connect" } },
              { "id": "t", "type": "table", "position": { "x": 6, "y": 0, "w": 6, "h": 4 },
                "data": { "range": { "from": "2026-01-01", "to": "2045-12-31" } },
                "display": { "columns": { "xirr": { "format": "percent", "signed": true, "colorBySign": true } }, "yAxis": { "min": 100 } } }
            ] } }
            """;
        using var response = await host.Client.PostAsync("/api/v1/dashboards", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var stored = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetProperty("dashboard").GetProperty("widgets");
        Assert.Equal("next-20-years", stored[0].GetProperty("data").GetProperty("range").GetProperty("preset").GetString());
        Assert.True(stored[1].GetProperty("display").GetProperty("columns").GetProperty("xirr").GetProperty("signed").GetBoolean());
    }

    [Fact]
    public async Task Renames_RewriteWidgetQueriesAndDateColumns()
    {
        await using var host = await CreateHostAsync();
        var saved = await SendAsync(host, HttpMethod.Post, "/api/v1/queries", new { name = "Monthly", query = MonthlyQuery() }, HttpStatusCode.Created);
        var savedId = saved.GetProperty("id").GetInt64();
        var dashboard = await SendAsync(host, HttpMethod.Post, "/api/v1/dashboards", new
        {
            name = "Finance",
            dashboard = new
            {
                widgets = new object[]
                {
                    Widget("inline", "line", 0, 0, 6, 4, new { query = MonthlyQuery(), dateColumn = "booked_on" }),
                    Widget("saved", "bar", 6, 0, 6, 4, new { savedQueryId = savedId, dateColumn = "booked_on" })
                }
            }
        }, HttpStatusCode.Created);

        await host.ApplySchemaAsync(AggregationQueryTests.Schema
            .Replace("\"name\": \"transactions\",", "\"name\": \"bookings\", \"renamedFrom\": \"transactions\",")
            .Replace("\"to\": \"transactions\"", "\"to\": \"bookings\"")
            .Replace("{ \"name\": \"booked_on\", \"type\": \"date\", \"nullable\": false }", "{ \"name\": \"booked\", \"renamedFrom\": \"booked_on\", \"type\": \"date\", \"nullable\": false }"));

        var reloaded = await host.Client.GetFromJsonAsync<JsonElement>($"/api/v1/dashboards/{dashboard.GetProperty("id").GetInt64()}");
        var widgets = reloaded.GetProperty("data").GetProperty("dashboard").GetProperty("widgets");
        var inline = widgets[0].GetProperty("data");
        Assert.Equal("bookings", inline.GetProperty("query").GetProperty("source").GetString());
        Assert.Equal("booked", inline.GetProperty("query").GetProperty("groupBy")[0].GetProperty("column").GetString());
        Assert.Equal("booked", inline.GetProperty("dateColumn").GetString());
        Assert.Equal("booked", widgets[1].GetProperty("data").GetProperty("dateColumn").GetString());

        // The rewritten inline query still runs.
        using var run = await host.Client.PostAsJsonAsync("/api/v1/query", inline.GetProperty("query"));
        Assert.Equal(HttpStatusCode.OK, run.StatusCode);
    }

    [Fact]
    public async Task SharedDashboards_AreVisible_ButOnlyTheOwnerChangesThem()
    {
        await using var host = await ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Backend:AuthMode"] = "standard",
            ["Backend:Jwt:Secret"] = "super-secret",
            ["Backend:AuthRateLimitPerMinute"] = "100",
            ["Schema:AutoMigrateOnStartup"] = "false"
        });
        (await host.Client.PostAsJsonAsync("/api/v1/auth/register", new { username = "bootstrap-admin", password = "supersecure" })).EnsureSuccessStatusCode();
        var admin = await LoginAsync(host, "bootstrap-admin", "supersecure");
        using (var createViewer = Request(HttpMethod.Post, "/api/v1/auth/register", admin, new { username = "viewer", password = "anotherpass", role = "viewer" }))
            (await host.Client.SendAsync(createViewer)).EnsureSuccessStatusCode();
        var viewer = await LoginAsync(host, "viewer", "anotherpass");

        long shared;
        using (var create = Request(HttpMethod.Post, "/api/v1/dashboards", admin, new { name = "Team", isShared = true, dashboard = new { widgets = Array.Empty<object>() } }))
            shared = (await (await host.Client.SendAsync(create)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetProperty("id").GetInt64();
        using (var create = Request(HttpMethod.Post, "/api/v1/dashboards", admin, new { name = "Mine", dashboard = new { widgets = Array.Empty<object>() } }))
            (await host.Client.SendAsync(create)).EnsureSuccessStatusCode();

        // Viewers may keep dashboards of their own.
        using (var own = Request(HttpMethod.Post, "/api/v1/dashboards", viewer, new { name = "Viewer's", dashboard = new { widgets = Array.Empty<object>() } }))
            Assert.Equal(HttpStatusCode.Created, (await host.Client.SendAsync(own)).StatusCode);

        using var list = Request(HttpMethod.Get, "/api/v1/dashboards", viewer);
        var names = (await (await host.Client.SendAsync(list)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data")
            .EnumerateArray().Select(d => d.GetProperty("name").GetString()).ToList();
        Assert.Equal(["Team", "Viewer's"], names);

        using var change = Request(HttpMethod.Put, $"/api/v1/dashboards/{shared}", viewer, new { name = "Hijacked" });
        Assert.Equal(HttpStatusCode.Forbidden, (await host.Client.SendAsync(change)).StatusCode);
    }

    // ── Helpers ──

    private static async Task<JsonElement> SendAsync(ApiIntegrationTestHost host, HttpMethod method, string url, object body, HttpStatusCode expected)
    {
        using var request = new HttpRequestMessage(method, url) { Content = JsonContent.Create(body) };
        using var response = await host.Client.SendAsync(request);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {payload}");
        return payload.GetProperty("data");
    }

    private static HttpRequestMessage Request(HttpMethod method, string url, string token, object? body = null)
    {
        var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static async Task<string> LoginAsync(ApiIntegrationTestHost host, string username, string password)
    {
        using var response = await host.Client.PostAsJsonAsync("/api/v1/auth/login", new { username, password });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<Bento.Api.Models.AuthTokenResponse>())!.AccessToken;
    }

    [Fact]
    public async Task SankeyWidgetsAndAnnotations_AreStored()
    {
        await using var host = await CreateHostAsync();

        using var created = await host.Client.PostAsJsonAsync("/api/v1/dashboards", new
        {
            name = "Flows",
            dashboard = new
            {
                widgets = new[] { new { id = "flow", type = "sankey", position = new { x = 0, y = 0, w = 8, h = 5 }, display = new { sankey = "cashflow" } } },
                annotations = new[] { new { date = "2026-09-01", label = "Salary raise", color = "teal" } }
            }
        });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        var dashboard = body.GetProperty("data").GetProperty("dashboard");
        Assert.Equal("sankey", dashboard.GetProperty("widgets")[0].GetProperty("type").GetString());
        Assert.Equal("Salary raise", dashboard.GetProperty("annotations")[0].GetProperty("label").GetString());
    }

    private static async Task<ApiIntegrationTestHost> CreateHostAsync()
    {
        var host = await ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Backend:AuthMode"] = "open",
            ["Schema:AutoMigrateOnStartup"] = "false"
        });
        await host.ApplySchemaAsync(AggregationQueryTests.Schema);
        return host;
    }
}

[Trait("Category", "Postgres")]
[Collection(PostgresCollection.Name)]
public sealed class PostgresDashboardApiTests
{
    [PostgresFact]
    public async Task Dashboards_RoundTripOnPostgres()
    {
        await using var host = await ApiIntegrationTestHost.StartPostgresAsync(new Dictionary<string, string?>
        {
            ["Backend:AuthMode"] = "open",
            ["Backend:Jwt:Secret"] = "postgres-test-secret",
            ["Schema:AutoMigrateOnStartup"] = "false"
        });

        using var created = await host.Client.PostAsJsonAsync("/api/v1/dashboards", new
        {
            name = "Finance",
            isShared = true,
            dashboard = new { widgets = new[] { new { id = "w1", type = "kpi", position = new { x = 0, y = 0, w = 3, h = 2 } } } }
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var list = await host.Client.GetFromJsonAsync<JsonElement>("/api/v1/dashboards");
        var only = Assert.Single(list.GetProperty("data").EnumerateArray());
        Assert.True(only.GetProperty("isShared").GetBoolean());
        Assert.Equal("kpi", only.GetProperty("dashboard").GetProperty("widgets")[0].GetProperty("type").GetString());
    }
}
