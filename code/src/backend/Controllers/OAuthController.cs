using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Bento.Api.Authorization;
using Bento.Api.Configuration;
using Bento.Api.Exceptions;
using Bento.Api.Services;
using Bento.Api.Services.Mcp;

namespace Bento.Api.Controllers;

/// <summary>
/// OAuth 2.1 for MCP clients (Step 4.6). <c>register</c> and <c>token</c> are
/// public and speak RFC 7591 / RFC 6749 (errors as <c>{ error, error_description }</c>);
/// the consent page of the app (<c>/oauth/authorize</c>) uses <c>authorize</c> with
/// the signed-in user's session.
/// </summary>
[ApiController]
[Route(ApiRoutes.OAuth)]
public sealed class OAuthController : ControllerBase
{
    private readonly OAuthService _oauth;
    private readonly BackendRuntimeOptions _runtimeOptions;
    private readonly Services.Workspaces.WorkspaceService _workspaces;
    private readonly Services.Workspaces.WorkspaceAccess _access;

    public OAuthController(OAuthService oauth, BackendRuntimeOptions runtimeOptions, Services.Workspaces.WorkspaceService workspaces,
        Services.Workspaces.WorkspaceAccess access)
    {
        _workspaces = workspaces;
        _access = access;
        _oauth = oauth;
        _runtimeOptions = runtimeOptions;
    }

