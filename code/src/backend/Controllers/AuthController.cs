using Microsoft.AspNetCore.Mvc;
using Bento.Api.Configuration;
using Bento.Api.Models;
using Bento.Api.Services;

namespace Bento.Api.Controllers;

[ApiController]
[Route(ApiRoutes.Auth)]
public sealed class AuthController : ControllerBase
{
    private static readonly string[] DeferredAuthFeatures =
    [
        "api key authentication",
        "CSRF protections until the browser-facing frontend exists",
        "CSP headers until the browser-facing frontend exists",
        "vault mode"
    ];

    /// <summary>
    /// The refresh cookie's name before the rename to Bento. A browser that
    /// still holds it is signed in once more through it; the next response
    /// moves the session to the current cookie and clears the old one.
    /// </summary>
    private const string LegacyRefreshCookieName = "repere_refresh";

    private readonly BackendRuntimeOptions _runtimeOptions;
    private readonly AuthService _authService;

    public AuthController(BackendRuntimeOptions runtimeOptions, AuthService authService)
    {
        _runtimeOptions = runtimeOptions;
        _authService = authService;
    }

    [HttpGet]
    [ProducesResponseType<AuthRuntimeContractResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<AuthRuntimeContractResponse>> GetContract(CancellationToken cancellationToken)
    {
        var registrationBootstrapOpen = await _authService.IsBootstrapSelfRegistrationOpenAsync(cancellationToken);

        var response = new AuthRuntimeContractResponse(
            Mode: _runtimeOptions.AuthMode.ToString().ToLowerInvariant(),
            AuthRequired: _runtimeOptions.IsStandardAuthMode,
            RegistrationBootstrapOpen: registrationBootstrapOpen,
            JwtConfigured: !string.IsNullOrWhiteSpace(_runtimeOptions.Jwt.Secret),
            JwtIssuer: _runtimeOptions.Jwt.Issuer,
            JwtAudience: _runtimeOptions.Jwt.Audience,
            AccessTokenLifetimeMinutes: _runtimeOptions.Jwt.AccessTokenLifetimeMinutes,
            RefreshTokenLifetimeDays: _runtimeOptions.Jwt.RefreshTokenLifetimeDays,
            AuthRateLimitPerMinute: _runtimeOptions.AuthRateLimitPerMinute,
            SessionRateLimitPerMinute: _runtimeOptions.AuthSessionRateLimitPerMinute,
            RefreshCookie: new AuthRefreshCookieContractResponse(
                Name: _runtimeOptions.AuthCookie.RefreshCookieName,
                HttpOnly: _runtimeOptions.AuthCookie.HttpOnly,
                RequireSecure: _runtimeOptions.AuthCookie.EffectiveRequireSecure,
                SameSite: _runtimeOptions.AuthCookie.SameSite,
                Path: _runtimeOptions.AuthCookie.Path),
            Deferred: DeferredAuthFeatures);

        return Ok(response);
    }

    [HttpPost("register")]
    [ProducesResponseType<RegisterUserResponse>(StatusCodes.Status201Created)]
    public async Task<ActionResult<RegisterUserResponse>> Register(
        [FromBody] RegisterUserRequest? request,
        CancellationToken cancellationToken)
    {
        var response = await _authService.RegisterAsync(request, User, cancellationToken);
        return StatusCode(StatusCodes.Status201Created, response);
    }

    [HttpPost("login")]
    [ProducesResponseType<AuthTokenResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<AuthTokenResponse>> Login(
        [FromBody] LoginRequest? request,
        CancellationToken cancellationToken)
    {
        var session = await _authService.LoginAsync(request, cancellationToken);
        WriteRefreshCookie(session);
        return Ok(session.Response);
    }

    [HttpPost("refresh")]
    [ProducesResponseType<AuthTokenResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<AuthTokenResponse>> Refresh(CancellationToken cancellationToken)
    {
        var refreshToken = ReadRefreshCookie();
        var session = await _authService.RefreshAsync(refreshToken, cancellationToken);
        WriteRefreshCookie(session);
        return Ok(session.Response);
    }

    /// <summary>
    /// Ends the browser session: revokes the refresh session named by the
    /// cookie (if any) and clears the cookie. Always 204 so a stale or missing
    /// cookie never blocks signing out.
    /// </summary>
    [HttpPost("logout")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken)
    {
        var refreshToken = ReadRefreshCookie();
        await _authService.LogoutAsync(refreshToken, cancellationToken);
        Response.Cookies.Delete(_runtimeOptions.AuthCookie.RefreshCookieName, BuildRefreshCookieOptions(expires: null));
        ClearLegacyRefreshCookie();
        return NoContent();
    }

    private void WriteRefreshCookie(AuthSessionResult session)
    {
        Response.Cookies.Append(
            _runtimeOptions.AuthCookie.RefreshCookieName,
            session.RefreshToken,
            BuildRefreshCookieOptions(session.RefreshExpiresAt.UtcDateTime));
        ClearLegacyRefreshCookie();
    }

    private string? ReadRefreshCookie()
    {
        if (Request.Cookies.TryGetValue(_runtimeOptions.AuthCookie.RefreshCookieName, out var refreshToken))
            return refreshToken;
        Request.Cookies.TryGetValue(LegacyRefreshCookieName, out var legacyToken);
        return legacyToken;
    }

    private void ClearLegacyRefreshCookie()
    {
        if (Request.Cookies.ContainsKey(LegacyRefreshCookieName) &&
            !string.Equals(LegacyRefreshCookieName, _runtimeOptions.AuthCookie.RefreshCookieName, StringComparison.Ordinal))
            Response.Cookies.Delete(LegacyRefreshCookieName, BuildRefreshCookieOptions(expires: null));
    }

    private CookieOptions BuildRefreshCookieOptions(DateTime? expires)
    {
        return new CookieOptions
        {
            HttpOnly = _runtimeOptions.AuthCookie.HttpOnly,
            // Request.IsHttps reflects X-Forwarded-Proto from a trusted proxy.
            Secure = _runtimeOptions.AuthCookie.EffectiveRequireSecure || Request.IsHttps,
            SameSite = ParseSameSite(_runtimeOptions.AuthCookie.SameSite),
            Path = _runtimeOptions.AuthCookie.Path,
            Expires = expires
        };
    }

    private static SameSiteMode ParseSameSite(string value)
    {
        return value switch
        {
            "Strict" => SameSiteMode.Strict,
            "Lax" => SameSiteMode.Lax,
            "None" => SameSiteMode.None,
            _ => SameSiteMode.Strict
        };
    }
}
