using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Bento.Api.Exceptions;
using Bento.Api.Formula.Sandbox;

namespace Bento.Api.Services.Import;

/// <summary>Limits for fetching import files (Step 5.1).</summary>
public sealed record ImportFetchOptions
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
    public long MaxBytes { get; init; } = 20 * 1024 * 1024;
    public int MaxRedirects { get; init; } = 5;
    /// <summary>Off by default: only https and public addresses (tests turn it on).</summary>
    public bool AllowPrivateNetwork { get; init; }
}

/// <summary>A header sent with the request; <c>{{name}}</c> in the value is a credential from the vault.</summary>
public sealed record ImportHeader
{
    public string? Name { get; init; }
    public string? Value { get; init; }
}

/// <summary>A fetched file: its bytes, a name for format detection, and where it came from in the end.</summary>
public sealed record ImportFetchResult(byte[] Bytes, string FileName, string? ContentType, string FinalUrl);

/// <summary>
/// Fetches an export from a URL for the import: https only, never the
/// server's own network (checked on the resolved address, so DNS tricks and
/// redirects to internal hosts do not help), a size and time limit, headers
/// with credentials from the vault. Unlike <c>fetch</c> in functions the host
/// is not on a list: the URL is the import's own, set by an editor.
/// </summary>
public sealed partial class ImportFetcher : IDisposable
{
    private static readonly HashSet<string> BlockedHeaders = new(StringComparer.OrdinalIgnoreCase) { "Host", "Content-Length", "Connection", "Transfer-Encoding", "Cookie" };

    private readonly HttpClient _client;
    private readonly CredentialService _credentials;

