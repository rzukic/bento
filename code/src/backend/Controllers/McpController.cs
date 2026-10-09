using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Bento.Api.Authorization;
using Bento.Api.Services;
using Bento.Api.Services.Mcp;

namespace Bento.Api.Controllers;

/// <summary>
/// The MCP endpoint (Step 4.6): streamable HTTP at <c>/mcp</c>, stateless, JSON
/// responses. <see cref="Middleware.AuthMiddleware"/> has already authenticated the
/// caller (OAuth access token, API key or session); a missing or bad token gets
/// 401 with the pointer to the OAuth metadata there.
/// </summary>
[ApiController]
[Route(ApiRoutes.Mcp)]
public sealed class McpController : ControllerBase
{
    private readonly McpServer _server;

    public McpController(McpServer server)
    {
        _server = server;
    }

    [HttpPost]
    public async Task<IActionResult> Post(CancellationToken ct)
    {
        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(Request.Body, cancellationToken: ct);
        }
        catch (JsonException)
        {
            return new JsonResult(McpServer.ParseFailure()) { StatusCode = StatusCodes.Status400BadRequest };
        }

        using (document)
        {
            var caller = Caller(HttpContext);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                var responses = new JsonArray();
                foreach (var message in root.EnumerateArray())
                {
                    if (await _server.HandleAsync(message, caller, ct) is { } response)
                        responses.Add(response);
                }
                return responses.Count == 0 ? Accepted() : Json(responses);
            }

            var single = await _server.HandleAsync(root, caller, ct);
            return single is null ? Accepted() : Json(single);
        }
    }

    /// <summary>No server-initiated stream: every answer comes with its request.</summary>
    [HttpGet]
    [HttpDelete]
    public IActionResult NotSupported()
    {
        Response.Headers.Allow = "POST";
        return StatusCode(StatusCodes.Status405MethodNotAllowed);
    }

    private static ContentResult Json(JsonNode node) => new()
    {
        Content = node.ToJsonString(),
        ContentType = "application/json",
        StatusCode = StatusCodes.Status200OK
    };

    internal static McpCaller Caller(HttpContext context)
    {
        var user = context.User;
        var method = user.FindFirstValue("auth_method");
        var role = user.GetRole();
        var credential = method switch
        {
            "api_key" => "API key #" + user.FindFirstValue("api_key_id"),
            "oauth" => user.FindFirstValue("oauth_client") ?? "MCP client",
            _ => null
        };
        var via = method ?? (user.Identity?.AuthenticationType == "OpenMode" ? "open" : "session");
        var userId = long.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id > 0
            ? id
            : (long?)null;

        var request = context.Request;
        var credentials = !string.IsNullOrWhiteSpace(request.Headers.Authorization)
            ? new Services.Mcp.BentoSelfClient.Credentials("Authorization", request.Headers.Authorization.ToString())
            : !string.IsNullOrWhiteSpace(request.Headers["X-API-Key"])
                ? new Services.Mcp.BentoSelfClient.Credentials("X-API-Key", request.Headers["X-API-Key"].ToString())
                : new Services.Mcp.BentoSelfClient.Credentials(null, null);

        // OAuth apps: the permissions ticked on the consent page (already capped by the role);
        // API keys: their scope; a session or open mode: everything the role allows.
        var permissions = method switch
        {
            "oauth" => (IReadOnlyList<string>)(user.FindFirstValue("oauth_permissions") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries),
            "api_key" => McpPermissions.ForApiKeyScope(role == "api" ? ApiKeyService.ReadWriteScope : ApiKeyService.ReadScope),
            _ => McpPermissions.ForRole(role)
        };

        var grantId = long.TryParse(user.FindFirstValue("oauth_grant_id"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var grant) ? grant : (long?)null;
        return new McpCaller(userId, via, credential, permissions.ToHashSet(StringComparer.Ordinal), credentials, grantId);
    }
}
