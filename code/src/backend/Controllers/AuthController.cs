using Microsoft.AspNetCore.Mvc;
using Repere.Api.Configuration;
using Repere.Api.Models;

namespace Repere.Api.Controllers;

[ApiController]
[Route(ApiRoutes.Auth)]
public sealed class AuthController : ControllerBase
{
    private static readonly string[] DeferredAuthFeatures =
    [
        "interactive login and registration endpoints",
        "jwt issuance, validation, and refresh-token persistence",
        "authorization middleware and role attributes",
        "api key authentication",
        "vault mode"
    ];

    private readonly BackendRuntimeOptions _runtimeOptions;

    public AuthController(BackendRuntimeOptions runtimeOptions)
    {
        _runtimeOptions = runtimeOptions;
    }

    [HttpGet]
    [ProducesResponseType<AuthRuntimeContractResponse>(StatusCodes.Status200OK)]
    public ActionResult<AuthRuntimeContractResponse> GetContract()
    {
        var response = new AuthRuntimeContractResponse(
            Mode: _runtimeOptions.AuthMode.ToString().ToLowerInvariant(),
            AuthRequired: _runtimeOptions.IsStandardAuthMode,
            RegistrationBootstrapOpen: _runtimeOptions.IsOpenAuthMode || _runtimeOptions.IsStandardAuthMode,
            JwtConfigured: !string.IsNullOrWhiteSpace(_runtimeOptions.Jwt.Secret),
            JwtIssuer: _runtimeOptions.Jwt.Issuer,
            JwtAudience: _runtimeOptions.Jwt.Audience,
            AccessTokenLifetimeMinutes: _runtimeOptions.Jwt.AccessTokenLifetimeMinutes,
            RefreshTokenLifetimeDays: _runtimeOptions.Jwt.RefreshTokenLifetimeDays,
            AuthRateLimitPerMinute: _runtimeOptions.AuthRateLimitPerMinute,
            RefreshCookie: new AuthRefreshCookieContractResponse(
                Name: _runtimeOptions.AuthCookie.RefreshCookieName,
                HttpOnly: _runtimeOptions.AuthCookie.HttpOnly,
                RequireSecure: _runtimeOptions.AuthCookie.RequireSecure,
                SameSite: _runtimeOptions.AuthCookie.SameSite,
                Path: _runtimeOptions.AuthCookie.Path),
            Deferred: DeferredAuthFeatures);

        return Ok(response);
    }
}
