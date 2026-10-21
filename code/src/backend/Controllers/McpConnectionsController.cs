using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Bento.Api.Authorization;
using Bento.Api.Configuration;
using Bento.Api.Exceptions;
using Bento.Api.Services;
using Bento.Api.Services.Mcp;

namespace Bento.Api.Controllers;

/// <summary>
/// The "Claude" page of Connections (Step 4.6): the MCP address to paste into a
/// client, the apps connected with the user's account (revocable) and the log
/// of MCP calls. Users see their own; admins see everyone's.
/// </summary>
[ApiController]
[Route(ApiRoutes.McpConnections)]
[RequireRole("viewer")]
public sealed class McpConnectionsController : ControllerBase
{
    private readonly OAuthService _oauth;
    private readonly McpCallLog _calls;
    private readonly BackendRuntimeOptions _runtimeOptions;
    private readonly Services.Workspaces.WorkspaceService _workspaces;
    private readonly Services.Workspaces.WorkspaceAccess _access;

    public McpConnectionsController(OAuthService oauth, McpCallLog calls, BackendRuntimeOptions runtimeOptions,
        Services.Workspaces.WorkspaceService workspaces, Services.Workspaces.WorkspaceAccess access)
    {
        _oauth = oauth;
        _calls = calls;
        _runtimeOptions = runtimeOptions;
        _workspaces = workspaces;
        _access = access;
    }

    /// <summary>The MCP address of the workspace the app is open in, and whether the user may write there.</summary>
    [HttpGet]
    public async Task<IActionResult> Info()
    {
        var role = User.GetUserId() is { } id && id > 0
            ? await _access.RoleInAsync(id, User.GetInstallationRole(), Services.Workspaces.WorkspaceContext.Id)
            : User.GetRole();
        return Ok(new
        {
            data = new
            {
                endpoint = PublicUrl.McpEndpoint(Request, _runtimeOptions),
                oauth = _runtimeOptions.IsStandardAuthMode,
                canWrite = role is "admin" or "editor"
            }
        });
    }

    [HttpGet("connections")]
    public async Task<IActionResult> Connections()
    {
        EnsureSignedInUser();
        var grants = await _oauth.ListGrantsAsync(ScopeUserId());
        var result = new List<object>();
        foreach (var grant in grants)
            result.Add(await ToResponseAsync(grant));
        return Ok(new { data = result });
    }

    [HttpGet("connections/{id:long}")]
    public async Task<IActionResult> Connection(long id)
    {
        EnsureSignedInUser();
        return Ok(new { data = await ToResponseAsync(await _oauth.GetGrantAsync(id, ScopeUserId())) });
    }

    public sealed record PermissionsRequest
    {
        public IReadOnlyList<string>? Permissions { get; init; }
        /// <summary>The workspaces it may use from now on (Step 5.5); null keeps them.</summary>
        public IReadOnlyList<long>? Workspaces { get; init; }
    }

    /// <summary>Changes what a connected app may do (capped by its user's role); applies to its next call.</summary>
    [HttpPut("connections/{id:long}")]
    public async Task<IActionResult> UpdatePermissions(long id, [FromBody] PermissionsRequest request)
    {
        EnsureSignedInUser();
        var unknown = (request.Permissions ?? []).Where(p => !McpPermissions.All.Contains(p, StringComparer.Ordinal)).ToList();
        if (unknown.Count > 0)
            throw new ApiValidationException($"Unknown permission: {string.Join(", ", unknown)}.", new Dictionary<string, object?> { ["field"] = "permissions" });
        var existing = await _oauth.GetGrantAsync(id, ScopeUserId());
        var openable = await OpenableAsync(existing);
        if (request.Workspaces is { } chosen && (chosen.Count == 0 || chosen.Any(w => !openable.ContainsKey(w))))
            throw new ApiValidationException("Choose at least one workspace its user can open.", new Dictionary<string, object?> { ["field"] = "workspaces" });
        var grant = await _oauth.UpdateGrantPermissionsAsync(id, ScopeUserId(), request.Permissions ?? [],
            Services.Workspaces.WorkspaceAccess.Highest(openable.Values), request.Workspaces?.Distinct().ToList());
        return Ok(new { data = await ToResponseAsync(grant) });
    }

    /// <summary>The calls made through one connected app.</summary>
    [HttpGet("connections/{id:long}/calls")]
    public async Task<IActionResult> ConnectionCalls(long id, [FromQuery] int limit = 100)
    {
        EnsureSignedInUser();
        var grant = await _oauth.GetGrantAsync(id, ScopeUserId());
        var calls = await _calls.ListForGrantAsync(grant.Id, grant.UserId, grant.ClientName, grant.CreatedAt, limit);
        return Ok(new { data = calls });
    }

    /// <summary>The workspaces the grant's user can open, with their role in each.</summary>
    private async Task<Dictionary<long, string>> OpenableAsync(OAuthService.GrantRow g)
    {
        var result = new Dictionary<long, string>();
        foreach (var workspace in await _workspaces.ReloadAsync())
        {
            if (await _access.RoleInAsync(g.UserId, g.UserRole, workspace.Id) is { } role)
                result[workspace.Id] = role;
        }
        return result;
    }

    private async Task<object> ToResponseAsync(OAuthService.GrantRow g)
    {
        var openable = await OpenableAsync(g);
        var best = Services.Workspaces.WorkspaceAccess.Highest(openable.Values) ?? g.UserRole;
        var all = _workspaces.All;
        return new
        {
            id = g.Id,
            clientName = g.ClientName,
            username = g.Username,
            scope = g.Scope,
            permissions = McpPermissions.Cap(g.Permissions, best),
            // What the user behind the app could grant it; each call is capped by their role in its workspace.
            allowed = McpPermissions.ForRole(best),
            // Null: every workspace.
            workspaces = g.Workspaces,
            // The workspaces its user can open, to choose from.
            openable = all.Where(w => openable.ContainsKey(w.Id)).Select(w => new Models.WorkspaceResponse(w.Id, w.Slug, w.Name, w.IsDefault, openable[w.Id])),
            createdAt = g.CreatedAt,
            lastUsedAt = g.LastUsedAt
        };
    }

    [HttpDelete("connections/{id:long}")]
    public async Task<IActionResult> Revoke(long id)
    {
        EnsureSignedInUser();
        await _oauth.RevokeGrantAsync(id, ScopeUserId());
        return NoContent();
    }

    [HttpGet("calls")]
    public async Task<IActionResult> Calls([FromQuery] int limit = 50)
    {
        EnsureSignedInUser();
        var calls = await _calls.ListAsync(ScopeUserId(), limit);
        return Ok(new { data = calls });
    }

    /// <summary>Admins look at everyone's connections and calls; everyone else at their own.</summary>
    private long? ScopeUserId() => User.IsInstallationAdmin() ? null : User.GetUserId();

    private void EnsureSignedInUser()
    {
        if (User.FindFirstValue("auth_method") is "api_key" or "oauth")
            throw new ApiForbiddenException("Connections are managed by the signed-in user, not by a key or token.");
    }
}
