using System.Diagnostics;

namespace Bento.Api.Middleware;

public sealed class RequestLoggingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RequestLoggingMiddleware> _logger;

    public RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            await _next(context);
        }
        finally
        {
            stopwatch.Stop();

            _logger.LogInformation(
                "HTTP {Method} {Path} completed with {StatusCode} in {ElapsedMs}ms for {RequestId}",
                context.Request.Method,
                context.Request.Path.Value ?? "/",
                context.Response.StatusCode,
                Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3),
                context.TraceIdentifier);
        }
    }
}
