using Bento.Api.Models;
using Bento.Api.Services.Workspaces;

namespace Bento.Api.Middleware;

/// <summary>
/// Picks the workspace a request works in (Step 5.5): <c>/api/v1/w/&lt;slug&gt;/…</c>
/// and <c>/mcp/&lt;slug&gt;</c> name it in the address (the path is rewritten to
/// the plain one, so routes and permission checks stay as they are), otherwise
/// the <c>X-Bento-Workspace</c> header, otherwise the default workspace — so
/// addresses, keys and MCP connections from before workspaces keep working.
/// </summary>
public sealed class WorkspaceMiddleware
{
    public const string HeaderName = "X-Bento-Workspace";

    /// <summary>The workspace's slug when the address named it (<c>/mcp/finances</c>), for discovery links.</summary>
    public const string AddressItem = "bento.workspace.address";

    private const string ApiPrefix = "/api/v1/w/";
    private const string McpPrefix = "/mcp/";

    private readonly RequestDelegate _next;

    public WorkspaceMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, WorkspaceService workspaces)
    {
        var path = context.Request.Path.Value ?? "/";
        string? slug = null;
        if (path.StartsWith(ApiPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var (name, rest) = Split(path[ApiPrefix.Length..]);
            slug = name;
            context.Request.Path = "/api/v1" + rest;
            context.Items[AddressItem] = name;
        }
        else if (path.StartsWith(McpPrefix, StringComparison.OrdinalIgnoreCase) && path.Length > McpPrefix.Length)
        {
            var (name, rest) = Split(path[McpPrefix.Length..]);
            slug = name;
            context.Request.Path = "/mcp" + rest;
            context.Items[AddressItem] = name;
        }
        else if (context.Request.Headers.TryGetValue(HeaderName, out var header) && !string.IsNullOrWhiteSpace(header.ToString()))
        {
            slug = header.ToString();
        }

        if (slug is null)
        {
            await _next(context);
            return;
        }

        var workspace = workspaces.Find(slug) ?? (await workspaces.ReloadAsync()).FirstOrDefault(w => string.Equals(w.Slug, slug.Trim(), StringComparison.OrdinalIgnoreCase));
        if (workspace is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsJsonAsync(new ApiErrorEnvelope(new ApiError(
                ApiErrorCode.NotFound.ToStringCode(),
                $"There is no workspace '{slug}'.",
                new Dictionary<string, object?> { ["workspace"] = slug, ["requestId"] = context.TraceIdentifier })));
            return;
        }

        context.Response.Headers[HeaderName] = workspace.Slug;
        using (WorkspaceContext.Use(workspace))
            await _next(context);
    }

    private static (string Slug, string Remainder) Split(string tail)
    {
        var slash = tail.IndexOf('/');
        return slash < 0 ? (Uri.UnescapeDataString(tail), "") : (Uri.UnescapeDataString(tail[..slash]), tail[slash..]);
    }
}
