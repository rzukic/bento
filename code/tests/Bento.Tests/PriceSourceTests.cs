using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Bento.Tests;

/// <summary>
/// Step 4.2 acceptance against a fake price API on 127.0.0.1: a credential, a
/// function that fetches prices with it, symbol suggestions, history and
/// latest prices written into the user's table, freshness and the daily budget.
/// The same scenario runs on Postgres (<see cref="PostgresPriceSourceTests"/>).
/// </summary>
public sealed class PriceSourceTests
{
    private const string Schema = """
        {
            "tables": [
                {
                    "name": "trades",
                    "columns": [
                        { "name": "id", "type": "integer", "primaryKey": true },
                        { "name": "isin", "type": "text" },
                        { "name": "shares", "type": "float" }
                    ]
                },
                {
                    "name": "prices",
                    "columns": [
                        { "name": "id", "type": "integer", "primaryKey": true },
                        { "name": "isin", "type": "text" },
                        { "name": "date", "type": "date" },
                        { "name": "price", "type": "currency" }
                    ]
                }
            ]
        }
        """;

    /// <summary>A fake provider: /lookup maps ISINs to symbols, /prices gives a price per symbol and day.</summary>
    private sealed class FakeProvider : IDisposable
    {
        public int PriceCalls;
        public readonly FakeQuoteServer Server;

        public FakeProvider()
        {
            Server = new FakeQuoteServer(request =>
            {
                if (request.Headers["X-Key"] != "s3cret-key")
                    return (401, """{"error":"bad key"}""", null);
                var query = System.Web.HttpUtility.ParseQueryString(request.Url!.Query);
                if (request.Url.AbsolutePath == "/lookup")
                {
                    var map = (query["isins"] ?? "").Split(',').ToDictionary(i => i, i => i switch
                    {
                        "IE00BK5BQT80" => "VWCE.DE",
                        "IE00B4L5Y983" => "EUNL.DE",
                        _ => ""
                    });
                    return (200, JsonSerializer.Serialize(map), null);
                }
                var call = Interlocked.Increment(ref PriceCalls);
                var from = DateOnly.Parse(query["from"]!, CultureInfo.InvariantCulture);
                var to = DateOnly.Parse(query["to"]!, CultureInfo.InvariantCulture);
                var rows = new List<object>();
                foreach (var symbol in (query["symbols"] ?? "").Split(','))
                {
                    var basis = symbol == "VWCE.DE" ? 100m : 50m;
                    for (var day = from; day <= to; day = day.AddDays(1))
                    {
                        if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                            continue;
                        // Today's price moves with every call, like a live quote.
                        var price = day == to ? basis + 10 + call : basis + day.DayNumber % 7;
                        rows.Add(new { s = symbol, d = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), p = price });
                    }
                }
                return (200, JsonSerializer.Serialize(rows), null);
            });
        }

