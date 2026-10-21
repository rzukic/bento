using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace Bento.Api.Services.Mcp;

/// <summary>
/// Calls Bento's own REST API over the loopback interface with the MCP caller's
/// credentials. Every MCP tool is a REST call, so roles, key scopes, validation
/// and the follow-up work of writes (recalculation, change events) are exactly
/// those of the app — nothing is re-implemented for MCP.
/// </summary>
public sealed class BentoSelfClient : IDisposable
{
    private readonly IServer _server;
    private readonly HttpClient _http;
    private Uri? _baseAddress;

    public BentoSelfClient(IServer server)
    {
        _server = server;
        _http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(60)
        };
    }

    public sealed record ApiResponse(int Status, JsonElement? Body, string RawBody)
    {
        public bool IsSuccess => Status is >= 200 and < 300;
    }

    /// <summary>Who the inner request runs as: the header the MCP request carried (none in open auth mode).</summary>
    public sealed record Credentials(string? HeaderName, string? HeaderValue);

    public async Task<ApiResponse> SendAsync(HttpMethod method, string pathAndQuery, Credentials credentials, object? body = null, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(method, new Uri(BaseAddress, pathAndQuery));
        if (!string.IsNullOrEmpty(credentials.HeaderName))
            request.Headers.TryAddWithoutValidation(credentials.HeaderName, credentials.HeaderValue);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        // The tools work in the workspace the MCP call came in for (/mcp/<slug>).
        request.Headers.TryAddWithoutValidation(Middleware.WorkspaceMiddleware.HeaderName, Workspaces.WorkspaceContext.Current.Slug);
        if (body is not null)
        {
            var json = body is JsonElement element ? element.GetRawText() : JsonSerializer.Serialize(body);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        using var response = await _http.SendAsync(request, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        JsonElement? parsed = null;
        if (!string.IsNullOrWhiteSpace(raw))
        {
            try
            {
                using var document = JsonDocument.Parse(raw);
                parsed = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                parsed = null;
            }
        }
        return new ApiResponse((int)response.StatusCode, parsed, raw);
    }

    /// <summary>The first address Kestrel listens on, with wildcard hosts pointed at the loopback interface.</summary>
    private Uri BaseAddress
    {
        get
        {
            if (_baseAddress is not null) return _baseAddress;
            var addresses = _server.Features.Get<IServerAddressesFeature>()?.Addresses ?? [];
            var address = addresses.FirstOrDefault(a => a.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                ?? addresses.FirstOrDefault()
                ?? throw new InvalidOperationException("The server has no address to call itself on.");
            var builder = new UriBuilder(address.Replace("://+", "://localhost").Replace("://*", "://localhost"));
            if (builder.Host is "0.0.0.0" or "[::]" or "::" or "localhost")
                builder.Host = "127.0.0.1";
            _baseAddress = builder.Uri;
            return _baseAddress;
        }
    }

    public void Dispose() => _http.Dispose();
}
