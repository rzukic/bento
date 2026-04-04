using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Repere.Api.Middleware;

namespace Repere.Tests;

public sealed class RequestMiddlewareTests
{
    [Fact]
    public async Task RequestIdMiddleware_GeneratesTraceIdentifier_AndResponseHeader()
    {
        var logger = new TestLogger<RequestIdMiddleware>();
        var middleware = new RequestIdMiddleware(
            async context =>
            {
                await context.Response.WriteAsync("ok");
            },
            logger);

        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        Assert.True(LooksLikeSafeRequestId(context.TraceIdentifier));
        Assert.Equal(context.TraceIdentifier, context.Response.Headers[RequestIdMiddleware.HeaderName].ToString());
    }

    [Fact]
    public async Task RequestIdMiddleware_ReusesSafeIncomingHeader()
    {
        const string requestId = "req_abc-123.safe";

        var middleware = new RequestIdMiddleware(
            _ => Task.CompletedTask,
            new TestLogger<RequestIdMiddleware>());

        var context = new DefaultHttpContext();
        context.Request.Headers[RequestIdMiddleware.HeaderName] = requestId;

        await middleware.InvokeAsync(context);

        Assert.Equal(requestId, context.TraceIdentifier);
        Assert.Equal(requestId, context.Response.Headers[RequestIdMiddleware.HeaderName].ToString());
    }

    [Fact]
    public async Task RequestIdMiddleware_ReplacesUnsafeIncomingHeader()
    {
        var middleware = new RequestIdMiddleware(
            _ => Task.CompletedTask,
            new TestLogger<RequestIdMiddleware>());

        var context = new DefaultHttpContext();
        context.Request.Headers[RequestIdMiddleware.HeaderName] = "bad value with spaces";

        await middleware.InvokeAsync(context);

        Assert.NotEqual("bad value with spaces", context.TraceIdentifier);
        Assert.True(LooksLikeSafeRequestId(context.TraceIdentifier));
    }

    [Fact]
    public async Task RequestLoggingMiddleware_LogsStructuredCompletionEntry()
    {
        var logger = new TestLogger<RequestLoggingMiddleware>();
        var middleware = new RequestLoggingMiddleware(
            context =>
            {
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return Task.CompletedTask;
            },
            logger);

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/api/v1/admin/health";
        context.TraceIdentifier = "req_123";

        await middleware.InvokeAsync(context);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal(HttpMethods.Post, entry.Properties["Method"]);
        Assert.Equal("/api/v1/admin/health", entry.Properties["Path"]);
        Assert.Equal(StatusCodes.Status204NoContent, entry.Properties["StatusCode"]);
        Assert.Equal("req_123", entry.Properties["RequestId"]);
        Assert.True(Convert.ToDouble(entry.Properties["ElapsedMs"]) >= 0d);
        Assert.DoesNotContain("Body", entry.Properties.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("RequestBody", entry.Properties.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("ResponseBody", entry.Properties.Keys, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MiddlewareOrder_MakesRequestIdAvailableToRequestLogging()
    {
        var requestIdLogger = new TestLogger<RequestIdMiddleware>();
        var requestLoggingLogger = new TestLogger<RequestLoggingMiddleware>();

        var loggingMiddleware = new RequestLoggingMiddleware(
            context =>
            {
                context.Response.StatusCode = StatusCodes.Status200OK;
                return Task.CompletedTask;
            },
            requestLoggingLogger);

        var requestIdMiddleware = new RequestIdMiddleware(
            context => loggingMiddleware.InvokeAsync(context),
            requestIdLogger);

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/api/v1/admin/health";
        context.Request.Headers[RequestIdMiddleware.HeaderName] = "incoming-safe-id";

        await requestIdMiddleware.InvokeAsync(context);

        var entry = Assert.Single(requestLoggingLogger.Entries);
        Assert.Equal("incoming-safe-id", entry.Properties["RequestId"]);
        Assert.Equal("incoming-safe-id", context.Response.Headers[RequestIdMiddleware.HeaderName].ToString());
    }

    private sealed class TestLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
        {
            return NullScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var properties = new Dictionary<string, object?>(StringComparer.Ordinal);

            if (state is IEnumerable<KeyValuePair<string, object?>> structuredState)
            {
                foreach (var pair in structuredState)
                {
                    properties[pair.Key] = pair.Value;
                }
            }

            Entries.Add(new LogEntry(logLevel, formatter(state, exception), properties));
        }
    }

    private sealed record LogEntry(
        LogLevel Level,
        string Message,
        IReadOnlyDictionary<string, object?> Properties);

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }

    private static bool LooksLikeSafeRequestId(string? requestId)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            return false;
        }

        return requestId.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.');
    }
}
