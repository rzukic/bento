using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bento.Api.Configuration;
using Bento.Api.Exceptions;
using Bento.Api.Services;
using Bento.Api.Services.Workspaces;

namespace Bento.Api.Middleware;

public sealed class AuthMiddleware
{
    private const string BearerPrefix = "Bearer ";

    private readonly RequestDelegate _next;
    private readonly BackendRuntimeOptions _runtimeOptions;

    public AuthMiddleware(RequestDelegate next, BackendRuntimeOptions runtimeOptions)
    {
        _next = next;
        _runtimeOptions = runtimeOptions;
    }

    public async Task InvokeAsync(HttpContext context, ApiKeyService apiKeys, OAuthService oauth, WorkspaceAccess access)
    {
        if (IsMcpRoute(context.Request.Path))
        {
            await AuthenticateMcpAsync(context, apiKeys, oauth, access);
            return;
        }

        if (!IsVersionedApiRoute(context.Request.Path))
        {
            await _next(context);
            return;
        }

        if (IsPublicHealthRoute(context.Request.Path) || IsPublicSetupRoute(context.Request.Path) || IsPublicOAuthRoute(context.Request.Path) ||
            context.Request.Path.Equals("/" + ApiRoutes.McpBundle, StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        if (_runtimeOptions.IsOpenAuthMode)
        {
            context.User = CreateOpenModePrincipal();
            await _next(context);
            return;
        }

        if (await TryAuthenticateCredentialAsync(context, apiKeys, oauth, access, enforceScope: true))
        {
            await _next(context);
            return;
        }

        var isAuthRoute = IsAuthRoute(context.Request.Path);
        if (isAuthRoute && string.IsNullOrWhiteSpace(context.Request.Headers.Authorization))
        {
            await _next(context);
            return;
        }

        context.User = await InWorkspaceAsync(context, access, CreateAuthenticatedPrincipal(context.Request.Headers.Authorization));

        await _next(context);
    }

    // ── Workspaces (Step 5.5 part B) ─────────────────────────────────────

    /// <summary>
    /// The caller's role for this request: in the workspace it is for, or — for
    /// the installation's own parts (users, keys, connections, vault …) — their
    /// installation role. Not a member: the workspace is not found, as if it did
    /// not exist.
    /// </summary>
    private static async Task<string> RoleForRequestAsync(HttpContext context, WorkspaceAccess access, long userId, string installationRole)
    {
        if (WorkspaceAccess.IsInstallationPath(context.Request.Path.Value ?? ""))
            return installationRole;
        var workspace = WorkspaceContext.Current;
        return await access.RoleInAsync(userId, installationRole, workspace.Id)
            ?? throw new ApiNotFoundException($"There is no workspace '{workspace.Slug}'.", new Dictionary<string, object?> { ["workspace"] = workspace.Slug });
    }

    /// <summary>A key or a connection may use only the workspaces it was made for.</summary>
    private static void EnsureWorkspaceAllowed(HttpContext context, IReadOnlyList<long>? workspaces, string what)
    {
        if (WorkspaceAccess.IsInstallationPath(context.Request.Path.Value ?? ""))
            return;
        var workspace = WorkspaceContext.Current;
        if (!WorkspaceAccess.Allows(workspaces, workspace.Id))
            throw new ApiForbiddenException($"This {what} may not use the workspace '{workspace.Slug}'.",
                new Dictionary<string, object?> { ["workspace"] = workspace.Slug });
    }

    /// <summary>A signed-in user's principal with their role in the workspace; their own role stays as installation_role.</summary>
    private static async Task<ClaimsPrincipal> InWorkspaceAsync(HttpContext context, WorkspaceAccess access, ClaimsPrincipal principal)
    {
        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        var installationRole = principal.FindFirstValue(ClaimTypes.Role) ?? "viewer";
        if (!long.TryParse(userId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            return principal;
        var role = await RoleForRequestAsync(context, access, id, installationRole);
        return WithRole(principal, role, installationRole, null);
    }

    private static ClaimsPrincipal WithRole(ClaimsPrincipal principal, string role, string installationRole, IReadOnlyList<long>? workspaces)
    {
        var claims = principal.Claims.Where(c => c.Type is not (ClaimTypes.Role or "role")).ToList();
        claims.Add(new Claim(ClaimTypes.Role, role));
        claims.Add(new Claim("role", role));
        claims.Add(new Claim("installation_role", installationRole));
        if (workspaces is not null)
            claims.Add(new Claim("workspaces", string.Join(' ', workspaces.Select(w => w.ToString(CultureInfo.InvariantCulture)))));
        var original = principal.Identity as ClaimsIdentity;
        return new ClaimsPrincipal(new ClaimsIdentity(claims, original?.AuthenticationType));
    }

    /// <summary>
    /// OAuth access tokens of MCP clients (<c>Bearer bento_at_…</c>) and API keys
    /// (<c>X-API-Key</c> or <c>Bearer bento_…</c>). Both act as their user within
    /// their scope; false when the request carries neither.
    /// </summary>
    private async Task<bool> TryAuthenticateCredentialAsync(HttpContext context, ApiKeyService apiKeys, OAuthService oauth, WorkspaceAccess access, bool enforceScope)
    {
        var bearer = ReadBearer(context.Request);
        if (OAuthService.LooksLikeAccessToken(bearer))
        {
            if (!_runtimeOptions.IsStandardAuthMode)
                throw Unauthorized("This auth mode is not available yet.");
            var stored = await oauth.AuthenticateAsync(bearer!);
            EnsureWorkspaceAllowed(context, stored.Workspaces, "connection");
            // The user's role in this workspace caps what the app may do here.
            var grant = stored.As(await RoleForRequestAsync(context, access, stored.UserId, stored.Role));
            if (enforceScope) EnsurePermissionAllows(grant, context.Request);
            context.User = WithRole(CreateOAuthPrincipal(grant), grant.Role, stored.Role, stored.Workspaces);
            return true;
        }

        // API keys (Part 3 R3.3): X-API-Key or "Authorization: Bearer bento_…".
        var apiKey = ReadApiKey(context.Request);
        if (apiKey is null)
            return false;
        if (!_runtimeOptions.IsStandardAuthMode)
            throw Unauthorized("This auth mode is not available yet.");
        var key = await apiKeys.AuthenticateAsync(apiKey);
        EnsureWorkspaceAllowed(context, key.Workspaces, "API key");
        var identity = key.As(await RoleForRequestAsync(context, access, key.UserId, key.UserRole ?? "viewer"));
        if (enforceScope)
        {
            if (identity.Permissions is { } permissions)
                EnsureKeyPermissionAllows(permissions, context.Request);
            else
                EnsureScopeAllows(identity.Scope, context.Request);
        }
        var principal = CreateApiKeyPrincipal(identity);
        context.User = WithRole(principal, principal.FindFirstValue(ClaimTypes.Role) ?? "viewer", key.UserRole ?? "viewer", key.Workspaces);
        return true;
    }

    /// <summary>
    /// <c>/mcp</c> takes an OAuth token, an API key or a session token. The scope is
    /// not checked here: a read-only caller may still POST (every MCP message is a
    /// POST); the REST calls behind each tool enforce it. Without valid credentials
    /// the answer points the client at the OAuth metadata (RFC 9728 §5.1).
    /// </summary>
    private async Task AuthenticateMcpAsync(HttpContext context, ApiKeyService apiKeys, OAuthService oauth, WorkspaceAccess access)
    {
        if (_runtimeOptions.IsOpenAuthMode)
        {
            context.User = CreateOpenModePrincipal();
            await _next(context);
            return;
        }

        try
        {
            if (!await TryAuthenticateCredentialAsync(context, apiKeys, oauth, access, enforceScope: false))
                context.User = await InWorkspaceAsync(context, access, CreateAuthenticatedPrincipal(context.Request.Headers.Authorization));
        }
        catch (Exception ex) when (ex is ApiNotFoundException or ApiForbiddenException)
        {
            // Not a member of this workspace, or a connection made for others.
            context.Response.StatusCode = ex is ApiNotFoundException ? StatusCodes.Status404NotFound : StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new
            {
                jsonrpc = "2.0",
                id = (object?)null,
                error = new { code = -32001, message = ex.Message }
            });
            return;
        }
        catch (ApiUnauthorizedException ex)
        {
            var metadata = Services.Mcp.PublicUrl.ResourceMetadata(context.Request, _runtimeOptions);
            var hasToken = !string.IsNullOrWhiteSpace(context.Request.Headers.Authorization) || !string.IsNullOrWhiteSpace(context.Request.Headers["X-API-Key"]);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = hasToken
                ? $"Bearer resource_metadata=\"{metadata}\", error=\"invalid_token\", error_description=\"{ex.Message}\""
                : $"Bearer resource_metadata=\"{metadata}\"";
            await context.Response.WriteAsJsonAsync(new
            {
                jsonrpc = "2.0",
                id = (object?)null,
                error = new { code = -32001, message = ex.Message }
            });
            return;
        }

        await _next(context);
    }

    private static string? ReadBearer(HttpRequest request)
    {
        var authorization = request.Headers.Authorization.ToString();
        return authorization.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase)
            ? authorization[BearerPrefix.Length..].Trim()
            : null;
    }

    /// <summary>
    /// An app's token may only make the calls its permissions cover; areas apps never
    /// reach (users, keys, credentials, connections) are refused outright.
    /// </summary>
    private static void EnsurePermissionAllows(OAuthService.OAuthIdentity grant, HttpRequest request)
    {
        var required = McpPermissions.RequiredFor(request.Method, request.Path.Value ?? "");
        if (required is null)
            throw new ApiForbiddenException("Connected apps cannot use this part of Bento.", new Dictionary<string, object?> { ["path"] = request.Path.Value });
        if (!grant.Permissions.Contains(required))
            throw new ApiForbiddenException(
                $"This connection was not allowed to do that (needs \"{required}\"). Connect again and tick it on Bento's sign-in page.",
                new Dictionary<string, object?> { ["permission"] = required });
    }

    /// <summary>A key with permissions reaches what a connected app with them could, and nothing else (users, keys, credentials).</summary>
    private static void EnsureKeyPermissionAllows(IReadOnlyList<string> permissions, HttpRequest request)
    {
        var required = McpPermissions.RequiredFor(request.Method, request.Path.Value ?? "");
        if (required is null)
            throw new ApiForbiddenException("API keys cannot use this part of Bento.", new Dictionary<string, object?> { ["path"] = request.Path.Value });
        if (!permissions.Contains(required))
            throw new ApiForbiddenException(
                $"This API key was not allowed to do that (needs \"{required}\"). Make a key with it under Connections → API keys.",
                new Dictionary<string, object?> { ["permission"] = required });
    }

    /// <summary>The user's own role (the API's role checks apply as for them); the permissions narrow it further.</summary>
    private static ClaimsPrincipal CreateOAuthPrincipal(OAuthService.OAuthIdentity grant)
    {
        return new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, grant.UserId.ToString(CultureInfo.InvariantCulture)),
            new Claim(ClaimTypes.Name, grant.Username),
            new Claim(ClaimTypes.Role, grant.Role),
            new Claim("auth_method", "oauth"),
            new Claim("oauth_permissions", string.Join(' ', grant.Permissions)),
            new Claim("oauth_grant_id", grant.GrantId.ToString(CultureInfo.InvariantCulture)),
            new Claim("oauth_client", grant.ClientName)
        ], "OAuth"));
    }

    private static bool IsMcpRoute(PathString path) =>
        path.Equals("/" + ApiRoutes.Mcp, StringComparison.OrdinalIgnoreCase) || path.Equals("/" + ApiRoutes.Mcp + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>Client registration and the token endpoint authenticate by their own rules (public clients, PKCE).</summary>
    private static bool IsPublicOAuthRoute(PathString path) =>
        path.Equals("/" + ApiRoutes.OAuthRegister, StringComparison.OrdinalIgnoreCase) ||
        path.Equals("/" + ApiRoutes.OAuthToken, StringComparison.OrdinalIgnoreCase);

    private static string? ReadApiKey(HttpRequest request)
    {
        var header = request.Headers["X-API-Key"].ToString();
        if (!string.IsNullOrWhiteSpace(header))
            return header.Trim();
        var authorization = request.Headers.Authorization.ToString();
        if (authorization.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var token = authorization[BearerPrefix.Length..].Trim();
            if (ApiKeyService.LooksLikeKey(token))
                return token;
        }
        return null;
    }

    /// <summary>
    /// Read keys read: GET requests, plus running queries and checking
    /// formulas (POST, but no change). Write keys may do what their role allows; admin routes stay
    /// closed to every key because keys never carry the admin role.
    /// </summary>
    private static void EnsureScopeAllows(string scope, HttpRequest request)
    {
        if (scope == ApiKeyService.ReadWriteScope)
            return;
        if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) || HttpMethods.IsOptions(request.Method))
            return;
        var path = request.Path.Value ?? "";
        var runsQuery = HttpMethods.IsPost(request.Method) &&
            (path.Equals("/" + ApiRoutes.Query, StringComparison.OrdinalIgnoreCase) ||
             path.Equals("/" + ApiRoutes.Query + "/variables", StringComparison.OrdinalIgnoreCase) ||
             (path.StartsWith("/" + ApiRoutes.SavedQueries + "/", StringComparison.OrdinalIgnoreCase) && path.EndsWith("/run", StringComparison.OrdinalIgnoreCase)) ||
             path.Equals("/" + ApiRoutes.Formulas + "/validate", StringComparison.OrdinalIgnoreCase) ||
             path.Equals("/" + ApiRoutes.Formulas + "/preview", StringComparison.OrdinalIgnoreCase));
        if (!runsQuery)
            throw new ApiForbiddenException("This API key can only read.", new Dictionary<string, object?> { ["scope"] = scope });
    }

    private static ClaimsPrincipal CreateApiKeyPrincipal(ApiKeyService.ApiKeyIdentity identity)
    {
        // Keys with permissions act with their user's role (an admin's key may change the
        // schema), narrowed by the permissions checked above; older keys keep api/viewer.
        var role = identity.Permissions is null
            ? identity.Scope == ApiKeyService.ReadWriteScope ? "api" : "viewer"
            : identity.UserRole ?? "viewer";
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, identity.UserId.ToString(CultureInfo.InvariantCulture)),
            new(ClaimTypes.Name, identity.Username),
            new(ClaimTypes.Role, role),
            new("auth_method", "api_key"),
            new("api_key_id", identity.KeyId.ToString(CultureInfo.InvariantCulture))
        };
        if (identity.Permissions is { } permissions)
            claims.Add(new Claim("api_key_permissions", string.Join(' ', permissions)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "ApiKey"));
    }

    private static bool IsVersionedApiRoute(PathString path)
    {
        return path.StartsWithSegments("/" + ApiRoutes.Prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAuthRoute(PathString path)
    {
        return path.StartsWithSegments("/" + ApiRoutes.Auth, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPublicHealthRoute(PathString path)
    {
        return path.Equals("/" + ApiRoutes.AdminHealth, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The setup wizard has to read the installation state before any account
    /// exists, so the status probe is public. Everything else under /setup
    /// stays behind the normal token check.
    /// </summary>
    private static bool IsPublicSetupRoute(PathString path)
    {
        return path.Equals("/" + ApiRoutes.SetupStatus, StringComparison.OrdinalIgnoreCase);
    }

    private ClaimsPrincipal CreateAuthenticatedPrincipal(string? authorizationHeader)
    {
        if (!_runtimeOptions.IsStandardAuthMode)
        {
            throw Unauthorized("This auth mode is not available yet.");
        }

        if (string.IsNullOrWhiteSpace(_runtimeOptions.Jwt.Secret))
        {
            throw Unauthorized("Bearer authentication is not configured.");
        }

        var token = ExtractBearerToken(authorizationHeader);
        return ValidateAccessToken(token);
    }

    private ClaimsPrincipal ValidateAccessToken(string token)
    {
        var segments = token.Split('.');
        if (segments.Length != 3)
        {
            throw Unauthorized("Bearer token is invalid.");
        }

        var signingInput = $"{segments[0]}.{segments[1]}";
        var expectedSignature = ComputeSignature(signingInput);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(expectedSignature),
                Encoding.ASCII.GetBytes(segments[2])))
        {
            throw Unauthorized("Bearer token is invalid.");
        }

        JsonElement payload;
        try
        {
            using var document = JsonDocument.Parse(Base64UrlDecode(segments[1]));
            payload = document.RootElement.Clone();
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            throw Unauthorized("Bearer token is invalid.");
        }

        var issuer = ReadRequiredStringClaim(payload, "iss");
        var audience = ReadRequiredStringClaim(payload, "aud");
        var subject = ReadRequiredStringClaim(payload, "sub");
        var username = ReadRequiredStringClaim(payload, "username");
        var role = ReadRequiredStringClaim(payload, "role");
        var sessionId = ReadRequiredStringClaim(payload, "sid");
        var expiresAt = ReadRequiredLongClaim(payload, "exp");

        var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (!string.Equals(issuer, _runtimeOptions.Jwt.Issuer, StringComparison.Ordinal)
            || !string.Equals(audience, _runtimeOptions.Jwt.Audience, StringComparison.Ordinal)
            || expiresAt <= nowUnix)
        {
            throw Unauthorized("Bearer token is invalid or expired.");
        }

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, subject),
                new Claim(ClaimTypes.Name, username),
                new Claim(ClaimTypes.Role, role),
                new Claim("sub", subject),
                new Claim("username", username),
                new Claim("role", role),
                new Claim("sid", sessionId)
            ],
            authenticationType: "Bearer");

        return new ClaimsPrincipal(identity);
    }

    private static ClaimsPrincipal CreateOpenModePrincipal()
    {
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "0"),
                new Claim(ClaimTypes.Name, "open-mode-admin"),
                new Claim(ClaimTypes.Role, "admin"),
                new Claim("sub", "0"),
                new Claim("username", "open-mode-admin"),
                new Claim("role", "admin")
            ],
            authenticationType: "OpenMode");

        return new ClaimsPrincipal(identity);
    }

    private static string ExtractBearerToken(string? authorizationHeader)
    {
        if (string.IsNullOrWhiteSpace(authorizationHeader)
            || !authorizationHeader.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw Unauthorized("Bearer token is required.");
        }

        var token = authorizationHeader[BearerPrefix.Length..].Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            throw Unauthorized("Bearer token is required.");
        }

        return token;
    }

    private string ComputeSignature(string signingInput)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_runtimeOptions.Jwt.Secret!));
        return Base64UrlEncode(hmac.ComputeHash(Encoding.UTF8.GetBytes(signingInput)));
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

    private static string ReadRequiredStringClaim(JsonElement payload, string claimName)
    {
        if (!payload.TryGetProperty(claimName, out var value) || value.ValueKind != JsonValueKind.String)
        {
            throw Unauthorized("Bearer token is invalid.");
        }

        return value.GetString()!;
    }

    private static long ReadRequiredLongClaim(JsonElement payload, string claimName)
    {
        if (!payload.TryGetProperty(claimName, out var value))
        {
            throw Unauthorized("Bearer token is invalid.");
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String
            && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
        {
            return number;
        }

        throw Unauthorized("Bearer token is invalid.");
    }

    private static ApiUnauthorizedException Unauthorized(string message)
    {
        return new ApiUnauthorizedException(
            message,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["scheme"] = "Bearer"
            });
    }
}
