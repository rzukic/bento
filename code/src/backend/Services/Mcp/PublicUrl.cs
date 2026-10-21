using Bento.Api.Configuration;

namespace Bento.Api.Services.Mcp;

/// <summary>
/// The address clients reach Bento at: <c>PUBLIC_URL</c> when set, else the
/// request's own scheme and host (after trusted forwarded headers). Behind a
/// proxy that terminates HTTPS without forwarding the scheme, only
/// <c>PUBLIC_URL</c> gives the right answer.
/// </summary>
public static class PublicUrl
{
    public static string For(HttpRequest request, BackendRuntimeOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.PublicUrl))
            return options.PublicUrl.Trim().TrimEnd('/');
        return $"{request.Scheme}://{request.Host.Value}".TrimEnd('/');
    }

    /// <summary>The MCP address of the current workspace: /mcp for the default one, /mcp/&lt;slug&gt; for others.</summary>
    public static string McpEndpoint(HttpRequest request, BackendRuntimeOptions options) =>
        For(request, options) + "/mcp" + (Workspaces.WorkspaceContext.Current.IsDefault ? "" : "/" + Workspaces.WorkspaceContext.Current.Slug);

    public static string ResourceMetadata(HttpRequest request, BackendRuntimeOptions options) =>
        For(request, options) + "/.well-known/oauth-protected-resource" +
        (request.HttpContext.Items.TryGetValue(Middleware.WorkspaceMiddleware.AddressItem, out var slug) && slug is string name && request.Path.StartsWithSegments("/mcp")
            ? "/mcp/" + Uri.EscapeDataString(name)
            : "");
}
