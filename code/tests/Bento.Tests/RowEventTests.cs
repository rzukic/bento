using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Bento.Api.Services;

namespace Bento.Tests;

/// <summary>
/// Step 5.12: rules on rows — added, changed (in some columns), deleted;
/// with a condition and an origin; a batch is one message.
/// </summary>
public sealed class RowEventTests
{
    private const string Rules = "/api/v1/admin/notifications/rules";

    private const string Schema = """
        { "tables": [
            { "name": "customers", "columns": [
                { "name": "id", "type": "integer", "primaryKey": true },
                { "name": "name", "type": "text", "nullable": false } ] },
            { "name": "orders", "displayName": "Orders", "recordLabel": { "column": "title" }, "columns": [
                { "name": "id", "type": "integer", "primaryKey": true },
                { "name": "title", "displayName": "Title", "type": "text", "nullable": false },
                { "name": "customer_id", "displayName": "Customer", "type": "reference", "references": "customers" },
                { "name": "total", "displayName": "Total", "type": "currency" },
                { "name": "status", "displayName": "Status", "type": "enum", "values": ["open", "urgent", "done"] },
                { "name": "note", "type": "text" } ] } ] }
        """;

    [Fact]
    public async Task RowRules_SpeakAboutTheRowsTheyWatch() => await RowRulesAsync(await StartAsync());

