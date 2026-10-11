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

    public static string McpEndpoint(HttpRequest request, BackendRuntimeOptions options) => For(request, options) + "/mcp";

    public static string ResourceMetadata(HttpRequest request, BackendRuntimeOptions options) =>
        For(request, options) + "/.well-known/oauth-protected-resource";
}
