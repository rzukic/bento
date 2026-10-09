using Microsoft.AspNetCore.Mvc;
using Bento.Api.Configuration;
using Bento.Api.Services.Mcp;

namespace Bento.Api.Controllers;

/// <summary>
/// Discovery for MCP clients: the protected resource (RFC 9728) points at Bento
/// as its own authorization server, whose metadata (RFC 8414) lists the
/// endpoints. Public; the consent page is the app's <c>/oauth/authorize</c>.
/// </summary>
[ApiController]
[Route(".well-known")]
public sealed class WellKnownController : ControllerBase
{
    private readonly BackendRuntimeOptions _runtimeOptions;

    public WellKnownController(BackendRuntimeOptions runtimeOptions)
    {
        _runtimeOptions = runtimeOptions;
    }

    [HttpGet("oauth-protected-resource")]
    [HttpGet("oauth-protected-resource/mcp")]
    public IActionResult ProtectedResource()
    {
        var baseUrl = PublicUrl.For(Request, _runtimeOptions);
        AllowAnyOrigin();
        return Ok(new Dictionary<string, object?>
        {
            ["resource"] = baseUrl + "/mcp",
            ["authorization_servers"] = new[] { baseUrl },
            ["scopes_supported"] = new[] { "read", "write" },
            ["bearer_methods_supported"] = new[] { "header" },
            ["resource_name"] = "Bento"
        });
    }

    [HttpGet("oauth-authorization-server")]
    [HttpGet("openid-configuration")]
    public IActionResult AuthorizationServer()
    {
        var baseUrl = PublicUrl.For(Request, _runtimeOptions);
        AllowAnyOrigin();
        return Ok(new Dictionary<string, object?>
        {
            ["issuer"] = baseUrl,
            ["authorization_endpoint"] = baseUrl + "/oauth/authorize",
            ["token_endpoint"] = baseUrl + "/" + ApiRoutes.OAuthToken,
            ["registration_endpoint"] = baseUrl + "/" + ApiRoutes.OAuthRegister,
            ["scopes_supported"] = new[] { "read", "write" },
            ["response_types_supported"] = new[] { "code" },
            ["grant_types_supported"] = new[] { "authorization_code", "refresh_token" },
            ["token_endpoint_auth_methods_supported"] = new[] { "none" },
            ["code_challenge_methods_supported"] = new[] { "S256" }
        });
    }

    /// <summary>Discovery documents are public facts; browser-based clients may read them from anywhere.</summary>
    private void AllowAnyOrigin() => Response.Headers.AccessControlAllowOrigin = "*";
}
