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

    public McpConnectionsController(OAuthService oauth, McpCallLog calls, BackendRuntimeOptions runtimeOptions)
    {
        _oauth = oauth;
        _calls = calls;
        _runtimeOptions = runtimeOptions;
    }

    [HttpGet]
    public IActionResult Info() => Ok(new
    {
        data = new
        {
            endpoint = PublicUrl.McpEndpoint(Request, _runtimeOptions),
            oauth = _runtimeOptions.IsStandardAuthMode,
            canWrite = User.GetRole() is "admin" or "editor"
        }
    });

    [HttpGet("connections")]
    public async Task<IActionResult> Connections()
    {
        EnsureSignedInUser();
        var grants = await _oauth.ListGrantsAsync(ScopeUserId());
        return Ok(new { data = grants.Select(ToResponse) });
    }

    [HttpGet("connections/{id:long}")]
    public async Task<IActionResult> Connection(long id)
    {
        EnsureSignedInUser();
        return Ok(new { data = ToResponse(await _oauth.GetGrantAsync(id, ScopeUserId())) });
    }

    public sealed record PermissionsRequest
    {
        public IReadOnlyList<string>? Permissions { get; init; }
    }

    /// <summary>Changes what a connected app may do (capped by its user's role); applies to its next call.</summary>
    [HttpPut("connections/{id:long}")]
    public async Task<IActionResult> UpdatePermissions(long id, [FromBody] PermissionsRequest request)
    {
        EnsureSignedInUser();
        var unknown = (request.Permissions ?? []).Where(p => !McpPermissions.All.Contains(p, StringComparer.Ordinal)).ToList();
        if (unknown.Count > 0)
            throw new ApiValidationException($"Unknown permission: {string.Join(", ", unknown)}.", new Dictionary<string, object?> { ["field"] = "permissions" });
        var grant = await _oauth.UpdateGrantPermissionsAsync(id, ScopeUserId(), request.Permissions ?? []);
        return Ok(new { data = ToResponse(grant) });
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

    private static object ToResponse(OAuthService.GrantRow g) => new
    {
        id = g.Id,
        clientName = g.ClientName,
        username = g.Username,
        scope = g.Scope,
        permissions = McpPermissions.Cap(g.Permissions, g.UserRole),
        // What the user behind the app could grant it.
        allowed = McpPermissions.ForRole(g.UserRole),
        createdAt = g.CreatedAt,
        lastUsedAt = g.LastUsedAt
    };

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
    private long? ScopeUserId() => User.GetRole() == "admin" ? null : User.GetUserId();

    private void EnsureSignedInUser()
    {
        if (User.FindFirstValue("auth_method") is "api_key" or "oauth")
            throw new ApiForbiddenException("Connections are managed by the signed-in user, not by a key or token.");
    }
}
