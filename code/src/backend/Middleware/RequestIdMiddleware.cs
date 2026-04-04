namespace Repere.Api.Middleware;

public sealed class RequestIdMiddleware
{
    public const string HeaderName = "X-Request-Id";
    private const int MaxRequestIdLength = 128;

    private readonly RequestDelegate _next;
    private readonly ILogger<RequestIdMiddleware> _logger;

    public RequestIdMiddleware(RequestDelegate next, ILogger<RequestIdMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var requestId = ResolveRequestId(context);

        context.TraceIdentifier = requestId;
        context.Response.Headers[HeaderName] = requestId;

        using (_logger.BeginScope(new Dictionary<string, object?>
        {
            ["RequestId"] = requestId
        }))
        {
            await _next(context);
        }
    }

    internal static string ResolveRequestId(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue(HeaderName, out var values))
        {
            var candidate = values.FirstOrDefault();
            if (IsSafeRequestId(candidate))
            {
                return candidate!;
            }
        }

        return Guid.NewGuid().ToString("N");
    }

    internal static bool IsSafeRequestId(string? requestId)
    {
        if (string.IsNullOrWhiteSpace(requestId) || requestId.Length > MaxRequestIdLength)
        {
            return false;
        }

        foreach (var ch in requestId)
        {
            if (!(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.'))
            {
                return false;
            }
        }

        return true;
    }
}
