using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bento.Api.Formula.Sandbox;

/// <summary>Limits for <c>fetch</c> in custom functions (Step 4.2, Part 5 R5.6).</summary>
public sealed record JsNetworkOptions
{
    public int MaxRequestsPerCall { get; init; } = 20;
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public long MaxResponseBytes { get; init; } = 2 * 1024 * 1024;
    public int MaxRedirects { get; init; } = 3;

    /// <summary>
    /// Off by default: requests to loopback, private, link-local and other
    /// non-public addresses are refused, and only https is allowed (tests turn it on).
    /// </summary>
    public bool AllowPrivateNetwork { get; init; }
}

/// <summary>The outcome of one request, as the sandbox's fetch sees it.</summary>
public sealed record JsFetchResult(int Status, string StatusText, string Url, Dictionary<string, string> Headers, string Body);

/// <summary>
/// HTTP for custom functions: only hosts on the function's list, never the
/// server's own network (checked on the resolved address, so DNS tricks do not
/// help), redirects checked hop by hop, bodies capped. One instance per app.
/// </summary>
public sealed class JsNetwork : IDisposable
{
    private static readonly HashSet<string> Methods = new(StringComparer.OrdinalIgnoreCase) { "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD" };
    private static readonly HashSet<string> BlockedHeaders = new(StringComparer.OrdinalIgnoreCase) { "Host", "Content-Length", "Connection", "Transfer-Encoding" };

    private readonly HttpClient _client;

