using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using Repere.Api.Configuration;
using Repere.Api.Exceptions;
using Repere.Api.Middleware;

namespace Repere.Tests;

/// <summary>
/// TASK-146: forwarded headers behind a trusted proxy, per-client rate limiting and
/// secure refresh cookies behind TLS termination.
/// </summary>
public sealed class ProxyAwarenessTests
{
    private const string LoopbackProxy = "127.0.0.1";

    [Fact]
    public async Task ForwardedHeaders_FromTrustedProxy_RateLimitClientsIndependently()
    {
        await using var host = await ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Backend:Jwt:Secret"] = "super-secret",
            ["Backend:AuthRateLimitPerMinute"] = "1",
            ["Backend:TrustedProxies:0"] = LoopbackProxy
        });

        using var firstClientA = await PostLoginAsync(host, forwardedFor: "10.0.0.1");
        Assert.Equal(HttpStatusCode.Unauthorized, firstClientA.StatusCode);

        using var secondClientA = await PostLoginAsync(host, forwardedFor: "10.0.0.1");
        Assert.Equal((HttpStatusCode)429, secondClientA.StatusCode);

        // A different forwarded client keeps its own bucket even though the TCP peer is the same.
        using var firstClientB = await PostLoginAsync(host, forwardedFor: "10.0.0.2");
        Assert.Equal(HttpStatusCode.Unauthorized, firstClientB.StatusCode);
    }

    [Fact]
    public async Task ForwardedHeaders_WithoutTrustedProxies_AreIgnoredForRateLimiting()
    {
        await using var host = await ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Backend:Jwt:Secret"] = "super-secret",
            ["Backend:AuthRateLimitPerMinute"] = "1"
        });

        using var firstResponse = await PostLoginAsync(host, forwardedFor: "10.0.0.1");
        Assert.Equal(HttpStatusCode.Unauthorized, firstResponse.StatusCode);

        // Spoofed X-Forwarded-For is ignored: both requests count against the real peer.
        using var spoofedResponse = await PostLoginAsync(host, forwardedFor: "10.0.0.2");
        Assert.Equal((HttpStatusCode)429, spoofedResponse.StatusCode);
    }

    [Fact]
    public async Task ForwardedProto_FromTrustedProxy_MarksRefreshCookieSecure_InDevelopment()
    {
        await using var host = await ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Backend:Jwt:Secret"] = "super-secret",
            ["Backend:TrustedProxies:0"] = LoopbackProxy
        });

        await RegisterBootstrapAdminAsync(host);

        using var response = await PostLoginAsync(host, forwardedFor: "10.0.0.1", forwardedProto: "https");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var refreshCookie = ExtractRefreshCookie(response);
        Assert.Contains("secure", refreshCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ForwardedProto_WithoutTrustedProxies_DoesNotMarkRefreshCookieSecure_InDevelopment()
    {
        await using var host = await ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Backend:Jwt:Secret"] = "super-secret"
        });

        await RegisterBootstrapAdminAsync(host);

        using var response = await PostLoginAsync(host, forwardedFor: "10.0.0.1", forwardedProto: "https");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var refreshCookie = ExtractRefreshCookie(response);
        Assert.DoesNotContain("secure", refreshCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PlainHttpLogin_WithTrustedProxies_KeepsDevelopmentCookieNonSecure()
    {
        await using var host = await ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Backend:Jwt:Secret"] = "super-secret",
            ["Backend:TrustedProxies:0"] = LoopbackProxy
        });

        await RegisterBootstrapAdminAsync(host);

        // The proxy forwards plain http://localhost traffic: no proto upgrade, cookie stays as today.
        using var response = await PostLoginAsync(host, forwardedFor: "10.0.0.1", forwardedProto: "http");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var refreshCookie = ExtractRefreshCookie(response);
        Assert.DoesNotContain("secure", refreshCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AdminHealthDetails_ReportsTrustedProxiesConfigured()
    {
        await using var host = await ApiIntegrationTestHost.StartAsync(
            new Dictionary<string, string?>
            {
                ["Backend:AuthMode"] = "open",
                ["Backend:TrustedProxies:0"] = "172.18.0.0/16"
            });

        var payload = await host.GetJsonAsync<System.Text.Json.JsonElement>("/api/v1/admin/health/details");
        Assert.True(payload.GetProperty("runtime").GetProperty("trusted_proxies_configured").GetBoolean());
    }

    [Fact]
    public void AuthRateLimiter_EvictsExpiredBuckets_AfterWindowPasses()
    {
        var limiter = new AuthRateLimiter();
        var start = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

        for (var i = 0; i < 50; i++)
        {
            Assert.True(limiter.CheckRequest($"POST:/api/v1/auth/login:10.0.0.{i}", 5, start).Allowed);
        }

        Assert.Equal(50, limiter.ActiveBucketCount);

        // Within the window nothing is evicted.
        limiter.CheckRequest("POST:/api/v1/auth/login:10.0.1.1", 5, start.AddSeconds(30));
        Assert.Equal(51, limiter.ActiveBucketCount);

        // Once the window has fully passed the old buckets are swept on the next access.
        var later = start.AddMinutes(2);
        Assert.True(limiter.CheckRequest("POST:/api/v1/auth/login:10.0.2.1", 5, later).Allowed);
        Assert.Equal(1, limiter.ActiveBucketCount);
    }

    [Fact]
    public void AuthRateLimiter_StaysBounded_UnderSustainedTrafficFromManyClients()
    {
        var limiter = new AuthRateLimiter();
        var start = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

        // 10 minutes of traffic, a new client IP every second.
        for (var second = 0; second < 600; second++)
        {
            limiter.CheckRequest($"POST:/api/v1/auth/login:10.{second / 256}.{second % 256}.1", 5, start.AddSeconds(second));
        }

        // At most two windows' worth of distinct clients can be resident at once.
        Assert.InRange(limiter.ActiveBucketCount, 1, 120);
    }

    [Fact]
    public void AuthRateLimiter_KeepsLimitSemantics_WhileSweeping()
    {
        var limiter = new AuthRateLimiter();
        var start = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

        Assert.True(limiter.CheckRequest("k", 2, start).Allowed);
        Assert.True(limiter.CheckRequest("k", 2, start.AddSeconds(10)).Allowed);

        var denied = limiter.CheckRequest("k", 2, start.AddSeconds(20));
        Assert.False(denied.Allowed);
        Assert.Equal(40, denied.RetryAfterSeconds);

        Assert.True(limiter.CheckRequest("k", 2, start.AddSeconds(60)).Allowed);
    }

    [Fact]
    public async Task AuthRateLimitMiddleware_CoversSetupPostRoutes_WithSameBucketStyleAsAuth()
    {
        var limiter = new AuthRateLimiter();
        var middleware = new AuthRateLimitMiddleware(
            _ => Task.CompletedTask,
            CreateRuntimeOptions(authRateLimitPerMinute: 1),
            limiter);

        var first = CreateHttpContext(HttpMethods.Post, "/api/v1/setup/sample-data", "10.0.0.1");
        await middleware.InvokeAsync(first);

        var second = CreateHttpContext(HttpMethods.Post, "/api/v1/setup/sample-data", "10.0.0.1");
        var ex = await Assert.ThrowsAsync<ApiTooManyRequestsException>(() => middleware.InvokeAsync(second));
        Assert.Contains("Too many auth attempts", ex.Message, StringComparison.Ordinal);

        // Another client is not affected.
        var otherClient = CreateHttpContext(HttpMethods.Post, "/api/v1/setup/sample-data", "10.0.0.2");
        await middleware.InvokeAsync(otherClient);

        // The prefix match is case-insensitive, like the auth routes.
        await middleware.InvokeAsync(CreateHttpContext(HttpMethods.Post, "/API/v1/Setup/sample-data", "10.0.0.3"));
        await Assert.ThrowsAsync<ApiTooManyRequestsException>(
            () => middleware.InvokeAsync(CreateHttpContext(HttpMethods.Post, "/API/v1/Setup/sample-data", "10.0.0.3")));
    }

    [Fact]
    public async Task AuthRateLimitMiddleware_DoesNotLimitSetupStatusReads()
    {
        var middleware = new AuthRateLimitMiddleware(
            _ => Task.CompletedTask,
            CreateRuntimeOptions(authRateLimitPerMinute: 1),
            new AuthRateLimiter());

        for (var i = 0; i < 3; i++)
        {
            await middleware.InvokeAsync(CreateHttpContext(HttpMethods.Get, "/api/v1/setup/status", "10.0.0.1"));
        }
    }

    [Fact]
    public async Task PlainHttpLogin_InProduction_BehindTrustedProxy_KeepsCookieNonSecure()
    {
        // The packaged full stack runs the backend in Production and is reached over
        // plain HTTP via the proxy (DOMAIN=localhost, http://<lan-ip>). A Secure cookie
        // would be dropped by browsers on anything but localhost.
        await using var host = await ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Backend:Jwt:Secret"] = "super-secret",
            ["Backend:TrustedProxies:0"] = LoopbackProxy
        }, environmentName: "Production");

        await RegisterBootstrapAdminAsync(host);

        using var response = await PostLoginAsync(host, forwardedFor: "10.0.0.1", forwardedProto: "http");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var refreshCookie = ExtractRefreshCookie(response);
        Assert.DoesNotContain("secure", refreshCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HttpsLogin_InProduction_BehindTrustedProxy_MarksCookieSecure()
    {
        await using var host = await ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Backend:Jwt:Secret"] = "super-secret",
            ["Backend:TrustedProxies:0"] = LoopbackProxy
        }, environmentName: "Production");

        await RegisterBootstrapAdminAsync(host);

        using var response = await PostLoginAsync(host, forwardedFor: "10.0.0.1", forwardedProto: "https");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var refreshCookie = ExtractRefreshCookie(response);
        Assert.Contains("secure", refreshCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ForcedSecureCookie_StaysSecure_OnPlainHttp()
    {
        await using var host = await ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Backend:Jwt:Secret"] = "super-secret",
            ["Backend:AuthCookie:RequireSecure"] = "true"
        });

        await RegisterBootstrapAdminAsync(host);

        using var response = await PostLoginAsync(host, forwardedFor: "10.0.0.1", forwardedProto: "http");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var refreshCookie = ExtractRefreshCookie(response);
        Assert.Contains("secure", refreshCookie, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task RegisterBootstrapAdminAsync(ApiIntegrationTestHost host)
    {
        using var registerResponse = await host.Client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            username = "bootstrap-admin",
            password = "supersecure"
        });
        Assert.Equal(HttpStatusCode.Created, registerResponse.StatusCode);
    }

    private static async Task<HttpResponseMessage> PostLoginAsync(
        ApiIntegrationTestHost host,
        string forwardedFor,
        string? forwardedProto = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = JsonContent.Create(new
            {
                username = "bootstrap-admin",
                password = "supersecure"
            })
        };
        request.Headers.Add("X-Forwarded-For", forwardedFor);
        if (forwardedProto is not null)
        {
            request.Headers.Add("X-Forwarded-Proto", forwardedProto);
        }

        return await host.Client.SendAsync(request);
    }

    private static string ExtractRefreshCookie(HttpResponseMessage response)
    {
        Assert.True(response.Headers.TryGetValues("Set-Cookie", out var cookieValues));
        var refreshCookie = Assert.Single(cookieValues);
        Assert.Contains("repere_refresh=", refreshCookie, StringComparison.Ordinal);
        return refreshCookie;
    }

    private static DefaultHttpContext CreateHttpContext(string method, string path, string remoteIp)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
        return context;
    }

    private static BackendRuntimeOptions CreateRuntimeOptions(int authRateLimitPerMinute)
    {
        return new BackendRuntimeOptions
        {
            Port = 5000,
            LogLevel = "Information",
            MaxBodySize = "10mb",
            AuthMode = BackendAuthMode.Standard,
            AuthRateLimitPerMinute = authRateLimitPerMinute,
            DataDir = "./data"
        };
    }
}
