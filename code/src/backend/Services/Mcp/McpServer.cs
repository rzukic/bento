using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bento.Api.Services.Mcp;

/// <summary>
/// The MCP protocol over JSON-RPC 2.0 (streamable HTTP, stateless, plain JSON
/// responses): <c>initialize</c>, <c>ping</c>, <c>tools/list</c> and
/// <c>tools/call</c>. Every tool call is written to the call log.
/// </summary>
public sealed class McpServer
{
    /// <summary>Newest first; a client asking for another version gets the newest.</summary>
    public static readonly string[] SupportedProtocolVersions = ["2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"];

    private const int ParseError = -32700;
    private const int InvalidRequest = -32600;
    private const int MethodNotFound = -32601;
    private const int InvalidParams = -32602;
    private const int InternalError = -32603;

    private static readonly string Version =
        typeof(McpServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "1.0.0";

    /// <summary>Arguments are logged as written (umlauts, €), whatever escaping the client used.</summary>
    private static readonly JsonSerializerOptions LogJson = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly McpTools _tools;
    private readonly McpCallLog _log;
    private readonly ILogger<McpServer> _logger;

    private readonly Apps.AppService? _apps;

    public McpServer(McpTools tools, McpCallLog log, ILogger<McpServer> logger, Apps.AppService? apps = null)
    {
        _apps = apps;
        _tools = tools;
        _log = log;
        _logger = logger;
    }

    /// <summary>The response to one JSON-RPC message, or null for notifications and responses.</summary>
    public async Task<JsonNode?> HandleAsync(JsonElement message, McpCaller caller, CancellationToken ct)
    {
        if (message.ValueKind != JsonValueKind.Object)
            return Error(null, InvalidRequest, "A JSON-RPC message must be an object.");

        var hasId = message.TryGetProperty("id", out var idElement) && idElement.ValueKind is JsonValueKind.String or JsonValueKind.Number;
        var id = hasId ? JsonNode.Parse(idElement.GetRawText()) : null;
        if (!message.TryGetProperty("method", out var methodElement) || methodElement.ValueKind != JsonValueKind.String)
        {
            // A response from the client (we never send requests) or garbage.
            return hasId && (message.TryGetProperty("result", out _) || message.TryGetProperty("error", out _))
                ? null
                : Error(id, InvalidRequest, "Missing method.");
        }

        var method = methodElement.GetString()!;
        if (!hasId)
            return null; // notifications/initialized, notifications/cancelled, …

        var parameters = message.TryGetProperty("params", out var p) ? p : default;
        try
        {
            return method switch
            {
                "initialize" => Result(id, Initialize(parameters, await WorkspaceContextAsync())),
                "ping" => Result(id, new JsonObject()),
                "tools/list" => Result(id, new JsonObject { ["tools"] = new JsonArray(VisibleTools(caller).Select(Describe).ToArray()) }),
                "tools/call" => await CallToolAsync(id, parameters, caller, ct),
                "resources/list" => Result(id, new JsonObject { ["resources"] = new JsonArray() }),
                "prompts/list" => Result(id, new JsonObject { ["prompts"] = new JsonArray() }),
                _ => Error(id, MethodNotFound, $"Method '{method}' is not supported.")
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "MCP {Method} failed", method);
            return Error(id, InternalError, "Internal error.");
        }
    }

    /// <summary>What this workspace adds to the instructions: which tables are calendars on the user's devices.</summary>
    private async Task<string?> WorkspaceContextAsync()
    {
        try
        {
            return _apps is null ? null : await _apps.McpContextAsync();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogWarning(e, "The apps could not be read for the MCP instructions.");
            return null;
        }
    }

    private static JsonObject Initialize(JsonElement parameters, string? context)
    {
        var requested = parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("protocolVersion", out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
        var version = requested is not null && SupportedProtocolVersions.Contains(requested) ? requested : SupportedProtocolVersions[0];
        return new JsonObject
        {
            ["protocolVersion"] = version,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
            ["serverInfo"] = new JsonObject { ["name"] = "bento", ["title"] = "Bento", ["version"] = Version },
            ["instructions"] = context is null ? McpTools.Instructions : McpTools.Instructions + "\n\n" + context
        };
    }

    /// <summary>A connection sees only the tools its permissions allow.</summary>
    private IEnumerable<McpTool> VisibleTools(McpCaller caller) => _tools.All.Where(tool => caller.Allows(tool.Permission));

    private static JsonNode Describe(McpTool tool) => new JsonObject
    {
        ["name"] = tool.Name,
        ["title"] = tool.Title,
        ["description"] = tool.Description,
        ["inputSchema"] = tool.InputSchema.DeepClone(),
        ["annotations"] = new JsonObject
        {
            ["title"] = tool.Title,
            ["readOnlyHint"] = tool.ReadOnly,
            ["destructiveHint"] = tool.Destructive,
            ["idempotentHint"] = tool.Idempotent,
            ["openWorldHint"] = false
        }
    };

    private async Task<JsonNode> CallToolAsync(JsonNode? id, JsonElement parameters, McpCaller caller, CancellationToken ct)
    {
        if (parameters.ValueKind != JsonValueKind.Object || !parameters.TryGetProperty("name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String)
            return Error(id, InvalidParams, "tools/call needs a tool name.");
        var name = nameElement.GetString()!;
        var tool = _tools.Find(name);
        if (tool is null)
            return Error(id, InvalidParams, $"Unknown tool '{name}'.");
        var arguments = parameters.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.Object
            ? a
            : JsonDocument.Parse("{}").RootElement;

        var watch = Stopwatch.StartNew();
        McpToolResult result;
        try
        {
            result = caller.Allows(tool.Permission) ? await tool.Run(caller, arguments, ct) : McpTools.Refusal(tool.Permission);
        }
        catch (McpArgumentException ex)
        {
            result = new McpToolResult(ex.Message, true);
        }
        watch.Stop();

        try
        {
            await _log.WriteAsync(new McpCallLog.Entry(
                caller.UserId, caller.Via, caller.Credential, name, JsonSerializer.Serialize(arguments, LogJson),
                !result.IsError, result.IsError ? result.Text : null, (int)watch.ElapsedMilliseconds, caller.GrantId));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not log the MCP call to {Tool}", name);
        }

        return Result(id, new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = result.Text }),
            ["isError"] = result.IsError
        });
    }

    private static JsonObject Result(JsonNode? id, JsonNode result) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["result"] = result };

    public static JsonObject Error(JsonNode? id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

    public static JsonObject ParseFailure() => Error(null, ParseError, "Parse error: the body is not JSON.");
}
