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
        return Ok(new
        {
            data = grants.Select(g => new
            {
                id = g.Id,
                clientName = g.ClientName,
                username = g.Username,
                scope = g.Scope,
                permissions = g.Permissions,
                createdAt = g.CreatedAt,
                lastUsedAt = g.LastUsedAt
            })
        });
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
    private long? ScopeUserId() => User.GetRole() == "admin" ? null : User.GetUserId();

    private void EnsureSignedInUser()
    {
        if (User.FindFirstValue("auth_method") is "api_key" or "oauth")
            throw new ApiForbiddenException("Connections are managed by the signed-in user, not by a key or token.");
    }
}