    internal static Task<ApiIntegrationTestHost> StartAsync() => ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?>
    {
        ["Backend:AuthMode"] = "open",
        ["Backend:Jwt:Secret"] = "sqlite-test-secret",
        ["Backend:PublicUrl"] = "https://bento.example",
        ["Schema:AutoMigrateOnStartup"] = "false"
    });

    internal static async Task RowRulesAsync(ApiIntegrationTestHost host)
    {
        await using var _ = host;
        await host.ApplySchemaAsync(Schema);
        using var receiver = new FakeReceiver();
        var channel = (await PostAsync(host, "/api/v1/admin/notifications/channels", new { name = "Hook", kind = "webhook", config = new { url = $"{receiver.Url}/hook" } })).GetProperty("id").GetInt64();
        var events = host.GetService<RowEvents>();
        var acme = (await PostAsync(host, "/api/v1/data/customers", new { name = "Acme" })).GetProperty("id").GetInt64();

        // What a rule may watch.
        object Rule(string name, object config) => new { name, kind = "event", config, channels = new[] { channel } };
        Assert.Contains("no condition", await FailAsync(host, Rules, Rule("x", new { @event = "row.deleted", source = "orders", formula = "total > 1" })));
        Assert.Contains("Only a rule on changed rows", await FailAsync(host, Rules, Rule("x", new { @event = "row.created", source = "orders", columns = new[] { "status" } })));
        Assert.Contains("no stored column 'nope'", await FailAsync(host, Rules, Rule("x", new { @event = "row.updated", source = "orders", columns = new[] { "nope" } })));
        Assert.Contains("'fax' is no origin", await FailAsync(host, Rules, Rule("x", new { @event = "row.created", source = "orders", origins = new[] { "fax" } })));
        Assert.Contains("nope", await FailAsync(host, Rules, Rule("x", new { @event = "row.created", source = "orders", formula = "nope = 1" })));

        var urgent = await PostAsync(host, Rules, Rule("Urgent order", new { @event = "row.created", source = "orders", formula = "status = \"urgent\"" }));
        Assert.Equal("A row is added to Orders where status = \"urgent\"", urgent.GetProperty("summary").GetString());
        var statusChanged = await PostAsync(host, Rules, Rule("Status changed", new { @event = "row.updated", source = "orders", columns = new[] { "status" } }));
        var deleted = await PostAsync(host, Rules, Rule("Order deleted", new { @event = "row.deleted", source = "orders" }));
        await PostAsync(host, Rules, Rule("From the form", new { @event = "row.created", source = "orders", origins = new[] { "form" } }));

        // One urgent order of two: one message, with its values; the link opens the row.
        var first = (await PostAsync(host, "/api/v1/data/orders", new { title = "Rush job", customer_id = acme, total = 1250.5, status = "urgent" })).GetProperty("id").GetInt64();
        await PostAsync(host, "/api/v1/data/orders", new { title = "Slow job", total = 10, status = "open" });
        await events.FlushAsync();
        var message = Single(receiver);
        Assert.Equal("Urgent order", message.GetProperty("title").GetString());
        Assert.Equal("New in Orders: Rush job — by open-mode-admin.", message.GetProperty("text").GetString());
        Assert.Equal($"https://bento.example/data/orders?row={first}", message.GetProperty("link").GetString());
        var facts = Facts(message);
        Assert.Equal("Acme", facts["Customer"]);
        Assert.Equal("€1,250.50", facts["Total"]);
        Assert.Equal("urgent", facts["Status"]);

        // Many at once (bulk): one message for all of them.
        receiver.Requests.Clear();
        using (var bulk = await host.Client.PostAsJsonAsync("/api/v1/data/orders/bulk", new { rows = Enumerable.Range(1, 7).Select(i => new { title = $"Batch {i}", status = "urgent" }).ToArray() }))
            Assert.True(bulk.IsSuccessStatusCode, await bulk.Content.ReadAsStringAsync());
        await events.FlushAsync();
        message = Single(receiver);
        Assert.Equal("7 new rows in Orders — by open-mode-admin.", message.GetProperty("text").GetString());
        Assert.Equal("and 2 more", Facts(message)["…"]);

        // A change of the note alone is not watched; of the status, it says from what to what.
        receiver.Requests.Clear();
        await PatchAsync(host, $"/api/v1/data/orders/{first}", new { note = "call first" });
        await events.FlushAsync();
        Assert.Empty(receiver.Requests);
        await PatchAsync(host, $"/api/v1/data/orders/{first}", new { status = "done" });
        await events.FlushAsync();
        message = Single(receiver);
        Assert.Equal("Status changed", message.GetProperty("title").GetString());
        Assert.Equal("urgent → done", Facts(message)["Status"]);

        // Deleted: what it was.
        receiver.Requests.Clear();
        using (var delete = await host.Client.DeleteAsync($"/api/v1/data/orders/{first}"))
            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        await events.FlushAsync();
        message = Single(receiver);
        Assert.Equal("Deleted from Orders: Rush job — by open-mode-admin.", message.GetProperty("text").GetString());
        Assert.Equal("€1,250.50", Facts(message)["Total"]);

        // An import is an origin of its own.
        receiver.Requests.Clear();
        var json = """[{ "title": "Imported", "status": "urgent" }]""";
        await PostAsync(host, "/api/v1/import/orders", new
        {
            content = Convert.ToBase64String(Encoding.UTF8.GetBytes(json)),
            fileName = "orders.json",
            mapping = new[] { new { source = "title", target = "title" }, new { source = "status", target = "status" } }
        });
        await events.FlushAsync();
        message = Single(receiver);
        Assert.Equal("New in Orders: Imported — by the import “orders.json”.", message.GetProperty("text").GetString());

        // A test shows the newest row as an example; the rule's state says when it spoke.
        receiver.Requests.Clear();
        var tested = await PostAsync(host, $"{Rules}/{deleted.GetProperty("id").GetInt64()}/test", new { });
        Assert.StartsWith("Test: Order deleted", Single(receiver).GetProperty("title").GetString());
        var state = (await host.Client.GetFromJsonAsync<JsonElement>($"{Rules}/{statusChanged.GetProperty("id").GetInt64()}")).GetProperty("data").GetProperty("state");
        Assert.Equal(JsonValueKind.String, state.GetProperty("firedAt").ValueKind);

        // At most 20 messages a rule an hour.
        receiver.Requests.Clear();
        for (var i = 0; i < 22; i++)
        {
            await PostAsync(host, "/api/v1/data/orders", new { title = $"Flood {i}", status = "urgent" });
            await events.FlushAsync();
        }
        Assert.Equal(NotificationRulesLimit() - 3, receiver.Requests.Count(r => JsonDocument.Parse(r.Body).RootElement.GetProperty("title").GetString() == "Urgent order"));
        var quiet = (await host.Client.GetFromJsonAsync<JsonElement>($"{Rules}/{urgent.GetProperty("id").GetInt64()}")).GetProperty("data").GetProperty("state");
        Assert.Contains("quiet until the hour is over", quiet.GetProperty("error").GetString());
    }

    /// <summary>The urgent rule spoke 3 times before the flood (one order, the bulk, the import).</summary>
    private static int NotificationRulesLimit() => Bento.Api.Services.Notifications.NotificationRuleService.MaxRowMessagesPerHour;

    private static JsonElement Single(FakeReceiver receiver) => JsonDocument.Parse(Assert.Single(receiver.Requests).Body).RootElement;

    private static Dictionary<string, string> Facts(JsonElement message) =>
        message.GetProperty("facts").EnumerateArray().ToDictionary(f => f.GetProperty("label").GetString()!, f => f.GetProperty("value").GetString()!);

    private static async Task<JsonElement> PostAsync(ApiIntegrationTestHost host, string url, object body)
    {
        using var response = await host.Client.PostAsJsonAsync(url, body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{url} {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.GetProperty("data").Clone();
    }

    private static async Task PatchAsync(ApiIntegrationTestHost host, string url, object body)
    {
        using var response = await host.Client.PatchAsJsonAsync(url, body);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task<string> FailAsync(ApiIntegrationTestHost host, string url, object body)
    {
        using var response = await host.Client.PostAsJsonAsync(url, body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{(int)response.StatusCode}: {text}");
        return text;
    }
}

[Collection(PostgresCollection.Name)]
public sealed class PostgresRowEventTests
{
    [PostgresFact]
    public async Task RowRules_SpeakAboutTheRowsTheyWatch_OnPostgres() =>
        await RowEventTests.RowRulesAsync(await ApiIntegrationTestHost.StartPostgresAsync(new Dictionary<string, string?>
        {
            ["Backend:AuthMode"] = "open",
            ["Backend:Jwt:Secret"] = "postgres-test-secret",
            ["Backend:PublicUrl"] = "https://bento.example",
            ["Schema:AutoMigrateOnStartup"] = "false"
        }));
}
