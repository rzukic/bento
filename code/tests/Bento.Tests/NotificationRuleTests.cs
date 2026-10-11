using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bento.Api.Services.Import;
using Bento.Api.Services.Notifications;

namespace Bento.Tests;

/// <summary>
/// Step 5.3: rules that speak once when a condition starts to hold — a value
/// over a threshold in this month, a budget run over, a failed scheduled
/// import — and stay quiet until it clears.
/// </summary>
public sealed class NotificationRuleTests
{
    private const string Path = "/api/v1/admin/notifications/rules";

    private const string Schema = """
        { "tables": [
            { "name": "categories", "columns": [
                { "name": "id", "type": "integer", "primaryKey": true },
                { "name": "name", "type": "text", "nullable": false },
                { "name": "budget", "type": "currency" } ] },
            { "name": "transactions", "columns": [
                { "name": "id", "type": "integer", "primaryKey": true },
                { "name": "date", "type": "date", "nullable": false },
                { "name": "amount", "type": "currency", "nullable": false },
                { "name": "category_id", "type": "reference", "references": "categories" } ] }
          ],
          "relations": [ { "name": "category_transactions", "type": "1:N", "from": "categories", "to": "transactions", "column": "category_id" } ] }
        """;

    private static Task<ApiIntegrationTestHost> StartAsync() => ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?>
    {
        ["Backend:AuthMode"] = "open",
        ["Backend:Jwt:Secret"] = "sqlite-test-secret",
        ["Backend:PublicUrl"] = "https://bento.example",
        ["Schema:AutoMigrateOnStartup"] = "false",
        ["Import:AllowPrivateNetwork"] = "true"
    });

    /// <summary>Today as the rules see it (Europe/Berlin).</summary>
    private static string Today() => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow,
        TimeZoneInfo.FindSystemTimeZoneById(NotificationRuleService.TimeZoneId)).DateTime).ToString("yyyy-MM-dd");

    [Fact]
    public async Task Condition_SpeaksOnceWhenItStartsToHold_AndAgainAfterItCleared() => await ConditionAsync(await StartAsync());

    internal static async Task ConditionAsync(ApiIntegrationTestHost host)
    {
        await using var _ = host;
        await host.ApplySchemaAsync(Schema);
        using var receiver = new FakeReceiver();
        var channel = await PostAsync(host, "/api/v1/admin/notifications/channels", new { name = "Hook", kind = "webhook", config = new { url = $"{receiver.Url}/hook" } });
        await PostAsync(host, "/api/v1/data/transactions", new { date = Today(), amount = -50 });
        // An expense of last year does not count in this month.
        await PostAsync(host, "/api/v1/data/transactions", new { date = "2020-01-15", amount = -500 });

        var rule = await PostAsync(host, Path, new
        {
            name = "Spending this month",
            kind = "condition",
            config = new { source = "transactions", formula = "-SUM(amount WHERE amount < 0)", period = "this-month", dateColumn = "date", op = ">", threshold = 100 },
            channels = new[] { channel.GetProperty("id").GetInt64() }
        });
        var id = rule.GetProperty("id").GetInt64();
        Assert.Contains("this month · hourly", rule.GetProperty("summary").GetString());

        var quiet = await PostAsync(host, $"{Path}/{id}/check", new { });
        Assert.False(quiet.GetProperty("fired").GetBoolean());
        Assert.Equal("€50.00", quiet.GetProperty("state").GetProperty("value").GetString());
        Assert.Empty(receiver.Requests);

        var expense = await PostAsync(host, "/api/v1/data/transactions", new { date = Today(), amount = -80 });
        var fired = await PostAsync(host, $"{Path}/{id}/check", new { });
        Assert.True(fired.GetProperty("fired").GetBoolean());
        var message = JsonDocument.Parse(Assert.Single(receiver.Requests).Body).RootElement;
        Assert.Equal("Spending this month", message.GetProperty("title").GetString());
        Assert.Equal("€130.00 — above €100.00 (this month).", message.GetProperty("text").GetString());
        Assert.Equal("https://bento.example/data/transactions", message.GetProperty("link").GetString());

        // Still holds: quiet.
        Assert.False((await PostAsync(host, $"{Path}/{id}/check", new { })).GetProperty("fired").GetBoolean());
        Assert.Single(receiver.Requests);

        // Clears, then holds again: speaks again.
        (await host.Client.DeleteAsync($"/api/v1/data/transactions/{expense.GetProperty("id")}")).EnsureSuccessStatusCode();
        Assert.False((await PostAsync(host, $"{Path}/{id}/check", new { })).GetProperty("state").GetProperty("active").GetBoolean());
        await PostAsync(host, "/api/v1/data/transactions", new { date = Today(), amount = -90 });
        Assert.True((await PostAsync(host, $"{Path}/{id}/check", new { })).GetProperty("fired").GetBoolean());
        Assert.Equal(2, receiver.Requests.Count);

        // A test says what it would say, whatever the state.
        var test = await PostAsync(host, $"{Path}/{id}/test", new { });
        Assert.StartsWith("Test: ", test.GetProperty("notification").GetProperty("title").GetString());
        Assert.Equal(3, receiver.Requests.Count);

        // Just checked: not due; the scheduler's next look is in an hour.
        var service = host.GetService<NotificationRuleService>();
        Assert.DoesNotContain(id, await service.DueAsync());
    }

    [Fact]
    public async Task Budget_TellsEachCategoryOnceAMonth()
    {
        await using var host = await StartAsync();
        await host.ApplySchemaAsync(Schema);
        using var receiver = new FakeReceiver();
        var channel = await PostAsync(host, "/api/v1/admin/notifications/channels", new { name = "Hook", kind = "webhook", config = new { url = $"{receiver.Url}/hook" } });
        await PostAsync(host, "/api/v1/data/categories", new { name = "Groceries", budget = 300 });
        await PostAsync(host, "/api/v1/data/categories", new { name = "Rent", budget = 1000 });
        await PostAsync(host, "/api/v1/data/categories", new { name = "Gifts" });
        var dashboard = await PostAsync(host, "/api/v1/dashboards", new
        {
            name = "Cash flow",
            dashboard = new
            {
                widgets = new[]
                {
                    new
                    {
                        id = "budgets",
                        type = "budget",
                        position = new { x = 0, y = 0, w = 6, h = 4 },
                        data = new { budget = new { source = "transactions", amount = "amount", date = "date", category = "category_id", limit = "budget", expenses = "negative", period = "month" } }
                    }
                }
            }
        });
        await PostAsync(host, "/api/v1/data/transactions", new { date = Today(), amount = -250, category_id = 1 });
        await PostAsync(host, "/api/v1/data/transactions", new { date = Today(), amount = -900, category_id = 2 });

        var rule = await PostAsync(host, Path, new
        {
            name = "Over budget",
            kind = "budget",
            config = new { dashboardId = dashboard.GetProperty("id").GetInt64(), widgetId = "budgets", percent = 100 },
            channels = new[] { channel.GetProperty("id").GetInt64() }
        });
        var id = rule.GetProperty("id").GetInt64();
        Assert.False((await PostAsync(host, $"{Path}/{id}/check", new { })).GetProperty("fired").GetBoolean());

        await PostAsync(host, "/api/v1/data/transactions", new { date = Today(), amount = -70, category_id = 1 });
        var groceries = await PostAsync(host, $"{Path}/{id}/check", new { });
        Assert.True(groceries.GetProperty("fired").GetBoolean());
        var message = JsonDocument.Parse(Assert.Single(receiver.Requests).Body).RootElement;
        Assert.StartsWith("Groceries is at 107 % of its budget for ", message.GetProperty("text").GetString());
        Assert.Equal("€320.00 of €300.00 · 107 %", message.GetProperty("facts")[0].GetProperty("value").GetString());
        Assert.Equal($"https://bento.example/dashboards/{dashboard.GetProperty("id")}", message.GetProperty("link").GetString());

        // Groceries again: quiet. Rent runs over: only Rent is told.
        await PostAsync(host, "/api/v1/data/transactions", new { date = Today(), amount = -10, category_id = 1 });
        Assert.False((await PostAsync(host, $"{Path}/{id}/check", new { })).GetProperty("fired").GetBoolean());
        await PostAsync(host, "/api/v1/data/transactions", new { date = Today(), amount = -150, category_id = 2 });
        Assert.True((await PostAsync(host, $"{Path}/{id}/check", new { })).GetProperty("fired").GetBoolean());
        var rent = JsonDocument.Parse(receiver.Requests.Last().Body).RootElement;
        Assert.Equal(["Rent"], rent.GetProperty("facts").EnumerateArray().Select(f => f.GetProperty("label").GetString()));
    }

    [Fact]
    public async Task FailedScheduledImport_IsToldOnce_UntilItWorksAgain() => await ImportEventAsync(await StartAsync());

    internal static async Task ImportEventAsync(ApiIntegrationTestHost host)
    {
        await using var _ = host;
        await host.ApplySchemaAsync(Schema);
        using var receiver = new FakeReceiver();
        var channel = await PostAsync(host, "/api/v1/admin/notifications/channels", new { name = "Hook", kind = "webhook", config = new { url = $"{receiver.Url}/hook" } });
        await PostAsync(host, Path, new { name = "Import failed", kind = "event", config = new { @event = "import.failed" }, channels = new[] { channel.GetProperty("id").GetInt64() } });

        var status = 500;
        using var bank = new FakeQuoteServer(_ => (status, "\"date\";\"amount\"\n\"" + Today() + "\";\"-12,40\"", null));
        var source = await PostAsync(host, "/api/v1/import/transactions/sources", new
        {
            name = "Girokonto",
            config = new
            {
                url = $"{bank.Url}/konto.csv",
                mapping = new object[] { new { source = "date", target = "date" }, new { source = "amount", target = "amount" } },
                schedule = new { kind = "hours", every = 24 }
            }
        });
        var imports = host.GetService<ImportSourceService>();
        var sourceId = source.GetProperty("id").GetInt64();

        await imports.RunScheduledAsync(sourceId, CancellationToken.None);
        var told = JsonDocument.Parse(Assert.Single(receiver.Requests).Body).RootElement;
        Assert.Equal("import.failed", told.GetProperty("event").GetString());
        Assert.Equal("Import failed: Girokonto", told.GetProperty("title").GetString());
        Assert.Contains("answered 500", told.GetProperty("text").GetString());
        Assert.Equal("https://bento.example/data/transactions/import", told.GetProperty("link").GetString());

        await imports.RunScheduledAsync(sourceId, CancellationToken.None);
        Assert.Single(receiver.Requests);

        status = 200;
        await imports.RunScheduledAsync(sourceId, CancellationToken.None);
        status = 500;
        await imports.RunScheduledAsync(sourceId, CancellationToken.None);
        Assert.Equal(2, receiver.Requests.Count);
    }

    [Fact]
    public async Task Rules_AreChecked()
    {
        await using var host = await StartAsync();
        await host.ApplySchemaAsync(Schema);
        var channel = await PostAsync(host, "/api/v1/admin/notifications/channels", new { name = "Hook", kind = "webhook", config = new { url = "https://example.com/hook" } });
        var channels = new[] { channel.GetProperty("id").GetInt64() };
        object Condition(object config) => new { name = "R", kind = "condition", config, channels };

        Assert.Contains("Column 'amout' not found", await FailAsync(host, Condition(new { source = "transactions", formula = "SUM(amout)", op = ">", threshold = 1 })));
        Assert.Contains("date column", await FailAsync(host, Condition(new { source = "transactions", formula = "SUM(amount)", period = "this-month", op = ">", threshold = 1 })));
        Assert.Contains("Compare with", await FailAsync(host, Condition(new { source = "transactions", formula = "SUM(amount)", op = "~", threshold = 1 })));
        Assert.Contains("HH:mm", await FailAsync(host, Condition(new { source = "transactions", formula = "SUM(amount)", op = ">", threshold = 1, every = "day", at = "8" })));
        Assert.Contains("at least one channel", await FailAsync(host, new { name = "R", kind = "event", config = new { @event = "import.failed" }, channels = Array.Empty<long>() }));
        Assert.Contains("no dashboard", await FailAsync(host, new { name = "R", kind = "budget", config = new { dashboardId = 99, widgetId = "x" }, channels }));
    }

    [Theory]
    [InlineData(null, "EUR", "€1,250.50", "-€3.20", "1,234.5", "12.5%")]
    [InlineData("de-DE", "EUR", "1.250,50\u00A0€", "-3,20\u00A0€", "1.234,5", "12,5 %")]
    [InlineData("de-AT", "EUR", "€\u00A01.250,50", "-€\u00A03,20", "1.234,5", "12,5 %")]
    [InlineData("de-CH", "CHF", "CHF\u00A01’250.50", "-CHF\u00A03.20", "1’234.5", "12.5 %")]
    [InlineData("fr-FR", "EUR", "1\u202F250,50\u00A0€", "-3,20\u00A0€", "1\u202F234,5", "12,5 %")]
    public void Numbers_AreWrittenTheWorkspacesWay_WithoutCultureData(string? locale, string currency, string amount, string negative, string number, string percent)
    {
        var workspace = new Bento.Api.Services.WorkspaceSettings.Values(locale, currency);
        string Unescape(string text) => System.Text.RegularExpressions.Regex.Unescape(text);
        Assert.Equal(Unescape(amount), MessageFormat.Format(1250.5m, "currency", workspace));
        Assert.Equal(Unescape(negative), MessageFormat.Format(-3.2m, "currency", workspace));
        Assert.Equal(Unescape(number), MessageFormat.Format(1234.5m, "number", workspace));
        Assert.Equal(Unescape(percent), MessageFormat.Format(0.125m, "percent", workspace));
        // Whole numbers: grouped like the rest, no decimals.
        Assert.Equal(Unescape(number).Replace("234,5", "000").Replace("234.5", "000").Replace("1", "12"), MessageFormat.Format(12000, "integer", workspace));
    }

    // ── Helpers ──

    private static Task<JsonElement> PostAsync(ApiIntegrationTestHost host, string url, object body) => ImportSourceTests.PostAsync(host, url, body);

    private static async Task<string> FailAsync(ApiIntegrationTestHost host, object body)
    {
        using var response = await host.Client.PostAsJsonAsync(Path, body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }
}

[Trait("Category", "Postgres")]
[Collection(PostgresCollection.Name)]
public sealed class PostgresNotificationRuleTests
{
    private static Task<ApiIntegrationTestHost> StartAsync() => ApiIntegrationTestHost.StartPostgresAsync(new Dictionary<string, string?>
    {
        ["Backend:AuthMode"] = "open",
        ["Backend:Jwt:Secret"] = "postgres-test-secret",
        ["Backend:PublicUrl"] = "https://bento.example",
        ["Schema:AutoMigrateOnStartup"] = "false",
        ["Import:AllowPrivateNetwork"] = "true"
    });

    [PostgresFact]
    public async Task Condition_OnPostgres() => await NotificationRuleTests.ConditionAsync(await StartAsync());

    [PostgresFact]
    public async Task ImportEvent_OnPostgres() => await NotificationRuleTests.ImportEventAsync(await StartAsync());
}
