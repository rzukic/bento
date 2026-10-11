using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Bento.Api.Services.Import;

namespace Bento.Tests;

/// <summary>
/// Step 5.1: a bank export fetched from a URL (with a credential from the
/// vault), saved as an import that runs now or on a schedule, categorises new
/// rows by the rules, updates rows that changed, and logs failures.
/// </summary>
public sealed class ImportSourceTests
{
    private const string Statement = """
        "Buchungstag";"Auftraggeber / Begünstigter";"Verwendungszweck";"Betrag (EUR)"
        "01.09.2026";"REWE Markt";"Einkauf";"-45,90"
        "02.09.2026";"Arbeitgeber GmbH";"Gehalt September";"3.250,00"
        "03.09.2026";"Bäckerei Müller";"Kaffee";"-3,20"
        """;

    private static readonly object[] Mapping =
    [
        new { source = "Buchungstag", target = "booked_on" },
        new { source = "Auftraggeber / Begünstigter", target = "counterparty" },
        new { source = "Verwendungszweck", target = "purpose" },
        new { source = "Betrag (EUR)", target = "amount" }
    ];

    private static Task<ApiIntegrationTestHost> StartAsync(bool allowPrivate = true) => ImportFormatTests.CreateHostAsync(ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?>
    {
        ["Backend:AuthMode"] = "open",
        ["Backend:Jwt:Secret"] = "sqlite-test-secret",
        ["Schema:AutoMigrateOnStartup"] = "false",
        ["Import:AllowPrivateNetwork"] = allowPrivate ? "true" : "false"
    }));

    [Fact]
    public async Task Fetch_GetsTheFileForThePage_WithACredentialOnlyForItsHost()
    {
        await using var host = await StartAsync();
        using var bank = new FakeQuoteServer(request => request.Headers["Authorization"] == "Bearer s3cret"
            ? (200, Statement, null)
            : (401, "{}", null));
        await PostAsync(host, "/api/v1/admin/credentials", new { name = "bank_token", value = "s3cret" });
        var headers = new[] { new { name = "Authorization", value = "Bearer {{bank_token}}" } };

        var fetched = await PostAsync(host, "/api/v1/import/fetch", new { url = $"{bank.Url}/export/girokonto.csv?from=2026-09-01", headers });
        Assert.Equal("girokonto.csv", fetched.GetProperty("fileName").GetString());
        Assert.Equal(Statement, Encoding.UTF8.GetString(Convert.FromBase64String(fetched.GetProperty("content").GetString()!)));

        // The page reads it like an upload.
        var analyzed = await PostAsync(host, "/api/v1/import/analyze", new { content = fetched.GetProperty("content").GetString(), fileName = "girokonto.csv" });
        Assert.Equal(3, analyzed.GetProperty("rowCount").GetInt32());

        var refused = await FailAsync(host, "/api/v1/import/fetch", new { url = $"{bank.Url}/export/girokonto.csv" }, 422);
        Assert.Contains("refused the request (401)", refused);
        var missing = await FailAsync(host, "/api/v1/import/fetch", new { url = $"{bank.Url}/x.csv", headers = new[] { new { name = "Authorization", value = "{{nope}}" } } }, 422);
        Assert.Contains("'nope' is not stored", missing);
        Assert.Contains("set by Bento", await FailAsync(host, "/api/v1/import/fetch", new { url = $"{bank.Url}/x.csv", headers = new[] { new { name = "Cookie", value = "a=b" } } }, 400));
    }

    [Fact]
    public async Task Fetch_OnlyCallsPublicHttpsAddresses()
    {
        await using var host = await StartAsync(allowPrivate: false);
        Assert.Contains("Only https", await FailAsync(host, "/api/v1/import/fetch", new { url = "http://example.com/a.csv" }, 400));
        Assert.Contains("not a public address", await FailAsync(host, "/api/v1/import/fetch", new { url = "https://127.0.0.1:9/a.csv" }, 422));
        Assert.Contains("not in the address", await FailAsync(host, "/api/v1/import/fetch", new { url = "https://user:pw@example.com/a.csv" }, 400));
    }

    [Fact]
    public async Task SavedImport_RunsNowAndOnSchedule() => await SavedImportAsync(await StartAsync());

    internal static async Task SavedImportAsync(ApiIntegrationTestHost host)
    {
        await using var _ = host;
        await ImportApiTests.SeedCategoriesAsync(host);
        // A rule from an earlier import: REWE is Lebensmittel (id 1).
        await PostAsync(host, "/api/v1/categorise/transactions/rules", new { rules = new[] { new { column = "category_id", matchColumn = "counterparty", pattern = "REWE", value = "1" } } });

        var statement = Statement;
        var status = 200;
        using var bank = new FakeQuoteServer(_ => (status, statement, null));
        var created = await PostAsync(host, "/api/v1/import/transactions/sources", new
        {
            name = "Girokonto",
            config = new
            {
                url = $"{bank.Url}/girokonto.csv",
                mapping = Mapping,
                keyColumns = new[] { "booked_on", "counterparty", "purpose" },
                updateChanged = true,
                categoryColumn = "category_id",
                schedule = new { kind = "hours", every = 24 }
            }
        });
        var id = created.GetProperty("id").GetInt64();
        Assert.Equal(JsonValueKind.Null, created.GetProperty("lastRun").ValueKind);
        // Never run on its schedule: due now.
        Assert.True(DateTimeOffset.Parse(created.GetProperty("nextRunAt").GetString()!) <= DateTimeOffset.UtcNow.AddSeconds(5));

        var first = await PostAsync(host, $"/api/v1/import/transactions/sources/{id}/run", new { });
        Assert.Equal("ok", first.GetProperty("status").GetString());
        Assert.Equal("manual", first.GetProperty("trigger").GetString());
        Assert.Equal(3, first.GetProperty("inserted").GetInt32());
        Assert.Equal(1, first.GetProperty("categorised").GetInt32());
        var rows = await ImportApiTests.ListAsync(host);
        Assert.Equal(1, rows[0].GetProperty("category_id").GetInt32());
        Assert.Equal(JsonValueKind.Null, rows[1].GetProperty("category_id").ValueKind);

        // The bank corrects REWE's amount; the scheduler finds the import due and updates the row.
        statement = statement.Replace("\"-45,90\"", "\"-46,10\"");
        var service = host.GetService<ImportSourceService>();
        Assert.Contains(id, await service.DueAsync());
        var scheduled = await service.RunScheduledAsync(id, CancellationToken.None);
        Assert.Equal(("ok", 0, 1, 2), (scheduled.Status, scheduled.Inserted, scheduled.Updated, scheduled.Duplicates));
        Assert.DoesNotContain(id, await service.DueAsync());
        rows = await ImportApiTests.ListAsync(host);
        Assert.Equal(-46.1m, rows[0].GetProperty("amount").GetDecimal());
        Assert.Equal(1, rows[0].GetProperty("category_id").GetInt32());

        // A failing bank: the run says why, nothing is written.
        status = 500;
        var failed = await PostAsync(host, $"/api/v1/import/transactions/sources/{id}/run", new { });
        Assert.Equal("failed", failed.GetProperty("status").GetString());
        Assert.Contains("answered 500", failed.GetProperty("error").GetString());
        Assert.Equal(3, (await ImportApiTests.ListAsync(host)).Count);
        var problem = (await host.Client.GetFromJsonAsync<JsonElement>("/api/v1/admin/problems")).GetProperty("data")
            .EnumerateArray().Single(p => p.GetProperty("kind").GetString() == "import");
        Assert.Contains("'Girokonto' into transactions failed (manual)", problem.GetProperty("message").GetString());

        var listed = await host.Client.GetFromJsonAsync<JsonElement>("/api/v1/import/transactions/sources");
        var source = listed.GetProperty("data")[0];
        Assert.Equal("failed", source.GetProperty("lastRun").GetProperty("status").GetString());
        var runs = (await host.Client.GetFromJsonAsync<JsonElement>($"/api/v1/import/transactions/sources/{id}/runs")).GetProperty("data");
        Assert.Equal(["manual", "schedule", "manual"], runs.EnumerateArray().Select(r => r.GetProperty("trigger").GetString()));

        // Turned off, it is never due.
        using (var off = await host.Client.PutAsJsonAsync($"/api/v1/import/transactions/sources/{id}", new { enabled = false }))
            off.EnsureSuccessStatusCode();
        Assert.Equal(JsonValueKind.Null, (await host.Client.GetFromJsonAsync<JsonElement>($"/api/v1/import/transactions/sources/{id}")).GetProperty("data").GetProperty("nextRunAt").ValueKind);
        using (var deleted = await host.Client.DeleteAsync($"/api/v1/import/transactions/sources/{id}"))
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
    }

    [Fact]
    public async Task SavedImport_ChecksItsSchedule_AndCredentialsNeedAnAdmin()
    {
        await using var host = await ImportFormatTests.CreateHostAsync(ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Backend:AuthMode"] = "standard",
            ["Backend:Jwt:Secret"] = "super-secret",
            ["Backend:AuthRateLimitPerMinute"] = "100",
            ["Schema:AutoMigrateOnStartup"] = "false"
        }));
        using (var register = await host.Client.PostAsJsonAsync("/api/v1/auth/register", new { username = "owner", password = "supersecure" }))
            Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        var admin = await LoginAsync(host, "owner", "supersecure");
        await SendAsync(host, admin, HttpMethod.Post, "/api/v1/auth/register", new { username = "editor", password = "anotherpass", role = "editor" }, HttpStatusCode.Created);
        await SendAsync(host, admin, HttpMethod.Post, "/api/v1/admin/credentials", new { name = "bank_token", value = "s3cret" }, HttpStatusCode.Created);
        var editor = await LoginAsync(host, "editor", "anotherpass");

        object Body(object schedule, string header) => new
        {
            name = "Depot",
            config = new
            {
                url = "https://bank.example/export.csv",
                headers = new[] { new { name = "Authorization", value = header } },
                mapping = Mapping,
                schedule
            }
        };
        const string path = "/api/v1/import/transactions/sources";
        Assert.Contains("HH:mm", await SendAsync(host, editor, HttpMethod.Post, path, Body(new { kind = "daily", at = "6 Uhr" }, "Bearer plain"), HttpStatusCode.BadRequest));
        Assert.Contains("1 to 168", await SendAsync(host, editor, HttpMethod.Post, path, Body(new { kind = "hours", every = 0 }, "Bearer plain"), HttpStatusCode.BadRequest));
        Assert.Contains("Only admins", await SendAsync(host, editor, HttpMethod.Post, path, Body(new { kind = "daily", at = "06:30" }, "Bearer {{bank_token}}"), HttpStatusCode.Forbidden));

        var saved = await SendAsync(host, admin, HttpMethod.Post, path, Body(new { kind = "daily", at = "06:30" }, "Bearer {{bank_token}}"), HttpStatusCode.Created);
        var source = JsonDocument.Parse(saved).RootElement.GetProperty("data");
        Assert.Equal("Europe/Berlin", source.GetProperty("config").GetProperty("schedule").GetProperty("timeZone").GetString());
        // The next 06:30 in Berlin.
        var next = DateTimeOffset.Parse(source.GetProperty("nextRunAt").GetString()!);
        var berlin = TimeZoneInfo.ConvertTime(next, TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin"));
        Assert.Equal(new TimeSpan(6, 30, 0), berlin.TimeOfDay);
        Assert.True(next <= DateTimeOffset.UtcNow.AddDays(1));

        Assert.Contains("only admins can change it", await SendAsync(host, editor, HttpMethod.Put, $"{path}/{source.GetProperty("id")}", new { name = "Mine" }, HttpStatusCode.Forbidden));
    }

    // ── Helpers ──

    internal static async Task<JsonElement> PostAsync(ApiIntegrationTestHost host, string url, object body)
    {
        using var response = await host.Client.PostAsJsonAsync(url, body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.GetProperty("data").Clone();
    }

    private static async Task<string> FailAsync(ApiIntegrationTestHost host, string url, object body, int status)
    {
        using var response = await host.Client.PostAsJsonAsync(url, body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True((int)response.StatusCode == status, $"{(int)response.StatusCode}: {text}");
        return text;
    }

    private static async Task<string> SendAsync(ApiIntegrationTestHost host, string token, HttpMethod method, string url, object body, HttpStatusCode expected)
    {
        using var request = new HttpRequestMessage(method, url) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await host.Client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text;
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
public sealed class PostgresImportSourceTests
{
    [PostgresFact]
    public async Task SavedImport_RunsNowAndOnSchedule_OnPostgres() =>
        await ImportSourceTests.SavedImportAsync(await ImportFormatTests.CreateHostAsync(ApiIntegrationTestHost.StartPostgresAsync(new Dictionary<string, string?>
        {
            ["Backend:AuthMode"] = "open",
            ["Backend:Jwt:Secret"] = "postgres-test-secret",
            ["Schema:AutoMigrateOnStartup"] = "false",
            ["Import:AllowPrivateNetwork"] = "true"
        })));
}
