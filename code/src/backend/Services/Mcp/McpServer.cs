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
    private readonly TemplateRegistry? _templates;
    private readonly Workspaces.WorkspaceService? _workspaces;

    public McpServer(McpTools tools, McpCallLog log, ILogger<McpServer> logger, Apps.AppService? apps = null, TemplateRegistry? templates = null,
        Workspaces.WorkspaceService? workspaces = null)
    {
        _workspaces = workspaces;
        _apps = apps;
        _templates = templates;
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
                "initialize" => Result(id, Initialize(parameters, await WorkspaceContextAsync(caller, ct))),
                "ping" => Result(id, new JsonObject()),
                "tools/list" => Result(id, new JsonObject { ["tools"] = new JsonArray(VisibleTools(caller).Select(t => Describe(t, ManyWorkspaces(caller))).ToArray()) }),
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

    /// <summary>
    /// What this workspace adds to the instructions: the other workspaces the
    /// connection can use (Step 5.13), its templates (their tables and
    /// conventions) and which tables are calendars on the user's devices.
    /// </summary>
    private async Task<string?> WorkspaceContextAsync(McpCaller caller, CancellationToken ct)
    {
        var parts = new List<string>();
        if (ManyWorkspaces(caller))
        {
            try
            {
                var usable = await _tools.WorkspacesAsync(caller, ct);
                if (usable.Count > 1)
                    parts.Add("Workspaces this connection can use — each has its own tables, dashboards and forms:\n" +
                              string.Join("\n", usable.Select(w => $"- {w.Name} (workspace: \"{w.Slug}\"{(w.Role is null ? "" : $", you are {w.Role}")}){(w.Current ? " — used when a tool gets no workspace" : "")}")) +
                              "\nPass workspace to any tool to work in another one; say which workspace you mean when it is not the default one.");
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _logger.LogWarning(e, "The workspaces could not be read for the MCP instructions.");
            }
        }
        try
        {
            if (_templates is not null && await _templates.ContextAsync() is { } templates)
                parts.Add(templates);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogWarning(e, "The templates could not be read for the MCP instructions.");
        }
        try
        {
            if (_apps is not null && await _apps.McpContextAsync() is { } apps)
                parts.Add(apps);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogWarning(e, "The apps could not be read for the MCP instructions.");
        }
        return parts.Count == 0 ? null : string.Join("\n", parts);
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

    /// <summary>Tools that are about workspaces themselves take no workspace argument.</summary>
    private static readonly HashSet<string> NoWorkspaceArgument = ["list_workspaces", "create_workspace", "bento_guide"];

    /// <summary>Whether the caller may reach more than one workspace (Step 5.13): then every tool takes <c>workspace</c>.</summary>
    private bool ManyWorkspaces(McpCaller caller) =>
        _workspaces is not null && _workspaces.All.Count(w => caller.AllowsWorkspace(w.Id)) > 1;

    private static JsonNode Describe(McpTool tool, bool manyWorkspaces)
    {
        var schema = tool.InputSchema.DeepClone().AsObject();
        if (manyWorkspaces && !NoWorkspaceArgument.Contains(tool.Name) && schema["properties"] is JsonObject properties)
            properties["workspace"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "The workspace to work in, by its short name (list_workspaces); leave out for this connection's own."
            };
        return Describe(tool, schema);
    }

    private static JsonNode Describe(McpTool tool, JsonObject inputSchema) => new JsonObject
    {
        ["name"] = tool.Name,
        ["title"] = tool.Title,
        ["description"] = tool.Description,
        ["inputSchema"] = inputSchema,
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

        // Step 5.13: another workspace for this call; the inner API call checks access there again.
        Workspaces.Workspace? target = null;
        string? workspaceError = null;
        if (arguments.TryGetProperty("workspace", out var named) && named.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(named.GetString()))
        {
            var slug = named.GetString()!.Trim();
            target = _workspaces?.Find(slug) ?? (_workspaces is null ? null : (await _workspaces.ReloadAsync()).FirstOrDefault(w => string.Equals(w.Slug, slug, StringComparison.OrdinalIgnoreCase)));
            if (target is null || !caller.AllowsWorkspace(target.Id))
            {
                workspaceError = target is null
                    ? $"There is no workspace '{slug}'; list_workspaces names those this connection can use."
                    : $"This connection may not use the workspace '{slug}'; list_workspaces names those it can.";
                target = null;
            }
            arguments = Without(arguments, "workspace");
        }

        var watch = Stopwatch.StartNew();
        McpToolResult result;
        try
        {
            if (workspaceError is not null)
                result = new McpToolResult(workspaceError, true);
            else if (!caller.Allows(tool.Permission))
                result = McpTools.Refusal(tool.Permission);
            else
            {
                using (target is null ? null : Workspaces.WorkspaceContext.Use(target))
                    result = await tool.Run(caller, arguments, ct);
            }
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

    /// <summary>The arguments without one property.</summary>
    private static JsonElement Without(JsonElement arguments, string name)
    {
        var copy = JsonNode.Parse(arguments.GetRawText())!.AsObject();
        copy.Remove(name);
        return JsonDocument.Parse(copy.ToJsonString()).RootElement.Clone();
    }
}
