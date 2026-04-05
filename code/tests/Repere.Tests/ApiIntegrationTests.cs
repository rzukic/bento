using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Repere.Api.Models;

namespace Repere.Tests;

public sealed class ApiIntegrationTests
{
    [Fact]
    public async Task Get_AdminHealth_ReturnsHealthyJson_AndGeneratedRequestId()
    {
        await using var host = await ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Backend:AuthMode"] = "open"
        });

        using var response = await host.Client.GetAsync("/api/v1/admin/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("X-Request-Id", out var values));

        var requestId = Assert.Single(values);
        Assert.False(string.IsNullOrWhiteSpace(requestId));

        var payload = await response.Content.ReadFromJsonAsync<AdminHealthResponse>();
        payload = Assert.IsType<AdminHealthResponse>(payload);

        Assert.Equal("healthy", payload.Status);
        Assert.Equal("connected", payload.Database.SystemPool);
        Assert.Equal("connected", payload.Database.DataPool);
        Assert.False(string.IsNullOrWhiteSpace(payload.Uptime));
        Assert.False(string.IsNullOrWhiteSpace(payload.Version));
        Assert.Null(payload.Error);

        var rawJson = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(rawJson);
        var root = document.RootElement;

        Assert.True(root.TryGetProperty("database", out var database));
        Assert.Equal("connected", database.GetProperty("system_pool").GetString());
        Assert.Equal("connected", database.GetProperty("data_pool").GetString());
        Assert.True(root.TryGetProperty("uptime", out var uptime));
        Assert.False(string.IsNullOrWhiteSpace(uptime.GetString()));
        Assert.True(root.TryGetProperty("version", out var version));
        Assert.False(string.IsNullOrWhiteSpace(version.GetString()));
        Assert.False(root.TryGetProperty("systemPool", out _));
        Assert.False(root.TryGetProperty("dataPool", out _));
    }

    [Fact]
    public async Task Get_AdminHealth_ReusesIncomingRequestId_AndRespondsOnVersionedRoute()
    {
        await using var host = await ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Backend:AuthMode"] = "open"
        });
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/health");
        request.Headers.TryAddWithoutValidation("X-Request-Id", "req_http_integration_123");

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("X-Request-Id", out var values));
        Assert.Equal("req_http_integration_123", Assert.Single(values));
    }

    [Fact]
    public async Task Get_RootHealth_ReturnsNotFound_WhileVersionedHealthExists()
    {
        await using var host = await ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Backend:AuthMode"] = "open"
        });

        using var rootResponse = await host.Client.GetAsync("/health");
        using var versionedResponse = await host.Client.GetAsync("/api/v1/admin/health");

        Assert.Equal(HttpStatusCode.NotFound, rootResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, versionedResponse.StatusCode);
    }

    [Fact]
    public async Task Get_AuthContract_ReturnsTypedAuthSurface_ForStandardMode()
    {
        await using var host = await ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Backend:Jwt:Secret"] = "super-secret",
            ["Backend:Jwt:Issuer"] = "repere-tests",
            ["Backend:Jwt:Audience"] = "repere-clients",
            ["Backend:Jwt:AccessTokenLifetimeMinutes"] = "15",
            ["Backend:Jwt:RefreshTokenLifetimeDays"] = "7",
            ["Backend:AuthCookie:RefreshCookieName"] = "repere_refresh",
            ["Backend:AuthCookie:RequireSecure"] = "true",
            ["Backend:AuthCookie:SameSite"] = "Strict",
            ["Backend:AuthCookie:Path"] = "/api/v1/auth",
            ["Backend:AuthRateLimitPerMinute"] = "5"
        });

        using var response = await host.Client.GetAsync("/api/v1/auth");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<AuthRuntimeContractResponse>();
        payload = Assert.IsType<AuthRuntimeContractResponse>(payload);

        Assert.Equal("standard", payload.Mode);
        Assert.True(payload.AuthRequired);
        Assert.True(payload.RegistrationBootstrapOpen);
        Assert.True(payload.JwtConfigured);
        Assert.Equal("repere-tests", payload.JwtIssuer);
        Assert.Equal("repere-clients", payload.JwtAudience);
        Assert.Equal(15, payload.AccessTokenLifetimeMinutes);
        Assert.Equal(7, payload.RefreshTokenLifetimeDays);
        Assert.Equal(5, payload.AuthRateLimitPerMinute);
        Assert.Equal("repere_refresh", payload.RefreshCookie.Name);
        Assert.True(payload.RefreshCookie.HttpOnly);
        Assert.True(payload.RefreshCookie.RequireSecure);
        Assert.Equal("Strict", payload.RefreshCookie.SameSite);
        Assert.Equal("/api/v1/auth", payload.RefreshCookie.Path);
        Assert.Contains("vault mode", payload.Deferred, StringComparer.Ordinal);
    }

    [Fact]
    public async Task Get_AdminHealth_InStandardMode_RequiresBearerToken()
    {
        await using var host = await ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Backend:Jwt:Secret"] = "super-secret"
        });

        using var response = await host.Client.GetAsync("/api/v1/admin/health");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<ApiErrorEnvelope>();
        payload = Assert.IsType<ApiErrorEnvelope>(payload);
        Assert.Equal("UNAUTHORIZED", payload.Error.Code);
        Assert.Contains("Bearer token is required", payload.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_AdminHealth_InStandardMode_ReturnsForbidden_ForNonAdminRole()
    {
        await using var host = await ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Backend:Jwt:Secret"] = "super-secret"
        });

        await host.Client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            username = "bootstrap-admin",
            password = "supersecure"
        });

        using var createUserRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/register")
        {
            Content = JsonContent.Create(new
            {
                username = "editor-user",
                password = "anotherpass",
                role = "editor"
            })
        };
        createUserRequest.Headers.Authorization = new("Bearer", await LoginAndGetAccessTokenAsync(host, "bootstrap-admin", "supersecure"));

        using var createUserResponse = await host.Client.SendAsync(createUserRequest);
        Assert.Equal(HttpStatusCode.Created, createUserResponse.StatusCode);

        var editorToken = await LoginAndGetAccessTokenAsync(host, "editor-user", "anotherpass");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/health");
        request.Headers.Authorization = new("Bearer", editorToken);

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<ApiErrorEnvelope>();
        payload = Assert.IsType<ApiErrorEnvelope>(payload);
        Assert.Equal("FORBIDDEN", payload.Error.Code);
        Assert.Contains("required", payload.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Get_AdminHealth_InOpenMode_BypassesBearerAuth()
    {
        await using var host = await ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Backend:AuthMode"] = "open"
        });

        using var response = await host.Client.GetAsync("/api/v1/admin/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Post_AdminHealth_WithOversizedJson_ReturnsStandardPayloadTooLargeError()
    {
        await using var host = await ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Backend:MaxBodySize"] = "1kb"
        });

        var oversizedJson = "\"" + new string('x', 2048) + "\"";
        using var content = new StringContent(oversizedJson, Encoding.UTF8, "application/json");

        using var response = await host.Client.PostAsync("/api/v1/admin/health", content);

        Assert.Equal((HttpStatusCode)413, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<ApiErrorEnvelope>();
        payload = Assert.IsType<ApiErrorEnvelope>(payload);

        Assert.Equal("VALIDATION_FAILED", payload.Error.Code);
        Assert.Contains("max size", payload.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(payload.Error.Details);
        Assert.Equal("1kb", Assert.IsType<System.Text.Json.JsonElement>(payload.Error.Details!["maxBodySize"]).GetString());
    }

    [Fact]
    public async Task Post_Register_CreatesBootstrapAdmin_WithBcryptPasswordHash()
    {
        await using var host = await ApiIntegrationTestHost.StartAsync();

        using var response = await host.Client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            username = "bootstrap-admin",
            password = "supersecure",
            role = "viewer"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<RegisterUserResponse>();
        payload = Assert.IsType<RegisterUserResponse>(payload);

        Assert.Equal("bootstrap-admin", payload.Username);
        Assert.Equal("admin", payload.Role);
        Assert.True(payload.BootstrapAdmin);

        var storedHash = await host.QuerySystemScalarAsync<string>(
            "SELECT password_hash FROM users WHERE username = $username;",
            new { username = "bootstrap-admin" });
        var storedRole = await host.QuerySystemScalarAsync<string>(
            "SELECT role FROM users WHERE username = $username;",
            new { username = "bootstrap-admin" });

        Assert.NotEqual("supersecure", storedHash);
        Assert.StartsWith("$2", storedHash, StringComparison.Ordinal);
        Assert.True(BCrypt.Net.BCrypt.Verify("supersecure", storedHash));
        Assert.Equal("admin", storedRole);
    }

    [Fact]
    public async Task Post_Register_RejectsWeakPayloads_WithStandardValidationEnvelope()
    {
        await using var host = await ApiIntegrationTestHost.StartAsync();

        using var response = await host.Client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            username = " ",
            password = "short",
            role = "owner"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<ApiErrorEnvelope>();
        payload = Assert.IsType<ApiErrorEnvelope>(payload);

        Assert.Equal("VALIDATION_FAILED", payload.Error.Code);
        Assert.Contains("Username is required.", payload.Error.Message, StringComparison.Ordinal);
        Assert.NotNull(payload.Error.Details);
        Assert.Equal("username", Assert.IsType<JsonElement>(payload.Error.Details!["field"]).GetString());
    }

    [Fact]
    public async Task Post_Register_RejectsFollowUpSelfRegistration_AfterBootstrapUserExists()
    {
        await using var host = await ApiIntegrationTestHost.StartAsync();

        using var firstResponse = await host.Client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            username = "bootstrap-admin",
            password = "supersecure"
        });
        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);

        using var secondResponse = await host.Client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            username = "second-user",
            password = "anotherpass"
        });

        Assert.Equal(HttpStatusCode.Forbidden, secondResponse.StatusCode);

        var payload = await secondResponse.Content.ReadFromJsonAsync<ApiErrorEnvelope>();
        payload = Assert.IsType<ApiErrorEnvelope>(payload);

        Assert.Equal("FORBIDDEN", payload.Error.Code);
        Assert.Contains("Self-registration is closed", payload.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Post_Register_AllowsAdminManagedCreation_WhenBearerIdentityIsPresentOnAuthRoute()
    {
        await using var host = await ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Backend:Jwt:Secret"] = "super-secret"
        });

        using var bootstrapResponse = await host.Client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            username = "bootstrap-admin",
            password = "supersecure"
        });
        Assert.Equal(HttpStatusCode.Created, bootstrapResponse.StatusCode);

        var adminToken = await LoginAndGetAccessTokenAsync(host, "bootstrap-admin", "supersecure");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/register")
        {
            Content = JsonContent.Create(new
            {
                username = "editor-user",
                password = "anotherpass",
                role = "editor"
            })
        };
        request.Headers.Authorization = new("Bearer", adminToken);

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<RegisterUserResponse>();
        payload = Assert.IsType<RegisterUserResponse>(payload);
        Assert.Equal("editor", payload.Role);
        Assert.False(payload.BootstrapAdmin);
    }

    [Fact]
    public async Task Post_Login_ReturnsAccessToken_AndRefreshCookie()
    {
        await using var host = await ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Backend:Jwt:Secret"] = "super-secret"
        });

        using var registerResponse = await host.Client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            username = "bootstrap-admin",
            password = "supersecure"
        });
        Assert.Equal(HttpStatusCode.Created, registerResponse.StatusCode);

        using var response = await host.Client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            username = "bootstrap-admin",
            password = "supersecure"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("Set-Cookie", out var cookieValues));

        var payload = await response.Content.ReadFromJsonAsync<AuthTokenResponse>();
        payload = Assert.IsType<AuthTokenResponse>(payload);

        Assert.False(string.IsNullOrWhiteSpace(payload.AccessToken));
        Assert.Equal("Bearer", payload.TokenType);
        Assert.Equal("bootstrap-admin", payload.User.Username);
        Assert.Equal("admin", payload.User.Role);

        var refreshCookie = Assert.Single(cookieValues);
        Assert.Contains("repere_refresh=", refreshCookie, StringComparison.Ordinal);
        Assert.Contains("httponly", refreshCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", refreshCookie, StringComparison.OrdinalIgnoreCase);

        var claims = ParseJwtPayload(payload.AccessToken);
        Assert.Equal(payload.User.Id.ToString(), claims.GetProperty("sub").GetString());
        Assert.Equal("admin", claims.GetProperty("role").GetString());
        Assert.True(claims.TryGetProperty("iat", out _));
        Assert.True(claims.TryGetProperty("exp", out _));
    }

    [Fact]
    public async Task Post_Refresh_RotatesCookie_AndRejectsReusedRefreshToken()
    {
        await using var host = await ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Backend:Jwt:Secret"] = "super-secret"
        });

        await host.Client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            username = "bootstrap-admin",
            password = "supersecure"
        });

        using var loginResponse = await host.Client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            username = "bootstrap-admin",
            password = "supersecure"
        });
        var initialCookie = ExtractCookieHeader(loginResponse, "repere_refresh");

        using var refreshRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh");
        refreshRequest.Headers.TryAddWithoutValidation("Cookie", initialCookie);

        using var refreshResponse = await host.Client.SendAsync(refreshRequest);
        Assert.Equal(HttpStatusCode.OK, refreshResponse.StatusCode);

        var refreshedCookie = ExtractCookieHeader(refreshResponse, "repere_refresh");
        Assert.NotEqual(initialCookie, refreshedCookie);

        using var reuseRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh");
        reuseRequest.Headers.TryAddWithoutValidation("Cookie", initialCookie);

        using var reuseResponse = await host.Client.SendAsync(reuseRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, reuseResponse.StatusCode);

        var payload = await reuseResponse.Content.ReadFromJsonAsync<ApiErrorEnvelope>();
        payload = Assert.IsType<ApiErrorEnvelope>(payload);
        Assert.Equal("UNAUTHORIZED", payload.Error.Code);
        Assert.Contains("Refresh session", payload.Error.Message, StringComparison.Ordinal);
    }

    private static JsonElement ParseJwtPayload(string token)
    {
        var segments = token.Split('.');
        Assert.Equal(3, segments.Length);

        var padded = segments[1]
            .Replace('-', '+')
            .Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - padded.Length % 4) % 4), '=');

        using var document = JsonDocument.Parse(Convert.FromBase64String(padded));
        return document.RootElement.Clone();
    }

    private static string ExtractCookieHeader(HttpResponseMessage response, string cookieName)
    {
        Assert.True(response.Headers.TryGetValues("Set-Cookie", out var values));
        var header = Assert.Single(values);
        var cookie = header.Split(';', 2, StringSplitOptions.TrimEntries)[0];
        Assert.StartsWith(cookieName + "=", cookie, StringComparison.Ordinal);
        return cookie;
    }

    private static async Task<string> LoginAndGetAccessTokenAsync(
        ApiIntegrationTestHost host,
        string username,
        string password)
    {
        using var response = await host.Client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            username,
            password
        });
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<AuthTokenResponse>();
        payload = Assert.IsType<AuthTokenResponse>(payload);
        return payload.AccessToken;
    }
}
