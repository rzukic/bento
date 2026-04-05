using Microsoft.AspNetCore.Http.Features;
using Repere.Api.Configuration;
using Repere.Api.Exceptions;

namespace Repere.Api.Middleware;

public sealed class BodySizeLimitMiddleware
{
    private readonly RequestDelegate _next;
    private readonly BackendRuntimeOptions _runtimeOptions;

    public BodySizeLimitMiddleware(RequestDelegate next, BackendRuntimeOptions runtimeOptions)
    {
        _next = next;
        _runtimeOptions = runtimeOptions;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!IsJsonRequest(context.Request))
        {
            await _next(context);
            return;
        }

        var maxBodySize = _runtimeOptions.GetMaxBodySizeBytes();
        ApplyServerLimitFeature(context, maxBodySize);

        var effectiveLength = GetEffectiveContentLength(context.Request);
        if (effectiveLength is not null && effectiveLength.Value > maxBodySize)
        {
            throw new ApiRequestTooLargeException(
                $"Request body exceeds configured max size of {_runtimeOptions.MaxBodySize}.",
                new Dictionary<string, object?>
                {
                    ["maxBodySize"] = _runtimeOptions.MaxBodySize,
                    ["contentLength"] = effectiveLength.Value
                });
        }

        await _next(context);
    }

    private static bool IsJsonRequest(HttpRequest request)
    {
        return request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static long? GetEffectiveContentLength(HttpRequest request)
    {
        if (request.ContentLength is not null)
        {
            return request.ContentLength.Value;
        }

        if (request.Body.CanSeek)
        {
            return request.Body.Length;
        }

        return null;
    }

    private static void ApplyServerLimitFeature(HttpContext context, long maxBodySize)
    {
        var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is not null && !feature.IsReadOnly)
        {
            feature.MaxRequestBodySize = maxBodySize;
        }
    }
}