    public JsNetwork(JsNetworkOptions options)
    {
        Options = options;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectCallback = ConnectAsync
        };
        _client = new HttpClient(handler) { Timeout = options.RequestTimeout };
    }

    public JsNetworkOptions Options { get; }

    /// <summary>Runs a request for a function: <paramref name="optionsJson"/> is fetch's second argument as JSON.</summary>
    public JsFetchResult Fetch(string url, string? optionsJson, IReadOnlyList<string> allowedHosts)
    {
        string method = "GET";
        string? body = null;
        var headers = new List<KeyValuePair<string, string>>();
        if (!string.IsNullOrWhiteSpace(optionsJson) && JsonNode.Parse(optionsJson) is JsonObject options)
        {
            if (options["method"]?.GetValue<string>() is { } m)
                method = m.ToUpperInvariant();
            if (options["body"] is { } b)
                body = b is JsonValue v && v.TryGetValue<string>(out var text) ? text : b.ToJsonString();
            if (options["headers"] is JsonObject h)
                headers.AddRange(h.Select(p => new KeyValuePair<string, string>(p.Key, p.Value?.ToString() ?? "")));
        }
        if (!Methods.Contains(method))
            throw new JsFetchException($"fetch does not support the method {method}.");

        var target = Check(url, allowedHosts);
        for (var hop = 0; ; hop++)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), target);
            foreach (var (name, value) in headers)
            {
                if (BlockedHeaders.Contains(name) || name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
                    continue;
                request.Headers.TryAddWithoutValidation(name, value);
            }
            if (body is not null && method is not ("GET" or "HEAD"))
            {
                var type = headers.FirstOrDefault(h => string.Equals(h.Key, "Content-Type", StringComparison.OrdinalIgnoreCase)).Value ?? "application/json";
                request.Content = new StringContent(body, Encoding.UTF8);
                request.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(type);
            }
            if (!request.Headers.UserAgent.Any())
                request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (compatible; Bento)");

            HttpResponseMessage response;
            try
            {
                response = _client.Send(request, HttpCompletionOption.ResponseHeadersRead);
            }
            catch (TaskCanceledException)
            {
                throw new JsFetchException($"{target.Host} did not answer within {Options.RequestTimeout.TotalSeconds:0} s.");
            }
            catch (HttpRequestException e)
            {
                throw new JsFetchException(e.InnerException is JsFetchException blocked ? blocked.Message : $"{target.Host} could not be reached: {e.Message}");
            }

            using (response)
            {
                var status = (int)response.StatusCode;
                if (status is >= 300 and < 400 && response.Headers.Location is { } location)
                {
                    if (hop >= Options.MaxRedirects)
                        throw new JsFetchException($"Too many redirects from {target.Host}.");
                    target = Check(new Uri(target, location).ToString(), allowedHosts);
                    if (status is 301 or 302 or 303 && method != "HEAD")
                    {
                        method = "GET";
                        body = null;
                    }
                    continue;
                }

                var text = ReadBody(response, target);
                var responseHeaders = response.Headers.Concat(response.Content.Headers)
                    .GroupBy(h => h.Key.ToLowerInvariant())
                    .ToDictionary(g => g.Key, g => string.Join(", ", g.SelectMany(h => h.Value)));
                return new JsFetchResult(status, response.ReasonPhrase ?? "", target.ToString(), responseHeaders, text);
            }
        }
    }

    private string ReadBody(HttpResponseMessage response, Uri target)
    {
        if (response.Content.Headers.ContentLength > Options.MaxResponseBytes)
            throw new JsFetchException($"The answer from {target.Host} is larger than {Options.MaxResponseBytes / 1024 / 1024} MB.");
        using var stream = response.Content.ReadAsStream();
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > Options.MaxResponseBytes)
                throw new JsFetchException($"The answer from {target.Host} is larger than {Options.MaxResponseBytes / 1024 / 1024} MB.");
        }
        var charset = response.Content.Headers.ContentType?.CharSet;
        Encoding encoding;
        try
        {
            encoding = string.IsNullOrWhiteSpace(charset) ? Encoding.UTF8 : Encoding.GetEncoding(charset.Trim('"'));
        }
        catch (ArgumentException)
        {
            encoding = Encoding.UTF8;
        }
        return encoding.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    /// <summary>The URL when its scheme and host are allowed.</summary>
    private Uri Check(string url, IReadOnlyList<string> allowedHosts)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new JsFetchException($"'{url}' is not a full URL (https://…).");
        if (uri.Scheme != Uri.UriSchemeHttps && !(Options.AllowPrivateNetwork && uri.Scheme == Uri.UriSchemeHttp))
            throw new JsFetchException($"fetch only calls https URLs, not {uri.Scheme}.");
        if (!CustomFunctionRules.HostAllowed(uri.Host, allowedHosts))
            throw new JsFetchException($"{uri.Host} is not on this function's list of hosts ({string.Join(", ", allowedHosts)}).");
        return uri;
    }

    /// <summary>Connects only to public addresses (unless private networks are allowed).</summary>
    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
        var usable = Options.AllowPrivateNetwork ? addresses : addresses.Where(IsPublic).ToArray();
        if (usable.Length == 0)
            throw new JsFetchException($"{context.DnsEndPoint.Host} is not a public address; functions cannot call the server's own network.");

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(usable, context.DnsEndPoint.Port, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            return false;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return !(b[0] == 10 || b[0] == 0 || b[0] >= 224 ||
                     (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
                     (b[0] == 192 && b[1] == 168) ||
                     (b[0] == 169 && b[1] == 254) ||
                     (b[0] == 100 && b[1] >= 64 && b[1] <= 127));
        }
        return !(address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast ||
                 (address.GetAddressBytes()[0] & 0xFE) == 0xFC);
    }

    public void Dispose() => _client.Dispose();
}

/// <summary>A request the sandbox refused or that failed; the function sees it as a rejected fetch.</summary>
public sealed class JsFetchException(string message) : Exception(message);

/// <summary>What a function may use beyond plain JavaScript: the network and its credentials.</summary>
public sealed record JsHost(JsNetwork? Network, IReadOnlyDictionary<string, string> Secrets)
{
    public static readonly JsHost None = new(null, new Dictionary<string, string>());

    public static string SerializeResult(JsFetchResult result) => JsonSerializer.Serialize(new
    {
        status = result.Status,
        statusText = result.StatusText,
        url = result.Url,
        headers = result.Headers,
        body = result.Body
    });
}
