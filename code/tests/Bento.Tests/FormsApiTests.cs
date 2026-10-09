using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Bento.Tests;

/// <summary>
/// Step 4.4: quick entry forms — Form JSON checked against its table, stored
/// private or shared, followed through renames. A filled form is an ordinary
/// row created through the data API. The scenario runs on Postgres too.
/// </summary>
public sealed class FormsApiTests
{
    private static object AddTransaction(object? extra = null) => new
    {
        table = "transactions",
        pinned = true,
        fields = new object[]
        {
            new { column = "amount", label = "Amount", sign = "negative" },
            new { column = "booked_on", label = "Date", @default = new { kind = "today" } },
            new { section = "Details" },
            new { column = "counterparty", help = "Who you paid" },
            new { column = "category_id", @default = new { kind = "last" } },
            new { column = "quantity", hidden = true, @default = new { kind = "value", value = 1 } }
        }
    };

    internal static async Task RunAsync(ApiIntegrationTestHost host)
    {
        await host.ApplySchemaAsync(AggregationQueryTests.Schema);

        var created = await SendAsync(host, HttpMethod.Post, "/api/v1/forms", new { name = "Add transaction", isShared = true, form = AddTransaction() }, HttpStatusCode.Created);
        var id = created.GetProperty("id").GetInt64();
        Assert.Equal("transactions", created.GetProperty("table").GetString());
        Assert.True(created.GetProperty("isOwner").GetBoolean());
        var fields = created.GetProperty("form").GetProperty("fields");
        Assert.Equal(6, fields.GetArrayLength());
        Assert.Equal("negative", fields[0].GetProperty("sign").GetString());
        Assert.Equal("Details", fields[2].GetProperty("section").GetString());
        Assert.True(created.GetProperty("form").GetProperty("pinned").GetBoolean());

        // Column names are matched without case and stored as the table spells them.
        var renamed = await SendAsync(host, HttpMethod.Put, $"/api/v1/forms/{id}", new
        {
            name = "Expense",
            form = new { table = "Transactions", fields = new object[] { new { column = "AMOUNT" }, new { column = "booked_on" } } }
        }, HttpStatusCode.OK);
        Assert.Equal("Expense", renamed.GetProperty("name").GetString());
        Assert.Equal("amount", renamed.GetProperty("form").GetProperty("fields")[0].GetProperty("column").GetString());
        Assert.True(renamed.GetProperty("isShared").GetBoolean());
        await SendAsync(host, HttpMethod.Put, $"/api/v1/forms/{id}", new { name = "Add transaction", form = AddTransaction() }, HttpStatusCode.OK);

        // What a form cannot be.
        foreach (var (form, message) in new (object Form, string Message)[]
        {
            (new { table = "nope", fields = new[] { new { column = "x" } } }, "There is no table 'nope'."),
            (new { table = "transactions", fields = Array.Empty<object>() }, "A form needs at least one field."),
            (new { table = "transactions", fields = new object[] { new { column = "booked_on" } } }, "transactions needs a value for amount; add it to the form."),
            (new { table = "transactions", fields = new object[] { new { column = "amount" }, new { column = "booked_on" }, new { column = "id" } } }, "id is computed or assigned by the database; a form cannot fill it."),
            (new { table = "transactions", fields = new object[] { new { column = "amount" }, new { column = "booked_on" }, new { column = "amount" } } }, "amount is in the form twice."),
            (new { table = "transactions", fields = new object[] { new { column = "amount" }, new { column = "booked_on" }, new { column = "memo" } } }, "transactions has no column 'memo'."),
            (new { table = "transactions", fields = new object[] { new { column = "amount" }, new { column = "booked_on", hidden = true } } }, "A hidden field needs a default (today, now or a fixed value): booked_on."),
            (new { table = "transactions", fields = new object[] { new { column = "amount" }, new { column = "booked_on" }, new { column = "counterparty", @default = new { kind = "today" } } } }, "counterparty is not a date; it cannot default to today."),
            (new { table = "transactions", fields = new object[] { new { column = "amount" }, new { column = "booked_on" }, new { column = "counterparty", sign = "negative" } } }, "counterparty is not a number; it has no sign."),
            (new { table = "transactions", fields = new object[] { new { column = "amount" }, new { column = "booked_on", @default = new { kind = "yesterday" } } } }, "default.kind is today, now, last (the value used last time) or value."),
        })
        {
            var error = await SendAsync(host, HttpMethod.Post, "/api/v1/forms", new { name = "Bad", form }, HttpStatusCode.BadRequest);
            Assert.Equal(message, error.GetProperty("message").GetString());
        }
        var unnamed = await SendAsync(host, HttpMethod.Post, "/api/v1/forms", new { name = " ", form = AddTransaction() }, HttpStatusCode.BadRequest);
        Assert.Equal("name", unnamed.GetProperty("details").GetProperty("field").GetString());

        // A filled form is a row through the data API, validated as usual.
        (await host.Client.PostAsJsonAsync("/api/v1/data/categories", new { name = "Groceries" })).EnsureSuccessStatusCode();
        using (var row = await host.Client.PostAsJsonAsync("/api/v1/data/transactions", new { amount = -12.5m, booked_on = "2026-10-07", counterparty = "Bakery", category_id = 1, quantity = 1 }))
            Assert.Equal(HttpStatusCode.Created, row.StatusCode);

        // Renaming the table and a column follows into the form.
        await host.ApplySchemaAsync(AggregationQueryTests.Schema
            .Replace("\"name\": \"transactions\",", "\"name\": \"bookings\", \"renamedFrom\": \"transactions\",")
            .Replace("\"to\": \"transactions\"", "\"to\": \"bookings\"")
            .Replace("{ \"name\": \"booked_on\", \"type\": \"date\", \"nullable\": false }", "{ \"name\": \"booked\", \"renamedFrom\": \"booked_on\", \"type\": \"date\", \"nullable\": false }"));
        var reloaded = (await host.Client.GetFromJsonAsync<JsonElement>($"/api/v1/forms/{id}")).GetProperty("data");
        Assert.Equal("bookings", reloaded.GetProperty("table").GetString());
        Assert.Equal("bookings", reloaded.GetProperty("form").GetProperty("table").GetString());
        Assert.Equal("booked", reloaded.GetProperty("form").GetProperty("fields")[1].GetProperty("column").GetString());

        var list = (await host.Client.GetFromJsonAsync<JsonElement>("/api/v1/forms")).GetProperty("data");
        Assert.Equal("Add transaction", Assert.Single(list.EnumerateArray()).GetProperty("name").GetString());

        using (var deleted = await host.Client.DeleteAsync($"/api/v1/forms/{id}"))
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        using (var gone = await host.Client.GetAsync($"/api/v1/forms/{id}"))
            Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task Forms_OnSqlite()
    {
        await using var host = await ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?> { ["Backend:AuthMode"] = "open", ["Schema:AutoMigrateOnStartup"] = "false" });
        await RunAsync(host);
    }

