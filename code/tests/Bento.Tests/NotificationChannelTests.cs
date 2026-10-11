using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Bento.Api.Models;
using Bento.Api.Services.Notifications;

namespace Bento.Tests;

/// <summary>
/// Step 5.2: email over SMTP, Slack and signed webhooks — secrets stored but
/// never sent back, a test send, a delivery log, and failures that say why.
/// </summary>
public sealed class NotificationChannelTests
{
    private const string Path = "/api/v1/admin/notifications/channels";

    private static Task<ApiIntegrationTestHost> StartAsync() => ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?>
    {
        ["Backend:AuthMode"] = "open",
        ["Backend:Jwt:Secret"] = "sqlite-test-secret",
        ["Schema:AutoMigrateOnStartup"] = "false"
    });

    [Fact]
    public async Task Email_SendsOverSmtp_AndKeepsThePasswordToItself()
    {
        await using var host = await StartAsync();
        using var smtp = new FakeSmtpServer("bento@example.com", "app-password");
        var created = await PostAsync(host, Path, new
        {
            name = "My email",
            kind = "email",
            config = new { to = new[] { "me@example.com" }, smtpHost = "127.0.0.1", smtpPort = smtp.Port, smtpSecurity = "none", smtpUser = "bento@example.com", smtpPassword = "app-password" }
        });
        var id = created.GetProperty("id").GetInt64();
        Assert.True(created.GetProperty("hasPassword").GetBoolean());
        Assert.Equal("me@example.com", created.GetProperty("target").GetString());
        // The password never comes back.
        Assert.DoesNotContain("app-password", await host.Client.GetStringAsync(Path));

        var test = await PostAsync(host, $"{Path}/{id}/test", new { });
        Assert.True(test.GetProperty("status").GetString() == "sent", test.ToString());
        var mail = Assert.Single(smtp.Messages);
        Assert.Contains("Subject: Bento: Test from Bento", mail.Data);
        Assert.Contains("me@example.com", mail.Recipients);
        Assert.Contains("This is a test of the channel", mail.Data);

        // Left out, the password is kept; a wrong one says so.
        using (var renamed = await host.Client.PutAsJsonAsync($"{Path}/{id}", new { name = "Email", config = new { to = new[] { "me@example.com" }, smtpHost = "127.0.0.1", smtpPort = smtp.Port, smtpSecurity = "none", smtpUser = "bento@example.com" } }))
            renamed.EnsureSuccessStatusCode();
        Assert.Equal("sent", (await PostAsync(host, $"{Path}/{id}/test", new { })).GetProperty("status").GetString());
        using (var wrong = await host.Client.PutAsJsonAsync($"{Path}/{id}", new { config = new { to = new[] { "me@example.com" }, smtpHost = "127.0.0.1", smtpPort = smtp.Port, smtpSecurity = "none", smtpUser = "bento@example.com", smtpPassword = "nope" } }))
            wrong.EnsureSuccessStatusCode();
        var failed = await PostAsync(host, $"{Path}/{id}/test", new { });
        Assert.Equal("failed", failed.GetProperty("status").GetString());
        Assert.Contains("did not accept the user name or password", failed.GetProperty("error").GetString());

        var deliveries = (await host.Client.GetFromJsonAsync<JsonElement>($"{Path}/{id}/deliveries")).GetProperty("data");
        Assert.Equal(["failed", "sent", "sent"], deliveries.EnumerateArray().Select(d => d.GetProperty("status").GetString()));
        Assert.Equal("failed", (await GetAsync(host, $"{Path}/{id}")).GetProperty("lastDelivery").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Slack_AndWebhook_PostJson_TheWebhookSigned() => await SlackAndWebhookAsync(await StartAsync());

    internal static async Task SlackAndWebhookAsync(ApiIntegrationTestHost host)
    {
        await using var _ = host;
        using var receiver = new FakeReceiver();
        var slack = await PostAsync(host, Path, new { name = "Slack", kind = "slack", config = new { url = $"{receiver.Url}/services/T000/B000/secret-token" } });
        Assert.Equal("127.0.0.1", slack.GetProperty("target").GetString());
        Assert.DoesNotContain("secret-token", await host.Client.GetStringAsync(Path));
        Assert.Equal("sent", (await PostAsync(host, $"{Path}/{slack.GetProperty("id")}/test", new { })).GetProperty("status").GetString());
        var slackPost = receiver.Requests.Single();
        Assert.Equal("/services/T000/B000/secret-token", slackPost.Path);
        Assert.StartsWith("*Test from Bento*\n", JsonDocument.Parse(slackPost.Body).RootElement.GetProperty("text").GetString());

        var hook = await PostAsync(host, Path, new { name = "n8n", kind = "webhook", config = new { url = $"{receiver.Url}/hook", secret = "sign-me" } });
        Assert.True(hook.GetProperty("hasSecret").GetBoolean());
        await PostAsync(host, $"{Path}/{hook.GetProperty("id")}/test", new { });
        var post = receiver.Requests.Last();
        var body = JsonDocument.Parse(post.Body).RootElement;
        Assert.Equal("test", body.GetProperty("event").GetString());
        Assert.Equal("n8n", body.GetProperty("channel").GetString());
        Assert.Equal("test", post.Headers["X-Bento-Event"]);
        Assert.Equal("sha256=" + NotificationSender.Signature("sign-me", post.Body), post.Headers["X-Bento-Signature"]);

        // A webhook that answers with an error: the delivery says what came back.
        receiver.Status = 410;
        var gone = await PostAsync(host, $"{Path}/{hook.GetProperty("id")}/test", new { });
        Assert.Contains("answered 410", gone.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Send_GoesOverEnabledChannels_AndLogsFailuresAsProblems() => await SendAsync(await StartAsync());

    internal static async Task SendAsync(ApiIntegrationTestHost host)
    {
        await using var _ = host;
        using var receiver = new FakeReceiver();
        var on = await PostAsync(host, Path, new { name = "On", kind = "webhook", config = new { url = $"{receiver.Url}/on" } });
        var off = await PostAsync(host, Path, new { name = "Off", kind = "webhook", config = new { url = $"{receiver.Url}/off" }, enabled = false });
        var broken = await PostAsync(host, Path, new { name = "Broken", kind = "webhook", config = new { url = "http://127.0.0.1:9/nothing" } });

        var sent = await host.GetService<NotificationService>().SendAsync(
            [on.GetProperty("id").GetInt64(), off.GetProperty("id").GetInt64(), broken.GetProperty("id").GetInt64()],
            new Notification("import.failed", "Import failed", "The bank answered 500.", "https://bento.example/data/transactions/import", [new NotificationFact("Import", "Girokonto")]));

        Assert.Equal(["failed", "sent"], sent.Select(d => d.Status).Order());
        Assert.Equal(["/on"], receiver.Requests.Select(r => r.Path));
        var body = JsonDocument.Parse(receiver.Requests.Single().Body).RootElement;
        Assert.Equal("Girokonto", body.GetProperty("facts")[0].GetProperty("value").GetString());
        var problem = (await host.Client.GetFromJsonAsync<JsonElement>("/api/v1/admin/problems")).GetProperty("data")[0];
        Assert.Equal("notify", problem.GetProperty("kind").GetString());
        Assert.Contains("over Broken", problem.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Channels_AreChecked()
    {
        await using var host = await StartAsync();
        Assert.Contains("at least one address", await FailAsync(host, new { name = "A", kind = "email", config = new { smtpHost = "smtp.example.com", from = "a@example.com" } }));
        Assert.Contains("'nobody' is not an email address", await FailAsync(host, new { name = "A", kind = "email", config = new { to = new[] { "nobody" }, smtpHost = "smtp.example.com", from = "a@example.com" } }));
        Assert.Contains("the emails come from", await FailAsync(host, new { name = "A", kind = "email", config = new { to = new[] { "me@example.com" }, smtpHost = "smtp.example.com", smtpUser = "apikey" } }));
        Assert.Contains("hooks.slack.com", await FailAsync(host, new { name = "A", kind = "slack", config = new { } }));
        Assert.Contains("email, slack or webhook", await FailAsync(host, new { name = "A", kind = "sms" }));

        var hook = await PostAsync(host, Path, new { name = "Hook", kind = "webhook", config = new { url = "https://example.com/hook", secret = "s" } });
        using var kind = await host.Client.PutAsJsonAsync($"{Path}/{hook.GetProperty("id")}", new { kind = "slack" });
        Assert.Equal(HttpStatusCode.BadRequest, kind.StatusCode);
        // "" removes the signing secret.
        using (var unsigned = await host.Client.PutAsJsonAsync($"{Path}/{hook.GetProperty("id")}", new { config = new { secret = "" } }))
            unsigned.EnsureSuccessStatusCode();
        var reloaded = await GetAsync(host, $"{Path}/{hook.GetProperty("id")}");
        Assert.False(reloaded.GetProperty("hasSecret").GetBoolean());
        Assert.True(reloaded.GetProperty("hasUrl").GetBoolean());
    }

    // ── Helpers ──

    private static Task<JsonElement> PostAsync(ApiIntegrationTestHost host, string url, object body) => ImportSourceTests.PostAsync(host, url, body);

    private static async Task<JsonElement> GetAsync(ApiIntegrationTestHost host, string url) =>
        (await host.Client.GetFromJsonAsync<JsonElement>(url)).GetProperty("data");

    private static async Task<string> FailAsync(ApiIntegrationTestHost host, object body)
    {
        using var response = await host.Client.PostAsJsonAsync(Path, body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }
}

/// <summary>Takes HTTP posts and keeps their path, headers and body; answers <see cref="Status"/>.</summary>
public sealed class FakeReceiver : IDisposable
{
    public sealed record Received(string Path, Dictionary<string, string> Headers, string Body);

    private readonly HttpListener _listener = new();

    public FakeReceiver()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        Url = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add(Url + "/");
        _listener.Start();
        _ = Task.Run(async () =>
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch
                {
                    return;
                }
                using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                var body = await reader.ReadToEndAsync();
                var headers = context.Request.Headers.AllKeys.Where(k => k is not null).ToDictionary(k => k!, k => context.Request.Headers[k]!, StringComparer.OrdinalIgnoreCase);
                Requests.Enqueue(new Received(context.Request.Url!.AbsolutePath, headers, body));
                context.Response.StatusCode = Status;
                context.Response.Close();
            }
        });
    }

    public string Url { get; }
    public int Status { get; set; } = 200;
    public ConcurrentQueue<Received> Requests { get; } = new();

    public void Dispose() => _listener.Close();
}

/// <summary>Just enough SMTP for one client: EHLO, AUTH PLAIN, MAIL, RCPT, DATA, QUIT; keeps the messages.</summary>
public sealed class FakeSmtpServer : IDisposable
{
    public sealed record Message(List<string> Recipients, string Data);

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly string _user;
    private readonly string _password;

    public FakeSmtpServer(string user, string password)
    {
        _user = user;
        _password = password;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptAsync);
    }

    public int Port { get; }
    public ConcurrentQueue<Message> Messages { get; } = new();

    private async Task AcceptAsync()
    {
        while (true)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync();
            }
            catch
            {
                return;
            }
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using var _ = client;
        var stream = client.GetStream();
        var reader = new StreamReader(stream, Encoding.ASCII);
        var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
        await writer.WriteLineAsync("220 fake ESMTP");
        var recipients = new List<string>();
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                var command = line.ToUpperInvariant();
                if (command.StartsWith("EHLO") || command.StartsWith("HELO"))
                {
                    await writer.WriteLineAsync("250-fake");
                    await writer.WriteLineAsync("250 AUTH PLAIN");
                }
                else if (command.StartsWith("AUTH PLAIN"))
                {
                    var parts = line.Split(' ');
                    var encoded = parts.Length > 2 ? parts[2] : await Continue(writer, reader);
                    var fields = Encoding.UTF8.GetString(Convert.FromBase64String(encoded)).Split('\0');
                    await writer.WriteLineAsync(fields.Length == 3 && fields[1] == _user && fields[2] == _password ? "235 OK" : "535 Authentication failed");
                }
                else if (command.StartsWith("MAIL FROM"))
                {
                    recipients = [];
                    await writer.WriteLineAsync("250 OK");
                }
                else if (command.StartsWith("RCPT TO"))
                {
                    recipients.Add(line[(line.IndexOf('<') + 1)..line.IndexOf('>')]);
                    await writer.WriteLineAsync("250 OK");
                }
                else if (command == "DATA")
                {
                    await writer.WriteLineAsync("354 Go ahead");
                    var data = new StringBuilder();
                    while (await reader.ReadLineAsync() is { } dataLine && dataLine != ".")
                        data.AppendLine(dataLine);
                    Messages.Enqueue(new Message(recipients, data.ToString()));
                    await writer.WriteLineAsync("250 Queued");
                }
                else if (command == "QUIT")
                {
                    await writer.WriteLineAsync("221 Bye");
                    return;
                }
                else
                {
                    await writer.WriteLineAsync("250 OK");
                }
            }
        }
        catch (IOException)
        {
        }
    }

    private static async Task<string> Continue(StreamWriter writer, StreamReader reader)
    {
        await writer.WriteLineAsync("334 ");
        return await reader.ReadLineAsync() ?? "";
    }

    public void Dispose() => _listener.Stop();
}

[Trait("Category", "Postgres")]
[Collection(PostgresCollection.Name)]
public sealed class PostgresNotificationChannelTests
{
    private static Task<ApiIntegrationTestHost> StartAsync() => ApiIntegrationTestHost.StartPostgresAsync(new Dictionary<string, string?>
    {
        ["Backend:AuthMode"] = "open",
        ["Backend:Jwt:Secret"] = "postgres-test-secret",
        ["Schema:AutoMigrateOnStartup"] = "false"
    });

    [PostgresFact]
    public async Task Slack_AndWebhook_OnPostgres() => await NotificationChannelTests.SlackAndWebhookAsync(await StartAsync());

    [PostgresFact]
    public async Task Send_OnPostgres() => await NotificationChannelTests.SendAsync(await StartAsync());
}