    // --- Dynamic client registration (RFC 7591) ----------------------------------

    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] JsonElement body)
    {
        try
        {
            if (body.ValueKind != JsonValueKind.Object)
                throw new OAuthException("invalid_client_metadata", "The body must be a JSON object.");
            var name = body.TryGetProperty("client_name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            var uris = body.TryGetProperty("redirect_uris", out var r) && r.ValueKind == JsonValueKind.Array
                ? r.EnumerateArray().Select(u => u.ValueKind == JsonValueKind.String ? u.GetString()! : "").ToList()
                : null;
            if (body.TryGetProperty("grant_types", out var g) && g.ValueKind == JsonValueKind.Array &&
                g.EnumerateArray().Any(t => t.GetString() is not ("authorization_code" or "refresh_token")))
                throw new OAuthException("invalid_client_metadata", "Only authorization_code and refresh_token are supported.");

            var client = await _oauth.RegisterClientAsync(name, uris);
            NoStore();
            return StatusCode(StatusCodes.Status201Created, new Dictionary<string, object?>
            {
                ["client_id"] = client.ClientId,
                ["client_name"] = client.Name,
                ["redirect_uris"] = client.RedirectUris,
                ["grant_types"] = new[] { "authorization_code", "refresh_token" },
                ["response_types"] = new[] { "code" },
                ["token_endpoint_auth_method"] = "none",
                ["client_id_issued_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            });
        }
        catch (OAuthException ex)
        {
            return OAuthError(ex);
        }
    }

    // --- Token endpoint ------------------------------------------------------------

    [HttpPost("token")]
    [Consumes("application/x-www-form-urlencoded", "application/json")]
    public async Task<IActionResult> Token()
    {
        try
        {
            var form = await ReadParametersAsync();
            var grantType = form.GetValueOrDefault("grant_type");
            var clientId = form.GetValueOrDefault("client_id");
            if (string.IsNullOrWhiteSpace(clientId) || await _oauth.FindClientAsync(clientId) is null)
                throw new OAuthException("invalid_client", "Unknown client.", StatusCodes.Status401Unauthorized);

            var tokens = grantType switch
            {
                "authorization_code" => await _oauth.ExchangeCodeAsync(clientId, form.GetValueOrDefault("code"), form.GetValueOrDefault("redirect_uri"), form.GetValueOrDefault("code_verifier")),
                "refresh_token" => await _oauth.RefreshAsync(clientId, form.GetValueOrDefault("refresh_token")),
                _ => throw new OAuthException("unsupported_grant_type", "grant_type must be authorization_code or refresh_token.")
            };

            NoStore();
            return Ok(new Dictionary<string, object?>
            {
                ["access_token"] = tokens.AccessToken,
                ["token_type"] = "Bearer",
                ["expires_in"] = tokens.ExpiresIn,
                ["refresh_token"] = tokens.RefreshToken,
                ["scope"] = tokens.Scope
            });
        }
        catch (OAuthException ex)
        {
            return OAuthError(ex);
        }
    }

    // --- Consent (used by the app's /oauth/authorize page) ------------------------

    public sealed record AuthorizeRequest
    {
        public string? ClientId { get; init; }
        public string? RedirectUri { get; init; }
        public string? CodeChallenge { get; init; }
        public string? CodeChallengeMethod { get; init; }
        /// <summary>What the user ticked on the consent page (<see cref="McpPermissions"/> ids); capped by the user's role.</summary>
        public List<string>? Permissions { get; init; }
        /// <summary>Older form: read or read-write, used when <see cref="Permissions"/> is missing.</summary>
        public string? Scope { get; init; }
        public string? State { get; init; }
        public bool Approve { get; init; }
        /// <summary>The workspaces the app may use (Step 5.5); each one the user can open.</summary>
        public List<long>? Workspaces { get; init; }
    }

    /// <summary>The workspaces the signed-in user can open, with their role in each.</summary>
    private async Task<List<(Services.Workspaces.Workspace Workspace, string Role)>> OpenableAsync(long userId)
    {
        var result = new List<(Services.Workspaces.Workspace, string)>();
        foreach (var workspace in await _workspaces.ReloadAsync())
        {
            if (await _access.RoleInAsync(userId, User.GetInstallationRole(), workspace.Id) is { } role)
                result.Add((workspace, role));
        }
        return result;
    }

    /// <summary>The workspace an MCP address names: …/mcp/&lt;slug&gt; (RFC 8707 resource); …/mcp is the default one.</summary>
    private static string? SlugOfResource(string? resource)
    {
        if (string.IsNullOrWhiteSpace(resource) || !Uri.TryCreate(resource, UriKind.Absolute, out var uri))
            return null;
        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments switch
        {
            ["mcp"] => Services.Workspaces.Workspace.DefaultSlug,
            ["mcp", var slug] => slug,
            _ => null
        };
    }

    /// <summary>What the consent page shows: the client's name and where it will send the user back to.</summary>
    [HttpGet("authorize")]
    [RequireRole("viewer")]
    public async Task<IActionResult> AuthorizeInfo([FromQuery(Name = "client_id")] string? clientId, [FromQuery(Name = "redirect_uri")] string? redirectUri,
        [FromQuery(Name = "resource")] string? resource)
    {
        var userId = EnsureSignedInUser();
        try
        {
            var client = await _oauth.ResolveAuthorizationAsync(clientId, redirectUri);
            var openable = await OpenableAsync(userId);
            // What the user may grant: what their best role in any workspace allows; each call is capped by the role where it runs.
            var role = Services.Workspaces.WorkspaceAccess.Highest(openable.Select(o => o.Role)) ?? User.GetRole();
            // The address the app connects to names its workspace; without one, the default (or the first the user can open).
            var named = SlugOfResource(resource);
            var selected = openable.FirstOrDefault(o => string.Equals(o.Workspace.Slug, named, StringComparison.OrdinalIgnoreCase));
            if (selected.Workspace is null)
                selected = openable.FirstOrDefault(o => o.Workspace.IsDefault);
            if (selected.Workspace is null && openable.Count > 0)
                selected = openable[0];
            return Ok(new
            {
                data = new
                {
                    clientName = client.Name,
                    redirectHost = new Uri(redirectUri!).Authority is { Length: > 0 } host ? host : new Uri(redirectUri!).Scheme,
                    canWrite = role is "admin" or "editor",
                    // What this user may grant at all; the page greys out the rest.
                    allowed = McpPermissions.ForRole(role),
                    workspaces = openable.Select(o => new Models.WorkspaceResponse(o.Workspace.Id, o.Workspace.Slug, o.Workspace.Name, o.Workspace.IsDefault, o.Role)),
                    selectedWorkspaces = selected.Workspace is null ? Array.Empty<long>() : [selected.Workspace.Id],
                    // The app asked for one workspace (its MCP address); others can be added.
                    namedWorkspace = named
                }
            });
        }
        catch (OAuthException ex)
        {
            throw new ApiValidationException(ex.Message, new Dictionary<string, object?> { ["error"] = ex.Error });
        }
    }

    /// <summary>The user's answer: a redirect back to the client with a code, or with access_denied.</summary>
    [HttpPost("authorize")]
    [RequireRole("viewer")]
    public async Task<IActionResult> Authorize([FromBody] AuthorizeRequest request)
    {
        var userId = EnsureSignedInUser();
        try
        {
            await _oauth.ResolveAuthorizationAsync(request.ClientId, request.RedirectUri);
        }
        catch (OAuthException ex)
        {
            // Never redirect to an address that is not registered for the client.
            throw new ApiValidationException(ex.Message, new Dictionary<string, object?> { ["error"] = ex.Error });
        }

        var parameters = new Dictionary<string, string?>();
        if (!request.Approve)
        {
            parameters["error"] = "access_denied";
            parameters["error_description"] = "The user declined.";
        }
        else
        {
            if (!string.Equals(request.CodeChallengeMethod ?? "S256", "S256", StringComparison.Ordinal))
            {
                parameters["error"] = "invalid_request";
                parameters["error_description"] = "Only PKCE with S256 is supported.";
            }
            else
            {
                try
                {
                    var wanted = request.Permissions ?? (ApiKeyService.NormalizeScope(request.Scope ?? ApiKeyService.ReadScope) == ApiKeyService.ReadWriteScope
                        ? [McpPermissions.Read, McpPermissions.WriteRows]
                        : [McpPermissions.Read]);
                    var openable = await OpenableAsync(userId);
                    var chosen = request.Workspaces is { Count: > 0 } listed ? listed.Distinct().ToList() : [Services.Workspaces.Workspace.DefaultId];
                    var roles = chosen.Select(id => openable.FirstOrDefault(o => o.Workspace.Id == id).Role).ToList();
                    if (roles.Any(r => r is null))
                        throw new ApiValidationException("Choose workspaces you can open.", new Dictionary<string, object?> { ["field"] = "workspaces" });
                    var permissions = McpPermissions.Cap(wanted, Services.Workspaces.WorkspaceAccess.Highest(roles));
                    parameters["code"] = await _oauth.ApproveAsync(userId, request.ClientId!, request.RedirectUri!, request.CodeChallenge ?? "", permissions, chosen);
                }
                catch (OAuthException ex)
                {
                    parameters["error"] = ex.Error;
                    parameters["error_description"] = ex.Message;
                }
            }
        }
        if (!string.IsNullOrEmpty(request.State)) parameters["state"] = request.State;
        parameters["iss"] = PublicUrl.For(Request, _runtimeOptions);

        return Ok(new { data = new { redirectTo = AppendQuery(request.RedirectUri!, parameters) } });
    }

    // --- Helpers -------------------------------------------------------------------

    /// <summary>Consent needs the person, not a key or a token acting for them.</summary>
    private long EnsureSignedInUser()
    {
        if (User.FindFirstValue("auth_method") is "api_key" or "oauth")
            throw new ApiForbiddenException("Only a signed-in user can connect an app.");
        if (!_runtimeOptions.IsStandardAuthMode || User.GetUserId() is not { } userId || userId <= 0)
            throw new ApiValidationException("Connecting apps needs sign-in to be switched on (AUTH_MODE=standard).");
        return userId;
    }

    private async Task<Dictionary<string, string?>> ReadParametersAsync()
    {
        if (Request.HasFormContentType)
        {
            var form = await Request.ReadFormAsync();
            return form.ToDictionary(pair => pair.Key, pair => (string?)pair.Value.ToString());
        }
        using var document = await JsonDocument.ParseAsync(Request.Body);
        return document.RootElement.ValueKind == JsonValueKind.Object
            ? document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.GetRawText())
            : [];
    }

    private static string AppendQuery(string uri, Dictionary<string, string?> parameters)
    {
        var query = string.Join("&", parameters.Where(p => p.Value is not null).Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value!)}"));
        return uri + (uri.Contains('?') ? "&" : "?") + query;
    }

    private void NoStore()
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";
    }

    private ObjectResult OAuthError(OAuthException ex)
    {
        NoStore();
        return StatusCode(ex.Status, new Dictionary<string, string> { ["error"] = ex.Error, ["error_description"] = ex.Message });
    }
}
