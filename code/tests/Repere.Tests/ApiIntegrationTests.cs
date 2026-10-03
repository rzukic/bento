using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Repere.Api.Configuration;
using Repere.Api.Db;
using Repere.Api.Models;
using Repere.Api.Services;

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

        var root = await ReadJsonAsync(response);

        AssertPublicHealthPayload(root, "healthy");
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
        Assert.False(payload.RefreshCookie.RequireSecure);
        Assert.Equal("Strict", payload.RefreshCookie.SameSite);
        Assert.Equal("/api/v1/auth", payload.RefreshCookie.Path);
        Assert.Contains("CSRF protections until the browser-facing frontend exists", payload.Deferred, StringComparer.Ordinal);
        Assert.Contains("CSP headers until the browser-facing frontend exists", payload.Deferred, StringComparer.Ordinal);
        Assert.Contains("vault mode", payload.Deferred, StringComparer.Ordinal);
    }

    [Fact]
    public async Task Get_AuthContract_ReportsBootstrapRegistrationClosure_AfterFirstUserExists()
    {
        await using var host = await ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Backend:Jwt:Secret"] = "super-secret"
        });

        using var beforeBootstrapResponse = await host.Client.GetAsync("/api/v1/auth");
        Assert.Equal(HttpStatusCode.OK, beforeBootstrapResponse.StatusCode);

        var beforeBootstrapPayload = await beforeBootstrapResponse.Content.ReadFromJsonAsync<AuthRuntimeContractResponse>();
        beforeBootstrapPayload = Assert.IsType<AuthRuntimeContractResponse>(beforeBootstrapPayload);
        Assert.True(beforeBootstrapPayload.RegistrationBootstrapOpen);

        using var registerResponse = await host.Client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            username = "bootstrap-admin",
            password = "supersecure"
        });
        Assert.Equal(HttpStatusCode.Created, registerResponse.StatusCode);

        using var afterBootstrapResponse = await host.Client.GetAsync("/api/v1/auth");
        Assert.Equal(HttpStatusCode.OK, afterBootstrapResponse.StatusCode);

        var afterBootstrapPayload = await afterBootstrapResponse.Content.ReadFromJsonAsync<AuthRuntimeContractResponse>();
        afterBootstrapPayload = Assert.IsType<AuthRuntimeContractResponse>(afterBootstrapPayload);
        Assert.False(afterBootstrapPayload.RegistrationBootstrapOpen);
    }

    [Fact]
    public async Task Get_AuthContract_InProductionMode_DoesNotForceSecureRefreshCookiesByDefault()
    {
        // Since Step 2.7 the unset default follows the request scheme in every
        // environment; require_secure only reports an explicit AUTH_REFRESH_COOKIE_SECURE=true.
        await using var host = await ApiIntegrationTestHost.StartAsync(
            new Dictionary<string, string?>
            {
                ["Backend:Jwt:Secret"] = "super-secret"
            },
            environmentName: "Production");

        using var response = await host.Client.GetAsync("/api/v1/auth");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<AuthRuntimeContractResponse>();
        payload = Assert.IsType<AuthRuntimeContractResponse>(payload);
        Assert.False(payload.RefreshCookie.RequireSecure);
    }

    [Fact]
    public async Task Get_AuthContract_ReportsForcedSecureRefreshCookies()
    {
        await using var host = await ApiIntegrationTestHost.StartAsync(
            new Dictionary<string, string?>
            {
                ["Backend:Jwt:Secret"] = "super-secret",
                ["Backend:AuthCookie:RequireSecure"] = "true"
            });

        using var response = await host.Client.GetAsync("/api/v1/auth");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<AuthRuntimeContractResponse>();
        payload = Assert.IsType<AuthRuntimeContractResponse>(payload);
        Assert.True(payload.RefreshCookie.RequireSecure);
    }

    [Fact]
    public async Task Get_AdminHealth_InStandardMode_AllowsAnonymousReadinessProbe()
    {
        await using var host = await ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Backend:Jwt:Secret"] = "super-secret"
        });

        using var response = await host.Client.GetAsync("/api/v1/admin/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var root = await ReadJsonAsync(response);

        AssertPublicHealthPayload(root, "healthy");
    }

    [Fact]
    public async Task Get_AdminHealth_InStandardMode_RemainsReadable_ForNonAdminRole()
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

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var root = await ReadJsonAsync(response);

        AssertPublicHealthPayload(root, "healthy");
    }

    [Fact]
    public async Task Get_AdminHealthDetails_InStandardMode_RequiresAdminRole()
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
                username = "viewer-user",
                password = "anotherpass",
                role = "viewer"
            })
        };
        createUserRequest.Headers.Authorization = new("Bearer", await LoginAndGetAccessTokenAsync(host, "bootstrap-admin", "supersecure"));

        using var createUserResponse = await host.Client.SendAsync(createUserRequest);
        Assert.Equal(HttpStatusCode.Created, createUserResponse.StatusCode);

        var viewerToken = await LoginAndGetAccessTokenAsync(host, "viewer-user", "anotherpass");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/health/details");
        request.Headers.Authorization = new("Bearer", viewerToken);

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Get_AdminHealthDetails_InStandardMode_AllowsAdminBearerToken()
    {
        const string jwtSecret = "super-secret";

        await using var host = await ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Backend:Jwt:Secret"] = jwtSecret
        });

        using var registerResponse = await host.Client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            username = "bootstrap-admin",
            password = "supersecure"
        });
        Assert.Equal(HttpStatusCode.Created, registerResponse.StatusCode);

        var adminToken = await LoginAndGetAccessTokenAsync(host, "bootstrap-admin", "supersecure");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/health/details");
        request.Headers.Authorization = new("Bearer", adminToken);

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var root = await ReadJsonAsync(response);

        AssertDetailedHealthPayload(root, "healthy", "standard", "sqlite");
    }

    [Fact]
    public async Task Get_AdminHealth_InStandardMode_IgnoresMalformedBearerToken_ForReadinessProbe()
    {
        await using var host = await ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Backend:Jwt:Secret"] = "super-secret"
        });

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/health");
        request.Headers.Authorization = new("Bearer", "not-a-jwt");

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var root = await ReadJsonAsync(response);

        AssertPublicHealthPayload(root, "healthy");
    }

    [Fact]
    public async Task Get_AdminHealth_InStandardMode_IgnoresExpiredBearerToken_ForReadinessProbe()
    {
        const string jwtSecret = "super-secret";

        await using var host = await ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Backend:Jwt:Secret"] = jwtSecret
        });

        using var registerResponse = await host.Client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            username = "bootstrap-admin",
            password = "supersecure"
        });
        Assert.Equal(HttpStatusCode.Created, registerResponse.StatusCode);

        var validToken = await LoginAndGetAccessTokenAsync(host, "bootstrap-admin", "supersecure");
        var expiredToken = RewriteJwtExpiration(validToken, jwtSecret, DateTimeOffset.UtcNow.AddMinutes(-5));

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/health");
        request.Headers.Authorization = new("Bearer", expiredToken);

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var root = await ReadJsonAsync(response);

        AssertPublicHealthPayload(root, "healthy");
    }

    [Fact]
    public async Task Get_AdminHealthDetails_InOpenMode_UsesOpenModeAdmin()
    {
        await using var host = await ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Backend:AuthMode"] = "open"
        });

        using var response = await host.Client.GetAsync("/api/v1/admin/health/details");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var root = await ReadJsonAsync(response);

        AssertDetailedHealthPayload(root, "healthy", "open", "sqlite");
    }

    [Fact]
    public async Task Get_AdminHealth_WhenUnhealthy_RemainsGenericForAnonymousCallers()
    {
        await using var host = await ApiIntegrationTestHost.StartAsync(
            configureTestServices: services =>
            {
                services.RemoveAll<AdminHealthService>();
                services.AddSingleton<AdminHealthService>(serviceProvider => new AdminHealthService(
                    new FailingDbPool(),
                    new FailingDbPool(),
                    serviceProvider.GetRequiredService<BackendRuntimeInfo>(),
                    serviceProvider.GetRequiredService<BackendRuntimeOptions>(),
                    serviceProvider.GetRequiredService<DatabaseConfig>()));
            });

        using var response = await host.Client.GetAsync("/api/v1/admin/health");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        var root = await ReadJsonAsync(response);

        AssertPublicHealthPayload(root, "unhealthy", expectedSystemPool: "unavailable", expectedDataPool: "unavailable", expectedError: "unhealthy");
        Assert.DoesNotContain("boom", root.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Get_AdminHealthDetails_WhenUnhealthy_PreservesDetailedErrorForAdmin()
    {
        await using var host = await ApiIntegrationTestHost.StartAsync(
            new Dictionary<string, string?>
            {
                ["Backend:Jwt:Secret"] = "super-secret"
            },
            configureTestServices: services =>
            {
                services.RemoveAll<AdminHealthService>();
                services.AddSingleton<AdminHealthService>(serviceProvider => new AdminHealthService(
                    new FailingDbPool(),
                    new FailingDbPool(),
                    serviceProvider.GetRequiredService<BackendRuntimeInfo>(),
                    serviceProvider.GetRequiredService<BackendRuntimeOptions>(),
                    serviceProvider.GetRequiredService<DatabaseConfig>()));
            });

        using var registerResponse = await host.Client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            username = "bootstrap-admin",
            password = "supersecure"
        });
        Assert.Equal(HttpStatusCode.Created, registerResponse.StatusCode);

        var adminToken = await LoginAndGetAccessTokenAsync(host, "bootstrap-admin", "supersecure");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/health/details");
        request.Headers.Authorization = new("Bearer", adminToken);

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        var root = await ReadJsonAsync(response);

        AssertDetailedHealthPayload(
            root,
            "unhealthy",
            "standard",
            "sqlite",
            expectedSystemPool: "unavailable",
            expectedDataPool: "unavailable",
            expectedErrorSubstring: "boom");
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
        Assert.DoesNotContain("secure", refreshCookie, StringComparison.OrdinalIgnoreCase);

        var claims = ParseJwtPayload(payload.AccessToken);
        Assert.Equal(payload.User.Id.ToString(), claims.GetProperty("sub").GetString());
        Assert.Equal("admin", claims.GetProperty("role").GetString());
        Assert.True(claims.TryGetProperty("iat", out _));
        Assert.True(claims.TryGetProperty("exp", out _));
    }

    [Fact]
    public async Task Post_Login_UsesConfiguredRefreshCookieSecurityFlags()
    {
        await using var host = await ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Backend:Jwt:Secret"] = "super-secret",
            ["Backend:AuthCookie:RequireSecure"] = "false",
            ["Backend:AuthCookie:SameSite"] = "Lax"
        });

        await host.Client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            username = "bootstrap-admin",
            password = "supersecure"
        });

        using var response = await host.Client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            username = "bootstrap-admin",
            password = "supersecure"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("Set-Cookie", out var cookieValues));

        var refreshCookie = Assert.Single(cookieValues);
        Assert.Contains("repere_refresh=", refreshCookie, StringComparison.Ordinal);
        Assert.Contains("httponly", refreshCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", refreshCookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secure", refreshCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Post_Login_InProductionMode_OnPlainHttp_IssuesNonSecureRefreshCookie()
    {
        // The packaged stack runs in Production but is reached over plain HTTP by
        // default (DOMAIN=localhost); a Secure cookie would never come back from a
        // browser on http://<lan-ip>. HTTPS (direct or forwarded) still marks it Secure.
        await using var host = await ApiIntegrationTestHost.StartAsync(
            new Dictionary<string, string?>
            {
                ["Backend:Jwt:Secret"] = "super-secret"
            },
            environmentName: "Production");

        await host.Client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            username = "bootstrap-admin",
            password = "supersecure"
        });

        using var response = await host.Client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            username = "bootstrap-admin",
            password = "supersecure"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("Set-Cookie", out var cookieValues));

        var refreshCookie = Assert.Single(cookieValues);
        Assert.DoesNotContain("secure", refreshCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", refreshCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Post_Login_RejectsInvalidPayload_BeforeAuthLogicRuns()
    {
        await using var host = await ApiIntegrationTestHost.StartAsync();

        using var response = await host.Client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            password = "supersecure"
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
    public async Task Post_Login_RejectsInvalidCredentials()
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
            password = "wrong-password"
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<ApiErrorEnvelope>();
        payload = Assert.IsType<ApiErrorEnvelope>(payload);
        Assert.Equal("UNAUTHORIZED", payload.Error.Code);
        Assert.Contains("Invalid username or password", payload.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Post_Refresh_IsNotThrottledByTheCredentialLimit()
    {
        // Every SPA page load refreshes the session. With the credential limit at 2,
        // a handful of reloads used to return 429 and drop a valid session.
        await using var host = await ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Backend:Jwt:Secret"] = "super-secret",
            ["Backend:AuthRateLimitPerMinute"] = "2"
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
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);

        for (var i = 0; i < 10; i++)
        {
            using var refreshResponse = await host.Client.PostAsync("/api/v1/auth/refresh", content: null);
            Assert.Equal(HttpStatusCode.OK, refreshResponse.StatusCode);
        }

        var contract = await host.Client.GetFromJsonAsync<AuthRuntimeContractResponse>("/api/v1/auth");
        contract = Assert.IsType<AuthRuntimeContractResponse>(contract);
        Assert.Equal(2, contract.AuthRateLimitPerMinute);
        Assert.Equal(120, contract.SessionRateLimitPerMinute);
    }

    [Fact]
    public async Task Post_Login_ReturnsTooManyRequests_WhenAuthRateLimitIsExceeded()
    {
        await using var host = await ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Backend:Jwt:Secret"] = "super-secret",
            ["Backend:AuthRateLimitPerMinute"] = "2"
        });

        await host.Client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            username = "bootstrap-admin",
            password = "supersecure"
        });

        using var firstResponse = await host.Client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            username = "bootstrap-admin",
            password = "supersecure"
        });
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);

        using var secondResponse = await host.Client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            username = "bootstrap-admin",
            password = "supersecure"
        });
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);

        using var throttledResponse = await host.Client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            username = "bootstrap-admin",
            password = "supersecure"
        });
        Assert.Equal((HttpStatusCode)429, throttledResponse.StatusCode);

        var payload = await throttledResponse.Content.ReadFromJsonAsync<ApiErrorEnvelope>();
        payload = Assert.IsType<ApiErrorEnvelope>(payload);
        Assert.Equal("TOO_MANY_REQUESTS", payload.Error.Code);
        Assert.Contains("Too many auth attempts", payload.Error.Message, StringComparison.Ordinal);
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
        using var manualClient = new HttpClient(new HttpClientHandler
        {
            UseCookies = false
        })
        {
            BaseAddress = host.Client.BaseAddress
        };

        using var refreshRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh");
        refreshRequest.Headers.TryAddWithoutValidation("Cookie", initialCookie);

        using var refreshResponse = await manualClient.SendAsync(refreshRequest);
        Assert.Equal(HttpStatusCode.OK, refreshResponse.StatusCode);

        var refreshedCookie = ExtractCookieHeader(refreshResponse, "repere_refresh");
        Assert.NotEqual(initialCookie, refreshedCookie);

        using var reuseRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh");
        reuseRequest.Headers.TryAddWithoutValidation("Cookie", initialCookie);

        using var reuseResponse = await manualClient.SendAsync(reuseRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, reuseResponse.StatusCode);

        var payload = await reuseResponse.Content.ReadFromJsonAsync<ApiErrorEnvelope>();
        payload = Assert.IsType<ApiErrorEnvelope>(payload);
        Assert.Equal("UNAUTHORIZED", payload.Error.Code);
        Assert.Contains("Refresh session", payload.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Post_Refresh_InDevelopmentMode_WorksThroughHttpClientCookieJar_ByDefault()
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
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);

        using var refreshResponse = await host.Client.PostAsync("/api/v1/auth/refresh", content: null);
        Assert.Equal(HttpStatusCode.OK, refreshResponse.StatusCode);
    }

    [Fact]
    public async Task Post_Refresh_InProductionMode_OnPlainHttp_ResumesSession()
    {
        // Before Step 2.7 the production default forced a Secure cookie, which the
        // client never replayed over HTTP and the refresh failed with 401. Session
        // resume over plain HTTP is exactly what the packaged stack needs.

        await using var host = await ApiIntegrationTestHost.StartAsync(
            new Dictionary<string, string?>
            {
                ["Backend:Jwt:Secret"] = "super-secret"
            },
            environmentName: "Production");

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
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);

        using var refreshResponse = await host.Client.PostAsync("/api/v1/auth/refresh", content: null);
        Assert.Equal(HttpStatusCode.OK, refreshResponse.StatusCode);

        var payload = await ReadJsonAsync(refreshResponse);
        Assert.Equal("bootstrap-admin", payload.GetProperty("user").GetProperty("username").GetString());
    }

    [Fact]
    public async Task Post_Logout_RevokesRefreshSession_AndClearsCookie()
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
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);

        using var logoutResponse = await host.Client.PostAsync("/api/v1/auth/logout", content: null);
        Assert.Equal(HttpStatusCode.NoContent, logoutResponse.StatusCode);
        Assert.True(logoutResponse.Headers.TryGetValues("Set-Cookie", out var cookieValues));
        var cleared = Assert.Single(cookieValues);
        Assert.Contains("repere_refresh=;", cleared, StringComparison.Ordinal);
        Assert.Contains("path=/api/v1/auth", cleared, StringComparison.OrdinalIgnoreCase);

        // The cookie jar dropped the cookie and the server-side session is revoked:
        // a refresh can no longer resume the session.
        using var refreshResponse = await host.Client.PostAsync("/api/v1/auth/refresh", content: null);
        Assert.Equal(HttpStatusCode.Unauthorized, refreshResponse.StatusCode);
    }

    [Fact]
    public async Task Post_Logout_RevokesSession_EvenIfTheCookieIsReplayedLater()
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
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        var refreshCookie = Assert.Single(loginResponse.Headers.GetValues("Set-Cookie")).Split(';')[0];

        using var logoutResponse = await host.Client.PostAsync("/api/v1/auth/logout", content: null);
        Assert.Equal(HttpStatusCode.NoContent, logoutResponse.StatusCode);

        // A copy of the old cookie (another tab, a stolen value) must be dead too.
        using var replay = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh");
        replay.Headers.Add("Cookie", refreshCookie);
        using var replayResponse = await host.Client.SendAsync(replay);
        Assert.Equal(HttpStatusCode.Unauthorized, replayResponse.StatusCode);
    }

    [Fact]
    public async Task Post_Logout_WithoutCookie_IsIdempotent()
    {
        await using var host = await ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Backend:Jwt:Secret"] = "super-secret"
        });

        using var logoutResponse = await host.Client.PostAsync("/api/v1/auth/logout", content: null);
        Assert.Equal(HttpStatusCode.NoContent, logoutResponse.StatusCode);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static void AssertPublicHealthPayload(
        JsonElement root,
        string expectedStatus,
        string expectedSystemPool = "connected",
        string expectedDataPool = "connected",
        string? expectedError = null)
    {
        Assert.Equal(expectedStatus, root.GetProperty("status").GetString());
        Assert.Equal(expectedSystemPool, root.GetProperty("database").GetProperty("system_pool").GetString());
        Assert.Equal(expectedDataPool, root.GetProperty("database").GetProperty("data_pool").GetString());
        Assert.True(root.TryGetProperty("version", out var version));
        Assert.False(string.IsNullOrWhiteSpace(version.GetString()));
        Assert.False(root.TryGetProperty("runtime", out _));
        Assert.False(root.TryGetProperty("uptime", out _));
        Assert.DoesNotContain("config_path", root.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("data_dir", root.GetRawText(), StringComparison.Ordinal);

        if (expectedError is null)
        {
            Assert.False(root.TryGetProperty("error", out _));
        }
        else
        {
            Assert.Equal(expectedError, root.GetProperty("error").GetString());
        }
    }

    private static void AssertDetailedHealthPayload(
        JsonElement root,
        string expectedStatus,
        string expectedAuthMode,
        string expectedProvider,
        string expectedSystemPool = "connected",
        string expectedDataPool = "connected",
        string? expectedErrorSubstring = null)
    {
        Assert.Equal(expectedStatus, root.GetProperty("status").GetString());
        Assert.Equal(expectedSystemPool, root.GetProperty("database").GetProperty("system_pool").GetString());
        Assert.Equal(expectedDataPool, root.GetProperty("database").GetProperty("data_pool").GetString());
        Assert.True(root.TryGetProperty("runtime", out var runtime));
        Assert.Equal(expectedProvider, runtime.GetProperty("database_provider").GetString());
        Assert.Equal(expectedAuthMode, runtime.GetProperty("auth_mode").GetString());
        Assert.True(runtime.GetProperty("jwt_configured").GetBoolean());
        Assert.True(runtime.TryGetProperty("config_path", out _));
        Assert.True(runtime.TryGetProperty("data_dir", out _));
        Assert.True(root.TryGetProperty("uptime", out var uptime));
        Assert.False(string.IsNullOrWhiteSpace(uptime.GetString()));
        Assert.True(root.TryGetProperty("version", out var version));
        Assert.False(string.IsNullOrWhiteSpace(version.GetString()));

        if (expectedErrorSubstring is null)
        {
            Assert.False(root.TryGetProperty("error", out _));
        }
        else
        {
            Assert.Contains(expectedErrorSubstring, root.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
        }
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

    private static string RewriteJwtExpiration(string token, string secret, DateTimeOffset expiresAt)
    {
        var segments = token.Split('.');
        Assert.Equal(3, segments.Length);

        using var document = JsonDocument.Parse(Base64UrlDecode(segments[1]));
        var payload = document.RootElement.EnumerateObject()
            .ToDictionary(
                property => property.Name,
                property => property.Value.Clone(),
                StringComparer.Ordinal);

        payload["exp"] = JsonSerializer.SerializeToElement(expiresAt.ToUnixTimeSeconds());

        var payloadJson = JsonSerializer.Serialize(payload);
        var encodedPayload = Base64UrlEncode(Encoding.UTF8.GetBytes(payloadJson));
        var signingInput = $"{segments[0]}.{encodedPayload}";

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var signature = Base64UrlEncode(hmac.ComputeHash(Encoding.UTF8.GetBytes(signingInput)));
        return $"{signingInput}.{signature}";
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

    private static byte[] Base64UrlDecode(string value)
    {
        var normalized = value
            .Replace('-', '+')
            .Replace('_', '/');
        normalized = normalized.PadRight(normalized.Length + ((4 - normalized.Length % 4) % 4), '=');
        return Convert.FromBase64String(normalized);
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private sealed class FailingDbPool : ISystemPool, IDataPool
    {
        public DatabaseProvider Provider => DatabaseProvider.Sqlite;

        public Task<System.Data.IDbConnection> GetConnectionAsync()
        {
            throw new InvalidOperationException("boom: detailed database connection failure");
        }
    }
}