    public ImportFetcher(ImportFetchOptions options, CredentialService credentials)
    {
        Options = options;
        _credentials = credentials;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectCallback = ConnectAsync
        };
        _client = new HttpClient(handler) { Timeout = options.Timeout };
    }

    public ImportFetchOptions Options { get; }

    /// <summary>The credential names a URL and its headers use (<c>{{name}}</c>).</summary>
    public static IReadOnlyList<string> CredentialNames(string? url, IEnumerable<ImportHeader>? headers) =>
        new[] { url }.Concat((headers ?? []).Select(h => h.Value))
            .Where(text => text is not null)
            .SelectMany(text => Placeholder().Matches(text!).Select(m => m.Groups[1].Value.Trim()))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>Checks a URL and headers without fetching: a full https URL, header names that may be sent.</summary>
    public void Validate(string? url, IReadOnlyList<ImportHeader>? headers)
    {
        CheckUrl(url, "url");
        foreach (var (header, index) in (headers ?? []).Select((h, i) => (h, i)))
        {
            if (string.IsNullOrWhiteSpace(header.Name) || header.Name.Any(c => c <= ' ' || c == ':'))
                throw Invalid($"headers[{index}].name", "A header needs a name without spaces or colons, e.g. Authorization.");
            if (BlockedHeaders.Contains(header.Name.Trim()))
                throw Invalid($"headers[{index}].name", $"The header {header.Name.Trim()} is set by Bento.");
        }
    }

    public async Task<ImportFetchResult> FetchAsync(string? url, IReadOnlyList<ImportHeader>? headers, CancellationToken cancellationToken = default)
    {
        Validate(url, headers);
        var secrets = await _credentials.ResolveAsync(CredentialNames(url, headers));
        string Fill(string text) => Placeholder().Replace(text, m => secrets[m.Groups[1].Value.Trim()]);

        var target = CheckUrl(Fill(url!.Trim()), "url");
        for (var hop = 0; ; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, target);
            // Credentials go only to the URL's own host, not to where it redirects.
            if (hop == 0 || string.Equals(target.Host, new Uri(Fill(url.Trim())).Host, StringComparison.OrdinalIgnoreCase))
                foreach (var header in headers ?? [])
                    request.Headers.TryAddWithoutValidation(header.Name!.Trim(), Fill(header.Value ?? ""));
            if (!request.Headers.UserAgent.Any())
                request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (compatible; Bento import)");

            HttpResponseMessage response;
            try
            {
                response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw Unavailable($"{target.Host} did not answer within {Options.Timeout.TotalSeconds:0} s.");
            }
            catch (HttpRequestException e)
            {
                throw Unavailable(e.InnerException is ImportFetchBlocked blocked ? blocked.Message : $"{target.Host} could not be reached: {e.Message}");
            }

            using (response)
            {
                var status = (int)response.StatusCode;
                if (status is >= 300 and < 400 && response.Headers.Location is { } location)
                {
                    if (hop >= Options.MaxRedirects)
                        throw Unavailable($"Too many redirects from {target.Host}.");
                    target = CheckUrl(new Uri(target, location).ToString(), "url");
                    continue;
                }
                if (status is 401 or 403)
                    throw Unavailable($"{target.Host} refused the request ({status}): the URL or its credentials are not (or no longer) valid.");
                if (status >= 400)
                    throw Unavailable($"{target.Host} answered {status} {response.ReasonPhrase}.");

                var bytes = await ReadAsync(response, target, cancellationToken);
                if (bytes.Length == 0)
                    throw Unavailable($"{target.Host} sent an empty file.");
                return new ImportFetchResult(bytes, FileNameOf(response, target), response.Content.Headers.ContentType?.MediaType, target.ToString());
            }
        }
    }

    private async Task<byte[]> ReadAsync(HttpResponseMessage response, Uri target, CancellationToken cancellationToken)
    {
        var tooLarge = $"The file at {target.Host} is larger than {Options.MaxBytes / 1024 / 1024} MB.";
        if (response.Content.Headers.ContentLength > Options.MaxBytes)
            throw Unavailable(tooLarge);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > Options.MaxBytes)
                throw Unavailable(tooLarge);
        }
        return buffer.ToArray();
    }

    /// <summary>The name from Content-Disposition, else the URL's last path segment, else by content type.</summary>
    private static string FileNameOf(HttpResponseMessage response, Uri target)
    {
        var disposition = response.Content.Headers.ContentDisposition;
        var fromHeader = (disposition?.FileNameStar ?? disposition?.FileName)?.Trim('"', ' ');
        if (!string.IsNullOrWhiteSpace(fromHeader))
            return Path.GetFileName(fromHeader);
        var last = Uri.UnescapeDataString(target.Segments.LastOrDefault() ?? "").Trim('/');
        if (Path.HasExtension(last))
            return last;
        var extension = response.Content.Headers.ContentType?.MediaType switch
        {
            "application/json" or "text/json" => ".json",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" => ".xlsx",
            _ => ".csv"
        };
        return (string.IsNullOrEmpty(last) ? "download" : last) + extension;
    }

    private Uri CheckUrl(string? url, string field)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            throw Invalid(field, "Enter the full address of the file, starting with https://.");
        if (uri.Scheme != Uri.UriSchemeHttps && !(Options.AllowPrivateNetwork && uri.Scheme == Uri.UriSchemeHttp))
            throw Invalid(field, $"Only https addresses can be fetched, not {uri.Scheme}.");
        if (!string.IsNullOrEmpty(uri.UserInfo))
            throw Invalid(field, "Put user names and passwords in a header from the vault, not in the address.");
        return uri;
    }

    /// <summary>Connects only to public addresses (unless private networks are allowed).</summary>
    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
        var usable = Options.AllowPrivateNetwork ? addresses : addresses.Where(JsNetwork.IsPublic).ToArray();
        if (usable.Length == 0)
            throw new ImportFetchBlocked($"{context.DnsEndPoint.Host} is not a public address; imports cannot fetch from the server's own network.");
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

    [GeneratedRegex(@"\{\{\s*([A-Za-z0-9_.\-]+)\s*\}\}")]
    private static partial Regex Placeholder();

    private static ApiValidationException Invalid(string field, string message) =>
        new(message, new Dictionary<string, object?> { ["field"] = field });

    /// <summary>The source could not be fetched: a 422 the caller shows as is.</summary>
    private static ApiUnprocessableEntityException Unavailable(string message) => new(message);

    public void Dispose() => _client.Dispose();

    private sealed class ImportFetchBlocked(string message) : Exception(message);
}
