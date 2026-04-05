using Repere.Api.Configuration;
using Repere.Api.Exceptions;

namespace Repere.Api.Middleware;

public sealed class CorsPolicyMiddleware
{
    private const string OriginHeader = "Origin";
    private const string AllowOriginHeader = "Access-Control-Allow-Origin";
    private const string AllowMethodsHeader = "Access-Control-Allow-Methods";
    private const string AllowHeadersHeader = "Access-Control-Allow-Headers";
    private const string ExposeHeadersHeader = "Access-Control-Expose-Headers";
    private const string VaryHeader = "Vary";
    private const string RequestMethodHeader = "Access-Control-Request-Method";
    private const string RequestHeadersHeader = "Access-Control-Request-Headers";

    private static readonly string[] DefaultAllowedMethods =
    [
        HttpMethods.Get,
        HttpMethods.Post,
        HttpMethods.Put,
        HttpMethods.Patch,
        HttpMethods.Delete,
        HttpMethods.Options
    ];

    private readonly RequestDelegate _next;
    private readonly BackendRuntimeOptions _runtimeOptions;

    public CorsPolicyMiddleware(RequestDelegate next, BackendRuntimeOptions runtimeOptions)
    {
        _next = next;
        _runtimeOptions = runtimeOptions;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var origin = context.Request.Headers[OriginHeader].ToString();
        if (string.IsNullOrWhiteSpace(origin))
        {
            await _next(context);
            return;
        }

        if (!IsAllowedOrigin(context, origin))
        {
            throw new ApiForbiddenException(
                "CORS origin is not allowed.",
                new Dictionary<string, object?>
                {
                    ["origin"] = origin
                });
        }

        ApplyCorsHeaders(context, origin);

        if (IsPreflightRequest(context.Request))
        {
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        await _next(context);
    }

    private bool IsAllowedOrigin(HttpContext context, string origin)
    {
        var normalizedOrigin = NormalizeOrigin(origin);
        if (normalizedOrigin is null)
        {
            return false;
        }

        var requestOrigin = BuildRequestOrigin(context.Request);
        if (string.Equals(normalizedOrigin, requestOrigin, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return _runtimeOptions.AllowedOrigins
            .Select(NormalizeOrigin)
            .Any(allowedOrigin => string.Equals(allowedOrigin, normalizedOrigin, StringComparison.OrdinalIgnoreCase));
    }

    private static void ApplyCorsHeaders(HttpContext context, string origin)
    {
        context.Response.Headers[AllowOriginHeader] = origin;
        context.Response.Headers[ExposeHeadersHeader] = RequestIdMiddleware.HeaderName;
        context.Response.Headers[VaryHeader] = OriginHeader;

        if (IsPreflightRequest(context.Request))
        {
            context.Response.Headers[AllowMethodsHeader] = string.Join(", ", DefaultAllowedMethods);

            var requestedHeaders = context.Request.Headers[RequestHeadersHeader].ToString();
            context.Response.Headers[AllowHeadersHeader] = string.IsNullOrWhiteSpace(requestedHeaders)
                ? "Content-Type, Authorization, X-API-Key, X-Request-Id"
                : requestedHeaders;
        }
    }

    private static bool IsPreflightRequest(HttpRequest request)
    {
        return HttpMethods.IsOptions(request.Method) &&
               !string.IsNullOrWhiteSpace(request.Headers[RequestMethodHeader].ToString());
    }

    private static string BuildRequestOrigin(HttpRequest request)
    {
        return $"{request.Scheme}://{request.Host.Value}".TrimEnd('/');
    }

    private static string? NormalizeOrigin(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin))
        {
            return null;
        }

        return Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            ? uri.GetLeftPart(UriPartial.Authority).TrimEnd('/')
            : null;
    }
}
