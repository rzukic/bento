using System.Net;
using System.Text;
using System.Text.Json;
using Bento.Api.Formula;
using Bento.Api.Formula.Sandbox;

namespace Bento.Tests;

/// <summary>A tiny HTTP server on 127.0.0.1 for functions that fetch (Step 4.2).</summary>
public sealed class FakeQuoteServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();

    public FakeQuoteServer(Func<HttpListenerRequest, (int Status, string Body, string? Location)> handle)
    {
        Port = FreePort();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _ = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch
                {
                    return;
                }
                Requests.Add($"{context.Request.HttpMethod} {context.Request.Url!.PathAndQuery} ua={context.Request.UserAgent} key={context.Request.Headers["X-Key"]}");
                try
                {
                    var (status, body, location) = handle(context.Request);
                    context.Response.StatusCode = status;
                    if (location is not null)
                        context.Response.RedirectLocation = location;
                    context.Response.ContentType = "application/json; charset=utf-8";
                    var bytes = Encoding.UTF8.GetBytes(body);
                    context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes);
                    context.Response.Close();
                }
                catch
                {
                    // The client gave up (a timeout, an answer too large): serve the next request.
                }
            }
        });
    }

    public int Port { get; }
    public string Url => $"http://127.0.0.1:{Port}";
    public System.Collections.Concurrent.ConcurrentBag<string> Requests { get; } = [];

    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Close();
    }
}

public sealed class JsNetworkTests : IDisposable
{
    private readonly FakeQuoteServer _server;
    private readonly JsNetwork _local = new(new JsNetworkOptions { AllowPrivateNetwork = true, RequestTimeout = TimeSpan.FromSeconds(2), MaxResponseBytes = 10_000 });

    public JsNetworkTests()
    {
        _server = new FakeQuoteServer(request => request.Url!.AbsolutePath switch
        {
            "/quote" => (200, """{"symbol":"VWCE.DE","price":173.38}""", null),
            "/missing" => (404, """{"error":"not found"}""", null),
            "/moved" => (302, "", "/quote"),
            "/away" => (302, "", "https://evil.example.org/steal"),
            "/big" => (200, new string('x', 20_000), null),
            "/slow" => NoAnswerUntilDone(),
            _ => (200, JsonSerializer.Serialize(new { method = request.HttpMethod }), null)
        });
    }

    private readonly ManualResetEventSlim _done = new();

    /// <summary>
    /// Answers only when the test is over, so the 2 s timeout always wins. A fixed
    /// 3 s sleep raced it: on a busy CI machine the timeout's timer waits for a free
    /// thread-pool thread (it fired after 6 s in a local test with the pool full), and
    /// the 3 s answer came first.
    /// </summary>
    private (int, string, string?) NoAnswerUntilDone()
    {
        _done.Wait(TimeSpan.FromSeconds(60));
        return (200, "{}", null);
    }

    public void Dispose()
    {
        _done.Set();
        _server.Dispose();
        _local.Dispose();
        _done.Dispose();
    }

    private CustomFunctionDef Def(string code, string result = "any", string[]? credentials = null, params string[] hosts) =>
        new("f", null, [], result, code, 1) { AllowedHosts = hosts.Length == 0 ? ["127.0.0.1"] : hosts, Credentials = credentials ?? [] };

    private JsCallResult Run(CustomFunctionDef definition, JsNetwork? network = null, Dictionary<string, string>? secrets = null)
    {
        var (function, error) = JsFunction.Create(definition, host: new JsHost(network ?? _local, secrets ?? []));
        Assert.True(function is not null, error);
        using (function)
            return function!.Call([]);
    }

    [Fact]
    public void Fetch_AwaitsJsonAndText()
    {
        var result = Run(Def($$"""
            const r = await fetch("{{_server.Url}}/quote", { headers: { "X-Key": "abc" } });
            const body = await r.json();
            return { ok: r.ok, status: r.status, type: r.headers.get("Content-Type"), price: body.price };
            """));
        Assert.True(result.Ok, result.Error);
        var value = (JsonElement)result.Value!;
        Assert.Equal("""{"ok":true,"status":200,"type":"application/json; charset=utf-8","price":173.38}""", value.GetRawText());
        Assert.Contains(_server.Requests, r => r.StartsWith("GET /quote ua=Mozilla/5.0 (compatible; Bento) key=abc", StringComparison.Ordinal));

        var posted = Run(Def($$"""
            const r = await fetch("{{_server.Url}}/echo", { method: "POST", body: JSON.stringify({ a: 1 }) });
            return (await r.json()).method;
            """, "text"));
        Assert.Equal("POST", posted.Value);

        var notFound = Run(Def($$"""const r = await fetch("{{_server.Url}}/missing"); return r.ok ? "ok" : "status " + r.status;""", "text"));
        Assert.Equal("status 404", notFound.Value);

        var redirected = Run(Def($$"""return (await (await fetch("{{_server.Url}}/moved")).json()).price;""", "float"));
        Assert.Equal(173.38m, redirected.Value);
    }

