using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Bento.Api.Services.Ai;

namespace Bento.Tests;

/// <summary>
/// Bento's AI (Step 4.7): settings with the key in the vault, the monthly cap,
/// the tool loop as the signed-in user (read-only for explaining), the usage log,
/// and the wire format of the Anthropic client.
/// </summary>
public sealed class AiTests
{
    [Fact]
    public async Task Settings_KeepTheKeySecret_AndOnlyAdminsChangeThem()
    {
        var model = new ScriptedModel();
        await using var host = await CreateHostAsync(model);
        var admin = await ApiKeyTests.AdminTokenAsync(host);
        var viewer = await ViewerTokenAsync(host, admin);

        var before = await DataAsync(await ApiKeyTests.SendAsync(host, HttpMethod.Get, "/api/v1/ai/status", viewer));
        Assert.False(before.GetProperty("configured").GetBoolean());

        using (var save = await ApiKeyTests.SendAsync(host, HttpMethod.Put, "/api/v1/ai/settings", admin, new { apiKey = "sk-ant-test-123", model = "claude-sonnet-5-5", monthlyLimitUsd = 5 }))
        {
            var body = await save.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, save.StatusCode);
            Assert.DoesNotContain("sk-ant-test-123", body);
            var settings = JsonDocument.Parse(body).RootElement.GetProperty("data");
            Assert.True(settings.GetProperty("configured").GetBoolean());
            Assert.Equal("claude-sonnet-5-5", settings.GetProperty("model").GetString());
            Assert.Equal(5m, settings.GetProperty("monthlyLimitUsd").GetDecimal());
        }

        // The key sits in the credential vault, encrypted.
        Assert.Contains("anthropic_api_key", await (await ApiKeyTests.SendAsync(host, HttpMethod.Get, "/api/v1/admin/credentials", admin)).Content.ReadAsStringAsync());
        Assert.True((await DataAsync(await ApiKeyTests.SendAsync(host, HttpMethod.Get, "/api/v1/ai/status", viewer))).GetProperty("enabled").GetBoolean());