    [Fact]
    public async Task SharedForms_AreVisible_ViewersCannotWriteThem()
    {
        await using var host = await ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Backend:AuthMode"] = "standard",
            ["Backend:Jwt:Secret"] = "super-secret",
            ["Backend:AuthRateLimitPerMinute"] = "100",
            ["Schema:AutoMigrateOnStartup"] = "false"
        });
        await host.ApplySchemaAsync(AggregationQueryTests.Schema);
        (await host.Client.PostAsJsonAsync("/api/v1/auth/register", new { username = "bootstrap-admin", password = "supersecure" })).EnsureSuccessStatusCode();
        var admin = await LoginAsync(host, "bootstrap-admin", "supersecure");
        foreach (var (username, role) in new[] { ("viewer", "viewer"), ("editor", "editor") })
        {
            using var register = Request(HttpMethod.Post, "/api/v1/auth/register", admin, new { username, password = "anotherpass", role });
            (await host.Client.SendAsync(register)).EnsureSuccessStatusCode();
        }
        var viewer = await LoginAsync(host, "viewer", "anotherpass");
        var editor = await LoginAsync(host, "editor", "anotherpass");

        long shared;
        using (var create = Request(HttpMethod.Post, "/api/v1/forms", admin, new { name = "Add transaction", isShared = true, form = AddTransaction() }))
            shared = (await (await host.Client.SendAsync(create)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetProperty("id").GetInt64();
        using (var create = Request(HttpMethod.Post, "/api/v1/forms", admin, new { name = "Private", form = AddTransaction() }))
            (await host.Client.SendAsync(create)).EnsureSuccessStatusCode();

        using (var list = Request(HttpMethod.Get, "/api/v1/forms", viewer))
        {
            var names = (await (await host.Client.SendAsync(list)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data")
                .EnumerateArray().Select(f => f.GetProperty("name").GetString());
            Assert.Equal(["Add transaction"], names);
        }
        using (var own = Request(HttpMethod.Post, "/api/v1/forms", viewer, new { name = "Viewer's", form = AddTransaction() }))
            Assert.Equal(HttpStatusCode.Forbidden, (await host.Client.SendAsync(own)).StatusCode);
        using (var own = Request(HttpMethod.Post, "/api/v1/forms", editor, new { name = "Editor's", form = AddTransaction() }))
            Assert.Equal(HttpStatusCode.Created, (await host.Client.SendAsync(own)).StatusCode);
        using (var change = Request(HttpMethod.Put, $"/api/v1/forms/{shared}", editor, new { name = "Hijacked" }))
            Assert.Equal(HttpStatusCode.Forbidden, (await host.Client.SendAsync(change)).StatusCode);
    }

    // ── Helpers ──

    private static async Task<JsonElement> SendAsync(ApiIntegrationTestHost host, HttpMethod method, string url, object body, HttpStatusCode expected)
    {
        using var request = new HttpRequestMessage(method, url) { Content = JsonContent.Create(body) };
        using var response = await host.Client.SendAsync(request);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {payload}");
        return payload.TryGetProperty("error", out var error) ? error : payload.GetProperty("data");
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
}

[Trait("Category", "Postgres")]
[Collection(PostgresCollection.Name)]
public sealed class PostgresFormsApiTests
{
    [PostgresFact]
    public async Task Forms_OnPostgres()
    {
        await using var host = await ApiIntegrationTestHost.StartPostgresAsync(new Dictionary<string, string?>
        {
            ["Backend:AuthMode"] = "open",
            ["Backend:Jwt:Secret"] = "postgres-test-secret",
            ["Schema:AutoMigrateOnStartup"] = "false"
        });
        await FormsApiTests.RunAsync(host);
    }
}