    [Fact]
    public void Fetch_RefusesWhatIsNotAllowed()
    {
        var otherHost = Run(Def("""await fetch("https://example.org/"); return 1;""", "integer"));
        Assert.Equal("Error: example.org is not on this function's list of hosts (127.0.0.1). (line 1)", otherHost.Error);

        // The function can catch the refusal like any failed fetch.
        var caught = Run(Def("""try { await fetch("https://example.org/"); return "no"; } catch (e) { return "caught: " + e.message; }""", "text"));
        Assert.Equal("caught: example.org is not on this function's list of hosts (127.0.0.1).", caught.Value);

        var redirectAway = Run(Def($$"""await fetch("{{_server.Url}}/away"); return 1;""", "integer"));
        Assert.Equal("Error: evil.example.org is not on this function's list of hosts (127.0.0.1). (line 1)", redirectAway.Error);

        var big = Run(Def($$"""await fetch("{{_server.Url}}/big"); return 1;""", "integer"));
        Assert.Contains("is larger than", big.Error);

        var slow = Run(Def($$"""await fetch("{{_server.Url}}/slow"); return 1;""", "integer"));
        Assert.Equal("Error: 127.0.0.1 did not answer within 2 s. (line 1)", slow.Error);

        // Without the private-network switch (production), the server's own network and http are off limits.
        using var production = new JsNetwork(new JsNetworkOptions());
        var local = Run(Def($$"""await fetch("{{_server.Url}}/quote"); return 1;""", "integer"), production);
        Assert.Equal("Error: fetch only calls https URLs, not http. (line 1)", local.Error);
        var loopback = Run(Def("""await fetch("https://localhost/"); return 1;""", "integer", null, "localhost"), production);
        Assert.Equal("Error: localhost is not a public address; functions cannot call the server's own network. (line 1)", loopback.Error);

        // A function without hosts has no fetch at all.
        var (plain, _) = JsFunction.Create(new CustomFunctionDef("p", null, [], "text", "return typeof fetch + typeof credentials;", 1));
        using (plain)
            Assert.Equal("undefinedundefined", plain!.Call([]).Value);
    }

    [Fact]
    public void Credentials_AreReadableButMasked()
    {
        var result = Run(Def($$"""
            console.log("key is", credentials.quote_key);
            const r = await fetch("{{_server.Url}}/quote", { headers: { "X-Key": credentials.quote_key } });
            throw new Error("failed with " + credentials.quote_key);
            """, "text", ["quote_key"]), secrets: new() { ["quote_key"] = "s3cr3t-value" });
        Assert.Equal(["key is •••"], result.Logs);
        Assert.Equal("Error: failed with ••• (line 3)", result.Error);
        Assert.Contains(_server.Requests, r => r.EndsWith("key=s3cr3t-value", StringComparison.Ordinal));

        var frozen = Run(Def("""credentials.quote_key = "x"; return credentials.quote_key;""", "text", ["quote_key"]), secrets: new() { ["quote_key"] = "s3cr3t-value" });
        Assert.NotNull(frozen.Error);
    }

    [Fact]
    public void AnyResult_IsJson()
    {
        var (function, error) = JsFunction.Create(new CustomFunctionDef("rows", null, [new CustomFunctionArgument("symbols", "any")], "any",
            "return symbols.map((s, i) => ({ symbol: s, date: \"2026-10-06\", price: 10 + i }));", 1));
        Assert.Null(error);
        using (function)
        {
            var result = function!.Call([JsonDocument.Parse("""["EUNL.DE","VWCE.DE"]""").RootElement]);
            Assert.True(result.Ok, result.Error);
            Assert.Equal("""[{"symbol":"EUNL.DE","date":"2026-10-06","price":10},{"symbol":"VWCE.DE","date":"2026-10-06","price":11}]""",
                ((JsonElement)result.Value!).GetRawText());
        }
    }

    [Theory]
    [InlineData("api.example.com", true)]
    [InlineData("*.yahoo.com", true)]
    [InlineData("https://api.example.com", false)]
    [InlineData("api.example.com/path", false)]
    [InlineData("API.example.com", false)]
    [InlineData("*", false)]
    public void HostPatterns(string host, bool valid) => Assert.Equal(valid, CustomFunctionRules.IsValidHost(host));

    [Fact]
    public void HostMatching()
    {
        Assert.True(CustomFunctionRules.HostAllowed("query1.finance.yahoo.com", ["*.yahoo.com"]));
        Assert.False(CustomFunctionRules.HostAllowed("yahoo.com", ["*.yahoo.com"]));
        Assert.False(CustomFunctionRules.HostAllowed("evilyahoo.com", ["*.yahoo.com"]));
        Assert.True(CustomFunctionRules.HostAllowed("API.openfigi.com", ["api.openfigi.com"]));
        Assert.False(JsNetwork.IsPublic(IPAddress.Parse("10.1.2.3")));
        Assert.False(JsNetwork.IsPublic(IPAddress.Parse("169.254.169.254")));
        Assert.False(JsNetwork.IsPublic(IPAddress.Parse("::1")));
        Assert.False(JsNetwork.IsPublic(IPAddress.Parse("fd00::1")));
        Assert.True(JsNetwork.IsPublic(IPAddress.Parse("142.250.1.1")));
    }
}