        Assert.Equal(HttpStatusCode.Forbidden, (await ApiKeyTests.SendAsync(host, HttpMethod.Put, "/api/v1/ai/settings", viewer, new { model = "claude-haiku-5-5" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ApiKeyTests.SendAsync(host, HttpMethod.Put, "/api/v1/ai/settings", admin, new { model = "gpt-4" })).StatusCode);

        // Scripts and connected apps bring their own model.
        var key = await ApiKeyTests.CreateKeyAsync(host, admin, "read-write");
        Assert.Equal(HttpStatusCode.Forbidden, (await ApiKeyTests.SendAsync(host, HttpMethod.Post, "/api/v1/ai/explain", key, new { title = "x", query = new { source = "customers" } })).StatusCode);

        // An empty key removes it.
        await ApiKeyTests.SendAsync(host, HttpMethod.Put, "/api/v1/ai/settings", admin, new { apiKey = "" });
        Assert.False((await DataAsync(await ApiKeyTests.SendAsync(host, HttpMethod.Get, "/api/v1/ai/status", viewer))).GetProperty("configured").GetBoolean());
    }

    [Fact]
    public async Task Explain_RunsReadOnlyTools_AsTheUser_AndRecordsTheCost()
    {
        var model = new ScriptedModel();
        await using var host = await CreateHostAsync(model);
        var admin = await ApiKeyTests.AdminTokenAsync(host);
        await ApiKeyTests.SendAsync(host, HttpMethod.Post, "/api/v1/data/customers", admin, new { name = "Acme", tier = "gold" });
        await ApiKeyTests.SendAsync(host, HttpMethod.Post, "/api/v1/data/customers", admin, new { name = "Globex", tier = "gold" });
        await ApiKeyTests.SendAsync(host, HttpMethod.Post, "/api/v1/data/customers", admin, new { name = "Initech", tier = "bronze" });
        await ApiKeyTests.SendAsync(host, HttpMethod.Put, "/api/v1/ai/settings", admin, new { apiKey = "sk-ant-test-123", monthlyLimitUsd = 5 });
        var viewer = await ViewerTokenAsync(host, admin);

        // Turn 1: the model asks for a breakdown (and tries a write tool it was not given); turn 2: it answers.
        model.Script.Enqueue(request => new AiResponse(
        [
            new AiThinking("", "sig-1"),
            new AiToolUse("tu_1", "aggregate", Json(new { source = "customers", groupBy = new[] { new { column = "tier" } }, measures = new[] { new { name = "n", fn = "count" } } })),
            new AiToolUse("tu_2", "create_row", Json(new { table = "customers", values = new { name = "Sneaky" } })),
        ], "tool_use", new AiUsage(4000, 300, 0, 3000), "claude-opus-5-5"));
        model.Script.Enqueue(request => new AiResponse([new AiText("Two of three customers are gold.")], "end_turn", new AiUsage(4500, 120, 3000, 0), "claude-opus-5-5"));

        using var response = await ApiKeyTests.SendAsync(host, HttpMethod.Post, "/api/v1/ai/explain", viewer, new
        {
            language = "de-DE",
            dashboard = "Customers",
            title = "Customers by tier",
            type = "donut",
            query = new { source = "customers", groupBy = new[] { new { column = "tier" } }, measures = new[] { new { name = "n", fn = "count" } } },
            result = new[] { new { tier = "gold", n = 2 }, new { tier = "bronze", n = 1 } }
        });
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        var answer = JsonDocument.Parse(body).RootElement.GetProperty("data");
        Assert.Equal("Two of three customers are gold.", answer.GetProperty("answer").GetString());
        Assert.Equal(2, answer.GetProperty("toolCalls").GetInt32());
        Assert.True(answer.GetProperty("costUsd").GetDecimal() > 0);

        // What the model was given: only reading tools, the guide in the cached system prompt, the widget as context.
        var first = model.Requests[0];
        Assert.Equal("claude-opus-5-5", first.Model);
        Assert.Contains(first.Tools, t => t.Name == "aggregate");
        Assert.DoesNotContain(first.Tools, t => t.Name is "create_row" or "update_row" or "delete_row" or "call_api" or "apply_schema_change");
        Assert.Contains("You explain a number or a chart", first.System);
        Assert.Contains("Wealth & cash-flow template", first.System);
        Assert.Contains("\"language\":\"de-DE\"", ((AiText)first.Messages[0].Blocks[0]).Text);

        // Turn 2: the thinking was sent back unchanged, the aggregate ran with the viewer's rights, the write tool did not exist.
        var second = model.Requests[1];
        Assert.Equal(new AiThinking("", "sig-1"), second.Messages[1].Blocks[0]);
        var results = second.Messages[2].Blocks.OfType<AiToolResult>().ToList();
        Assert.Contains("\"n\":2", results.Single(r => r.ToolUseId == "tu_1").Content);
        Assert.False(results.Single(r => r.ToolUseId == "tu_1").IsError);
        Assert.True(results.Single(r => r.ToolUseId == "tu_2").IsError);
        Assert.DoesNotContain("Sneaky", await (await ApiKeyTests.SendAsync(host, HttpMethod.Get, "/api/v1/data/customers", admin)).Content.ReadAsStringAsync());

        // The call is in the log: the viewer sees their own, the admin everyone's.
        var own = (await DataAsync(await ApiKeyTests.SendAsync(host, HttpMethod.Get, "/api/v1/ai/usage", viewer)))[0];
        Assert.Equal("explain", own.GetProperty("feature").GetString());
        Assert.Equal(8500, own.GetProperty("inputTokens").GetInt64());
        Assert.Equal(3000, own.GetProperty("cacheReadTokens").GetInt64());
        Assert.True(own.GetProperty("ok").GetBoolean());
        var spent = (await DataAsync(await ApiKeyTests.SendAsync(host, HttpMethod.Get, "/api/v1/ai/settings", admin))).GetProperty("spentThisMonthUsd").GetDecimal();
        Assert.Equal(AiSettingsService.Cost("claude-opus-5-5", new AiUsage(8500, 420, 3000, 3000)), spent);
    }

    [Fact]
    public async Task Explain_RefusesWithoutKey_OrBudget_WithoutCallingTheModel()
    {
        var model = new ScriptedModel();
        await using var host = await CreateHostAsync(model);
        var admin = await ApiKeyTests.AdminTokenAsync(host);
        var request = new { title = "Customers", query = new { source = "customers", measures = new[] { new { name = "n", fn = "count" } } } };

        using (var notSetUp = await ApiKeyTests.SendAsync(host, HttpMethod.Post, "/api/v1/ai/explain", admin, request))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, notSetUp.StatusCode);
            Assert.Contains("not set up", await notSetUp.Content.ReadAsStringAsync());
        }