        public void Dispose() => Server.Dispose();
    }

    internal static async Task RunAsync(ApiIntegrationTestHost host)
    {
        using var provider = new FakeProvider();
        await host.ApplySchemaAsync(Schema);
        foreach (var (isin, shares) in new[] { ("IE00BK5BQT80", 10.0), ("IE00B4L5Y983", 5.0), ("IE00BK5BQT80", 2.0) })
            await SendAsync(host, HttpMethod.Post, "/api/v1/data/trades", new { isin, shares }, HttpStatusCode.Created);

        // ── Credentials ──
        var created = await SendAsync(host, HttpMethod.Post, "/api/v1/admin/credentials", new { name = "quote_key", value = "s3cret-key", description = "Fake provider" }, HttpStatusCode.Created);
        Assert.True(created.GetProperty("readable").GetBoolean());
        var listed = await host.Client.GetStringAsync("/api/v1/admin/credentials");
        Assert.Contains("quote_key", listed);
        Assert.DoesNotContain("s3cret-key", listed);

        // ── Functions that call the provider ──
        var unknownCredential = await SendAsync(host, HttpMethod.Post, "/api/v1/functions", new
        {
            name = "broken", resultType = "any", code = "return [];", allowedHosts = new[] { "127.0.0.1" }, credentials = new[] { "nope" }
        }, HttpStatusCode.BadRequest);
        Assert.Equal("No credential named 'nope'; add it under Credentials first.", unknownCredential.GetProperty("message").GetString());
        var badHost = await SendAsync(host, HttpMethod.Post, "/api/v1/functions", new
        {
            name = "broken", resultType = "any", code = "return [];", allowedHosts = new[] { "https://example.com/x" }
        }, HttpStatusCode.BadRequest);
        Assert.Equal("allowedHosts[0]", badHost.GetProperty("details").GetProperty("field").GetString());

        var quotes = await SendAsync(host, HttpMethod.Post, "/api/v1/functions", new
        {
            name = "quotes",
            description = "Daily prices from the fake provider",
            arguments = new[] { new { name = "symbols", type = "any" }, new { name = "from", type = "date" }, new { name = "to", type = "date" } },
            resultType = "any",
            allowedHosts = new[] { "127.0.0.1" },
            credentials = new[] { "quote_key" },
            code = $$"""
                const r = await fetch(`{{provider.Server.Url}}/prices?symbols=${symbols.join(",")}&from=${from}&to=${to}`, { headers: { "X-Key": credentials.quote_key } });
                if (!r.ok) throw new Error("provider answered " + r.status);
                return (await r.json()).map(x => ({ symbol: x.s, date: x.d, price: x.p, time: x.d + "T15:30:00Z" }));
                """
        }, HttpStatusCode.Created);
        Assert.False(quotes.GetProperty("usableInFormulas").GetBoolean());
        Assert.Equal(["127.0.0.1"], quotes.GetProperty("allowedHosts").EnumerateArray().Select(h => h.GetString()));

        await SendAsync(host, HttpMethod.Post, "/api/v1/functions", new
        {
            name = "isin_lookup",
            arguments = new[] { new { name = "keys", type = "any" } },
            resultType = "any",
            allowedHosts = new[] { "127.0.0.1" },
            credentials = new[] { "quote_key" },
            code = $$"""
                const r = await fetch(`{{provider.Server.Url}}/lookup?isins=${keys.join(",")}`, { headers: { "X-Key": credentials.quote_key } });
                return await r.json();
                """
        }, HttpStatusCode.Created);

        // The editor's test run really calls the API and masks the key.
        var tested = await SendAsync(host, HttpMethod.Post, "/api/v1/functions/test", new
        {
            arguments = new[] { new { name = "symbols", type = "any" }, new { name = "from", type = "date" }, new { name = "to", type = "date" } },
            resultType = "any",
            allowedHosts = new[] { "127.0.0.1" },
            credentials = new[] { "quote_key" },
            code = $$"""
                console.log("using", credentials.quote_key);
                const r = await fetch(`{{provider.Server.Url}}/prices?symbols=${symbols.join(",")}&from=${from}&to=${to}`, { headers: { "X-Key": credentials.quote_key } });
                return (await r.json()).length;
                """,
            values = new object[] { new[] { "VWCE.DE" }, "2026-10-05", "2026-10-06" }
        });
        Assert.True(tested.GetProperty("error").ValueKind == JsonValueKind.Null, tested.ToString());
        Assert.Equal(2, tested.GetProperty("value").GetInt32());
        Assert.Equal(1, tested.GetProperty("requests").GetInt32());
        Assert.Equal(["using •••"], tested.GetProperty("logs").EnumerateArray().Select(l => l.GetString()));

        // Functions that call the internet do not run in formulas.
        var formula = await SendAsync(host, HttpMethod.Post, "/api/v1/formulas/validate", new { table = "trades", formula = "CUSTOM(\"isin_lookup\", isin)" });
        Assert.Equal("isin_lookup calls the internet; such functions run as price sources, not in formulas.", formula.GetProperty("errors")[0].GetProperty("message").GetString());

        // ── The price source ──
        var config = new Dictionary<string, object?>
        {
            ["function"] = "quotes",
            ["lookupFunction"] = "isin_lookup",
            ["table"] = "prices",
            ["keyColumn"] = "isin",
            ["dateColumn"] = "date",
            ["priceColumn"] = "price",
            ["keysFrom"] = "trades.isin",
            ["historyFrom"] = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-30).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["timeZone"] = "UTC"
        };
        var wrongColumn = await SendAsync(host, HttpMethod.Post, "/api/v1/price-sources", new
        {
            name = "Fake", config = new Dictionary<string, object?>(config) { ["priceColumn"] = "isin" }
        }, HttpStatusCode.BadRequest);
        Assert.Equal("'isin' is text; this needs currency or float.", wrongColumn.GetProperty("message").GetString());

        var source = await SendAsync(host, HttpMethod.Post, "/api/v1/price-sources", new { name = "Fake", config }, HttpStatusCode.Created);
        var id = source.GetProperty("id").GetInt64();
        Assert.Equal(["IE00B4L5Y983:IE00B4L5Y983:False", "IE00BK5BQT80:IE00BK5BQT80:False"], Keys(source));

        var suggestions = await SendAsync(host, HttpMethod.Post, $"/api/v1/price-sources/{id}/suggest", null);
        var symbols = suggestions.EnumerateArray().ToDictionary(s => s.GetProperty("key").GetString()!, s => s.GetProperty("symbol").GetString()!);
        Assert.Equal("VWCE.DE", symbols["IE00BK5BQT80"]);
        Assert.Equal("EUNL.DE", symbols["IE00B4L5Y983"]);
        source = await SendAsync(host, HttpMethod.Put, $"/api/v1/price-sources/{id}", new { config = new Dictionary<string, object?>(config) { ["symbols"] = symbols } });
        Assert.Equal(["IE00B4L5Y983:EUNL.DE:True", "IE00BK5BQT80:VWCE.DE:True"], Keys(source));

        // The daily run fetches each key's history and then the last days of all keys.
        var daily = await SendAsync(host, HttpMethod.Post, $"/api/v1/price-sources/{id}/run?kind=daily", null);
        Assert.True(daily.GetProperty("error").ValueKind == JsonValueKind.Null, daily.ToString());
        Assert.Equal(3, daily.GetProperty("requests").GetInt32());
        var weekdays = Enumerable.Range(0, 31).Select(i => DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-i)).Count(d => d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday));
        var prices = await PricesAsync(host);
        Assert.Equal(weekdays * 2, prices.Count);
        // History writes every day; the latest call then moves today's two prices.
        var isWeekday = DateTime.UtcNow.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);
        Assert.Equal(prices.Count + (isWeekday ? 2 : 0), daily.GetProperty("rowsWritten").GetInt32());
        var lastWeekday = Enumerable.Range(0, 7).Select(i => DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-i)).First(d => d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday));
        Assert.StartsWith(lastWeekday.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "T15:30:00", daily.GetProperty("lastQuoteAt").GetString());

        // A refresh within the freshness window makes no request; force does, and only today's prices change.
        var calls = provider.PriceCalls;
        var fresh = await SendAsync(host, HttpMethod.Post, "/api/v1/price-sources/refresh", new { tables = new[] { "prices" } });
        Assert.False(fresh.GetProperty("changed").GetBoolean());
        Assert.False(fresh.GetProperty("sources")[0].GetProperty("fetched").GetBoolean());
        Assert.Equal(calls, provider.PriceCalls);
        var forced = await SendAsync(host, HttpMethod.Post, "/api/v1/price-sources/refresh", new { tables = new[] { "prices" }, force = true });
        Assert.True(forced.GetProperty("sources")[0].GetProperty("fetched").GetBoolean());
        Assert.Equal(calls + 1, provider.PriceCalls);
        Assert.Equal(isWeekday ? 2 : 0, forced.GetProperty("sources")[0].GetProperty("rowsWritten").GetInt32());
        Assert.Equal(prices.Count, (await PricesAsync(host)).Count);
        var other = await SendAsync(host, HttpMethod.Post, "/api/v1/price-sources/refresh", new { tables = new[] { "trades" }, force = true });
        Assert.Empty(other.GetProperty("sources").EnumerateArray());

        // The source lists each key's latest price.
        source = await SendAsync(host, HttpMethod.Get, $"/api/v1/price-sources/{id}", null);
        var vwce = source.GetProperty("keys").EnumerateArray().Single(k => k.GetProperty("key").GetString() == "IE00BK5BQT80");
        Assert.NotNull(vwce.GetProperty("lastDate").GetString());
        Assert.Equal(4, source.GetProperty("status").GetProperty("requestsToday").GetInt32());

        // The daily budget stops fetching.
        await SendAsync(host, HttpMethod.Put, $"/api/v1/price-sources/{id}", new { config = new Dictionary<string, object?>(config) { ["symbols"] = symbols, ["maxRequestsPerDay"] = 4 } });
        var stopped = await SendAsync(host, HttpMethod.Post, $"/api/v1/price-sources/{id}/run?kind=latest", null);
        Assert.Equal("The daily budget of 4 requests is used up; prices stay as they are until tomorrow.", stopped.GetProperty("error").GetString());
        Assert.Equal(calls + 1, provider.PriceCalls);

        // A failing provider leaves the prices as they are and says why.
        await SendAsync(host, HttpMethod.Put, "/api/v1/admin/credentials/quote_key", new { value = "wrong" });
        await SendAsync(host, HttpMethod.Put, $"/api/v1/price-sources/{id}", new { config = new Dictionary<string, object?>(config) { ["symbols"] = symbols } });
        var failed = await SendAsync(host, HttpMethod.Post, $"/api/v1/price-sources/{id}/run?kind=latest", null);
        Assert.Equal("quotes: Error: provider answered 401 (line 2)", failed.GetProperty("error").GetString());
        Assert.Equal(prices.Count, (await PricesAsync(host)).Count);
        var runs = await SendAsync(host, HttpMethod.Get, $"/api/v1/price-sources/{id}/runs", null);
        Assert.Equal(["latest", "latest", "latest", "daily"], runs.EnumerateArray().Select(r => r.GetProperty("kind").GetString()));

        // A credential in use cannot be deleted; the function keeps its list.
        var inUse = await SendAsync(host, HttpMethod.Delete, "/api/v1/admin/credentials/quote_key", null, HttpStatusCode.Conflict);
        Assert.Equal("'quote_key' is used by isin_lookup, quotes; remove it from those functions first.", inUse.GetProperty("message").GetString());

        // The functions list the source as a use and cannot be deleted while it runs them.
        var quotesNow = await SendAsync(host, HttpMethod.Get, "/api/v1/functions/quotes", null);
        Assert.Equal(["priceSource:Fake"], quotesNow.GetProperty("usedBy").EnumerateArray().Select(u => $"{u.GetProperty("kind").GetString()}:{u.GetProperty("name").GetString()}"));
        var stillUsed = await SendAsync(host, HttpMethod.Delete, "/api/v1/functions/isin_lookup", null, HttpStatusCode.Conflict);
        Assert.Equal("'isin_lookup' runs in the price source Fake; change or delete that source first.", stillUsed.GetProperty("message").GetString());

        using (var deleted = await host.Client.DeleteAsync($"/api/v1/price-sources/{id}"))
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        using (var freed = await host.Client.DeleteAsync("/api/v1/functions/isin_lookup"))
            Assert.Equal(HttpStatusCode.NoContent, freed.StatusCode);
    }

    [Fact]
    public async Task PriceSources_OnSqlite()
    {
        await using var host = await ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Backend:AuthMode"] = "open",
            ["Schema:AutoMigrateOnStartup"] = "false",
            ["Functions:AllowPrivateNetwork"] = "true"
        });
        await RunAsync(host);
    }

    // ── Helpers ──

    private static string[] Keys(JsonElement source) =>
        source.GetProperty("keys").EnumerateArray()
            .Select(k => $"{k.GetProperty("key").GetString()}:{k.GetProperty("symbol").GetString()}:{k.GetProperty("mapped").GetBoolean()}")
            .ToArray();

    private static async Task<List<JsonElement>> PricesAsync(ApiIntegrationTestHost host) =>
        (await host.Client.GetFromJsonAsync<JsonElement>("/api/v1/data/prices?per_page=500&sort=date")).GetProperty("data").EnumerateArray().ToList();

    private static async Task<JsonElement> SendAsync(ApiIntegrationTestHost host, HttpMethod method, string path, object? body,
        HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        using var response = await host.Client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(expected == response.StatusCode, $"{method} {path}: {(int)response.StatusCode} {text}");
        if (string.IsNullOrEmpty(text))
            return default;
        var json = JsonDocument.Parse(text).RootElement.Clone();
        return json.TryGetProperty("error", out var error) && json.ValueKind == JsonValueKind.Object && !json.TryGetProperty("data", out _)
            ? error
            : json.GetProperty("data");
    }
}

[Trait("Category", "Postgres")]
[Collection(PostgresCollection.Name)]
public sealed class PostgresPriceSourceTests
{
    [PostgresFact]
    public async Task PriceSources_OnPostgres()
    {
        await using var host = await ApiIntegrationTestHost.StartPostgresAsync(new Dictionary<string, string?>
        {
            ["Backend:AuthMode"] = "open",
            ["Backend:Jwt:Secret"] = "postgres-test-secret",
            ["Schema:AutoMigrateOnStartup"] = "false",
            ["Functions:AllowPrivateNetwork"] = "true"
        });
        await PriceSourceTests.RunAsync(host);
    }
}
