using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Repere.Api.Configuration;
using Repere.Api.Exceptions;

namespace Repere.Api.Middleware;

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

    public async Task InvokeAsync(HttpContext context)
    {
        if (!IsVersionedApiRoute(context.Request.Path))
        {
            await _next(context);
            return;
        }

        if (IsPublicHealthRoute(context.Request.Path))
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

        var isAuthRoute = IsAuthRoute(context.Request.Path);
        if (isAuthRoute && string.IsNullOrWhiteSpace(context.Request.Headers.Authorization))
        {
            await _next(context);
            return;
        }

        context.User = CreateAuthenticatedPrincipal(context.Request.Headers.Authorization);

        await _next(context);
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