        await ApiKeyTests.SendAsync(host, HttpMethod.Put, "/api/v1/ai/settings", admin, new { apiKey = "sk-ant-test-123", monthlyLimitUsd = 0 });
        using (var noBudget = await ApiKeyTests.SendAsync(host, HttpMethod.Post, "/api/v1/ai/explain", admin, request))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, noBudget.StatusCode);
            Assert.Contains("ai_budget_exhausted", await noBudget.Content.ReadAsStringAsync());
        }
        Assert.False((await DataAsync(await ApiKeyTests.SendAsync(host, HttpMethod.Get, "/api/v1/ai/status", admin))).GetProperty("budgetLeft").GetBoolean());

        await ApiKeyTests.SendAsync(host, HttpMethod.Put, "/api/v1/ai/settings", admin, new { monthlyLimitUsd = 5, enabled = false });
        Assert.Contains("switched off", await (await ApiKeyTests.SendAsync(host, HttpMethod.Post, "/api/v1/ai/explain", admin, request)).Content.ReadAsStringAsync());
        Assert.Empty(model.Requests);

        // A refusal is reported and still counted.
        await ApiKeyTests.SendAsync(host, HttpMethod.Put, "/api/v1/ai/settings", admin, new { enabled = true });
        model.Script.Enqueue(_ => new AiResponse([], "refusal", new AiUsage(100, 0, 0, 0), "claude-opus-5-5"));
        using (var refused = await ApiKeyTests.SendAsync(host, HttpMethod.Post, "/api/v1/ai/explain", admin, request))
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        var log = (await DataAsync(await ApiKeyTests.SendAsync(host, HttpMethod.Get, "/api/v1/ai/usage", admin)))[0];
        Assert.False(log.GetProperty("ok").GetBoolean());
        Assert.Contains("declined", log.GetProperty("error").GetString());
    }

    [Fact]
    public async Task AnthropicClient_SendsTheExpectedRequest_AndReadsTheAnswer()
    {
        await using var api = await FakeAnthropicApi.StartAsync("""
            {"id":"msg_1","type":"message","role":"assistant","model":"claude-opus-5-5",
             "content":[{"type":"thinking","thinking":"","signature":"sig-2"},{"type":"text","text":"Looking."},{"type":"tool_use","id":"tu_9","name":"aggregate","input":{"source":"transactions"}}],
             "stop_reason":"tool_use","stop_sequence":null,
             "usage":{"input_tokens":12,"output_tokens":7,"cache_read_input_tokens":3000,"cache_creation_input_tokens":0}}
            """);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Backend:Ai:BaseUrl"] = api.Url }).Build();
        var client = new AnthropicAiModel(config);

        var schema = JsonNode.Parse("""{"type":"object","properties":{"source":{"type":"string"}},"required":["source"]}""")!.AsObject();
        var response = await client.SendAsync("sk-ant-test-123", new AiRequest(
            "claude-opus-5-5", "You explain.", [new AiToolSpec("aggregate", "Aggregates.", schema), new AiToolSpec("list_rows", "Rows.", new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() })],
            [
                AiMessage.User("Explain this widget"),
                new AiMessage("assistant", [new AiThinking("", "sig-1"), new AiToolUse("tu_1", "aggregate", Json(new { source = "transactions" }))]),
                new AiMessage("user", [new AiToolResult("tu_1", "{\"data\":[]}", false)]),
            ], 16000, "medium"), CancellationToken.None);

        // The answer, mapped.
        Assert.Equal("tool_use", response.StopReason);
        Assert.Equal(new AiThinking("", "sig-2"), response.Content[0]);
        Assert.Equal("Looking.", ((AiText)response.Content[1]).Text);
        Assert.Equal("aggregate", ((AiToolUse)response.Content[2]).Name);
        Assert.Equal(3000, response.Usage.CacheReadTokens);

        // The request on the wire.
        Assert.StartsWith("/v1/messages", api.Path);
        Assert.Equal("sk-ant-test-123", api.Headers["x-api-key"]);
        Assert.Contains("server-side-fallback-2026-07-01", api.Headers["anthropic-beta"]);
        var body = JsonNode.Parse(api.Body)!;
        Assert.Equal("claude-opus-5-5", (string?)body["model"]);
        Assert.Equal("default", (string?)body["fallbacks"]);
        Assert.Equal("medium", (string?)body["output_config"]!["effort"]);
        Assert.Equal("ephemeral", (string?)body["system"]![0]!["cache_control"]!["type"]);
        Assert.Null(body["tools"]![0]!["cache_control"]);
        Assert.Equal("ephemeral", (string?)body["tools"]![1]!["cache_control"]!["type"]);
        Assert.Equal("source", (string?)body["tools"]![0]!["input_schema"]!["required"]![0]);
        Assert.Equal("sig-1", (string?)body["messages"]![1]!["content"]![0]!["signature"]);
        Assert.Equal("tu_1", (string?)body["messages"]![2]!["content"]![0]!["tool_use_id"]);
    }

    [Fact]
    public async Task AnthropicClient_RetriesWithoutTheFallback_AndExplainsAuthErrors()
    {
        const string answer = """
            {"id":"msg_2","type":"message","role":"assistant","model":"claude-opus-5-5","content":[{"type":"text","text":"Fine."}],
             "stop_reason":"end_turn","stop_sequence":null,"usage":{"input_tokens":5,"output_tokens":2}}
            """;
        const string fallbackError = """{"type":"error","error":{"type":"invalid_request_error","message":"fallbacks: not available for this organization"}}""";
        await using (var api = await FakeAnthropicApi.StartAsync((400, fallbackError), (200, answer)))
        {
            var client = new AnthropicAiModel(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Backend:Ai:BaseUrl"] = api.Url }).Build());
            var response = await client.SendAsync("sk-ant-test-123", new AiRequest("claude-opus-5-5", "You explain.", [], [AiMessage.User("Hi")], 1000, "low"), CancellationToken.None);
            Assert.Equal("Fine.", ((AiText)response.Content[0]).Text);
            Assert.Equal(2, api.Bodies.Count);
            Assert.Equal("default", (string?)JsonNode.Parse(api.Bodies[0])!["fallbacks"]);
            Assert.Null(JsonNode.Parse(api.Bodies[1])!["fallbacks"]);
        }

        const string authError = """{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}""";
        await using (var api = await FakeAnthropicApi.StartAsync((401, authError)))
        {
            var client = new AnthropicAiModel(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Backend:Ai:BaseUrl"] = api.Url }).Build());
            var error = await Assert.ThrowsAsync<AiProviderException>(() => client.SendAsync("sk-ant-wrong", new AiRequest("claude-opus-5-5", "x", [], [AiMessage.User("Hi")], 1000, "low"), CancellationToken.None));
            Assert.Contains("did not accept the API key", error.Message);
        }
    }

    // ── Helpers ──

    private static async Task<ApiIntegrationTestHost> CreateHostAsync(IAiModel model)
    {
        var host = await ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Backend:AuthMode"] = "standard",
            ["Backend:Jwt:Secret"] = "super-secret",
            ["Backend:AuthRateLimitPerMinute"] = "100",
            ["Schema:AutoMigrateOnStartup"] = "false"
        }, configureTestServices: services => services.Replace(ServiceDescriptor.Singleton(model)));
        await host.ApplySchemaAsync(ApiKeyTests.Schema);
        return host;
    }

    internal static async Task<string> ViewerTokenAsync(ApiIntegrationTestHost host, string admin)
    {
        (await ApiKeyTests.SendAsync(host, HttpMethod.Post, "/api/v1/auth/register", admin, new { username = "viewer", password = "anotherpass", role = "viewer" })).EnsureSuccessStatusCode();
        using var login = await host.Client.PostAsJsonAsync("/api/v1/auth/login", new { username = "viewer", password = "anotherpass" });
        return (await login.Content.ReadFromJsonAsync<Bento.Api.Models.AuthTokenResponse>())!.AccessToken;
    }

    internal static async Task<JsonElement> DataAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        return JsonDocument.Parse(body).RootElement.GetProperty("data").Clone();
    }

    internal static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    /// <summary>A model that answers from a script and remembers what it was asked.</summary>
    internal sealed class ScriptedModel : IAiModel
    {
        public Queue<Func<AiRequest, AiResponse>> Script { get; } = new();
        public List<AiRequest> Requests { get; } = [];

        public Task<AiResponse> SendAsync(string apiKey, AiRequest request, CancellationToken cancellationToken)
        {
            Assert.Equal("sk-ant-test-123", apiKey);
            lock (Script)
            {
                Requests.Add(request with { Messages = request.Messages.ToList() });
                return Task.FromResult(Script.Dequeue()(request));
            }
        }
    }

    /// <summary>Canned Messages API answers (status, body) on a local port, recording each request.</summary>
    private sealed class FakeAnthropicApi : IAsyncDisposable
    {
        private readonly HttpListener _listener;
        private readonly Task _serve;

        private FakeAnthropicApi(HttpListener listener, string url, (int Status, string Body)[] answers)
        {
            _listener = listener;
            Url = url;
            _serve = Task.Run(async () =>
            {
                foreach (var (status, answer) in answers)
                {
                    var context = await _listener.GetContextAsync();
                    Path = context.Request.Url!.PathAndQuery;
                    foreach (var key in context.Request.Headers.AllKeys)
                        Headers[key!.ToLowerInvariant()] = context.Request.Headers[key]!;
                    using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
                        Bodies.Add(await reader.ReadToEndAsync());
                    var bytes = Encoding.UTF8.GetBytes(answer);
                    context.Response.StatusCode = status;
                    context.Response.ContentType = "application/json";
                    context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes);
                    context.Response.Close();
                }
            });
        }

        public string Url { get; }
        public string Path { get; private set; } = "";
        public List<string> Bodies { get; } = [];
        public string Body => Bodies[^1];
        public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

        public static Task<FakeAnthropicApi> StartAsync(string answer) => StartAsync((200, answer));

        public static Task<FakeAnthropicApi> StartAsync(params (int Status, string Body)[] answers)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            var url = $"http://127.0.0.1:{port}/";
            var listener = new HttpListener();
            listener.Prefixes.Add(url);
            listener.Start();
            return Task.FromResult(new FakeAnthropicApi(listener, url.TrimEnd('/'), answers));
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            try { await _serve; } catch { /* stopped */ }
            _listener.Close();
        }
    }
}
