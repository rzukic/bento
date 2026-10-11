using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bento.Api.Models;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Bento.Api.Services.Notifications;

/// <summary>A channel ready to send: its kind, plain settings and decrypted secrets.</summary>
public sealed record ResolvedChannel(string Name, string Kind, NotificationChannelConfig Config, string? Password, string? Url, string? Secret);

/// <summary>A send that did not work, with a message for people (wrong password, webhook gone, …).</summary>
public sealed class NotificationSendException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Sends one notification over one channel (Step 5.2): an email over SMTP
/// (MailKit: STARTTLS, TLS on connect, or plain for a local relay), a Slack
/// incoming webhook (also Mattermost and other Slack-compatible ones), or a
/// JSON webhook signed with HMAC-SHA256. Channels are set up by admins, so a
/// relay or webhook in the server's own network is allowed.
/// </summary>
public sealed class NotificationSender : IDisposable
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    // Umlauts and quotes as they are, not \u escapes: easier to read in n8n or a log.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly HttpClient _http = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = Timeout };

    public async Task SendAsync(ResolvedChannel channel, Notification notification, CancellationToken cancellationToken)
    {
        switch (channel.Kind)
        {
            case "email":
                await SendEmailAsync(channel, notification, cancellationToken);
                break;
            case "slack":
                await PostAsync(channel.Url, JsonSerializer.Serialize(new { text = SlackText(notification) }, Json), null, notification.Event, cancellationToken);
                break;
            case "webhook":
                var body = JsonSerializer.Serialize(new
                {
                    @event = notification.Event,
                    title = notification.Title,
                    text = notification.Text,
                    link = notification.Link,
                    facts = notification.Facts ?? [],
                    channel = channel.Name,
                    sentAt = DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture)
                }, Json);
                await PostAsync(channel.Url, body, channel.Secret, notification.Event, cancellationToken);
                break;
            default:
                throw new NotificationSendException($"Unknown channel kind '{channel.Kind}'.");
        }
    }

    // ── Email ────────────────────────────────────────────────────────────

    private static async Task SendEmailAsync(ResolvedChannel channel, Notification notification, CancellationToken cancellationToken)
    {
        var config = channel.Config;
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(string.IsNullOrWhiteSpace(config.From) ? config.SmtpUser! : config.From));
        foreach (var to in config.To ?? [])
            message.To.Add(MailboxAddress.Parse(to));
        message.Subject = $"Bento: {notification.Title}";
        message.Headers.Add("X-Bento-Event", notification.Event);
        message.Body = new BodyBuilder { TextBody = PlainText(notification), HtmlBody = Html(notification) }.ToMessageBody();

        var security = config.SmtpSecurity switch
        {
            "ssl" => SecureSocketOptions.SslOnConnect,
            "none" => SecureSocketOptions.None,
            _ => SecureSocketOptions.StartTls
        };
        using var client = new SmtpClient { Timeout = (int)Timeout.TotalMilliseconds };
        try
        {
            await client.ConnectAsync(config.SmtpHost, config.SmtpPort ?? DefaultPort(config.SmtpSecurity), security, cancellationToken);
            if (!string.IsNullOrEmpty(config.SmtpUser))
                await client.AuthenticateAsync(config.SmtpUser, channel.Password ?? "", cancellationToken);
            await client.SendAsync(message, cancellationToken);
            await client.DisconnectAsync(true, cancellationToken);
        }
        catch (AuthenticationException e)
        {
            throw new NotificationSendException($"{config.SmtpHost} did not accept the user name or password: {e.Message}", e);
        }
        catch (SslHandshakeException e)
        {
            throw new NotificationSendException($"No secure connection to {config.SmtpHost}: {e.Message} Check the port and the security setting (587 with STARTTLS, 465 with TLS).", e);
        }
        catch (SmtpCommandException e)
        {
            throw new NotificationSendException($"{config.SmtpHost} refused the email ({(int)e.StatusCode}): {e.Message}", e);
        }
        catch (Exception e) when (e is SmtpProtocolException or IOException or System.Net.Sockets.SocketException or TimeoutException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new NotificationSendException($"{config.SmtpHost}:{config.SmtpPort ?? DefaultPort(config.SmtpSecurity)} could not be reached: {e.Message}", e);
        }
    }

    public static int DefaultPort(string? security) => security switch { "ssl" => 465, "none" => 25, _ => 587 };

    // ── Webhooks ─────────────────────────────────────────────────────────

    private async Task PostAsync(string? url, string body, string? secret, string eventName, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var target))
            throw new NotificationSendException("The channel has no webhook address.");
        using var request = new HttpRequestMessage(HttpMethod.Post, target) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Bento", "1.0"));
        request.Headers.Add("X-Bento-Event", eventName);
        if (!string.IsNullOrEmpty(secret))
            request.Headers.Add("X-Bento-Signature", "sha256=" + Signature(secret, body));

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken);
        }
        catch (TaskCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            throw new NotificationSendException($"{target.Host} did not answer within {Timeout.TotalSeconds:0} s.", e);
        }
        catch (HttpRequestException e)
        {
            throw new NotificationSendException($"{target.Host} could not be reached: {e.Message}", e);
        }
        using (response)
        {
            if (response.IsSuccessStatusCode)
                return;
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            text = text.Length > 200 ? text[..200] + "…" : text;
            throw new NotificationSendException((int)response.StatusCode switch
            {
                >= 300 and < 400 => $"{target.Host} redirected the request ({(int)response.StatusCode}); use the final address.",
                403 or 404 or 410 when target.Host.Contains("slack", StringComparison.OrdinalIgnoreCase) =>
                    $"Slack refused the webhook ({(int)response.StatusCode} {text}); it was probably removed. Create a new one and update the channel.",
                _ => $"{target.Host} answered {(int)response.StatusCode} {response.ReasonPhrase}{(text.Length > 0 ? $": {text}" : "")}"
            });
        }
    }

    /// <summary>Hex HMAC-SHA256 of the body with the channel's secret: the receiver checks X-Bento-Signature with it.</summary>
    public static string Signature(string secret, string body) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body))).ToLowerInvariant();

    // ── Text ─────────────────────────────────────────────────────────────

    private static string PlainText(Notification notification)
    {
        var text = new StringBuilder(notification.Text).AppendLine();
        if (notification.Facts is { Count: > 0 } facts)
        {
            text.AppendLine();
            foreach (var fact in facts)
                text.Append(fact.Label).Append(": ").AppendLine(fact.Value);
        }
        if (notification.Link is not null)
            text.AppendLine().AppendLine(notification.Link);
        return text.ToString();
    }

    private static string Html(Notification notification)
    {
        var html = new StringBuilder("<div style=\"font-family:system-ui,sans-serif;font-size:14px;line-height:1.5\">")
            .Append("<p style=\"margin:0 0 12px\"><strong>").Append(WebUtility.HtmlEncode(notification.Title)).Append("</strong></p>")
            .Append("<p style=\"margin:0 0 12px;white-space:pre-line\">").Append(WebUtility.HtmlEncode(notification.Text)).Append("</p>");
        if (notification.Facts is { Count: > 0 } facts)
        {
            html.Append("<table style=\"border-collapse:collapse;margin:0 0 12px\">");
            foreach (var fact in facts)
                html.Append("<tr><td style=\"padding:2px 12px 2px 0;color:#666\">").Append(WebUtility.HtmlEncode(fact.Label))
                    .Append("</td><td style=\"padding:2px 0\">").Append(WebUtility.HtmlEncode(fact.Value)).Append("</td></tr>");
            html.Append("</table>");
        }
        if (notification.Link is not null)
            html.Append("<p style=\"margin:0\"><a href=\"").Append(WebUtility.HtmlEncode(notification.Link)).Append("\">Open in Bento</a></p>");
        return html.Append("</div>").ToString();
    }

    /// <summary>Slack's mrkdwn: the title in bold, the text, the facts, a link. &amp;, &lt; and &gt; escaped as Slack asks.</summary>
    private static string SlackText(Notification notification)
    {
        static string Escape(string text) => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        var text = new StringBuilder("*").Append(Escape(notification.Title)).Append("*\n").Append(Escape(notification.Text));
        foreach (var fact in notification.Facts ?? [])
            text.Append('\n').Append(Escape(fact.Label)).Append(": ").Append(Escape(fact.Value));
        if (notification.Link is not null)
            text.Append("\n<").Append(notification.Link).Append("|Open in Bento>");
        return text.ToString();
    }

    public void Dispose() => _http.Dispose();
}
