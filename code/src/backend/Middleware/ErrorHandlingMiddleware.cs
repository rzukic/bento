using Bento.Api.Exceptions;
using Bento.Api.Models;
using Bento.Api.Services;

namespace Bento.Api.Middleware;

public sealed class ErrorHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ErrorHandlingMiddleware> _logger;
    private readonly IHostEnvironment _environment;

    public ErrorHandlingMiddleware(
        RequestDelegate next,
        ILogger<ErrorHandlingMiddleware> logger,
        IHostEnvironment environment)
    {
        _next = next;
        _logger = logger;
        _environment = environment;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            // The browser went away (a widget switched from M to Q): its query was
            // cancelled in the database; nobody waits for an answer.
            if (context.RequestAborted.IsCancellationRequested)
            {
                _logger.LogDebug("HTTP {Method} {Path} cancelled by the client after {Ms} ms", context.Request.Method, context.Request.Path.Value ?? "/", watch.ElapsedMilliseconds);
                if (!context.Response.HasStarted)
                    context.Response.StatusCode = StatusCodeClientClosedRequest;
                return;
            }

            if (context.Response.HasStarted)
            {
                throw;
            }

            var mapped = MapException(ex, context.TraceIdentifier);
            if (mapped.StatusCode >= 500 && context.RequestServices?.GetService(typeof(ProblemLog)) is ProblemLog problems && !context.Items.ContainsKey(ProblemLog.LoggedItem))
            {
                var timeout = mapped.Response.Error.Code == ApiErrorCode.Timeout.ToStringCode();
                var query = context.Items.TryGetValue(ProblemLog.QueryItem, out var q) ? q as string : null;
                var detail = timeout ? query : (query is null ? "" : $"Query: {query}\n\n") + ex;
                await problems.RecordAsync(timeout ? "timeout" : "error", timeout ? mapped.Response.Error.Message : $"{ex.GetType().Name}: {ex.Message}", detail, watch.ElapsedMilliseconds, mapped.StatusCode);
            }

            _logger.LogError(
                ex,
                "HTTP {Method} {Path} failed with {StatusCode} {ErrorCode} for {RequestId}",
                context.Request.Method,
                context.Request.Path.Value ?? "/",
                mapped.StatusCode,
                mapped.Response.Error.Code,
                context.TraceIdentifier);

            context.Response.Clear();
            context.Response.StatusCode = mapped.StatusCode;
            context.Response.ContentType = "application/json";

            await context.Response.WriteAsJsonAsync(mapped.Response);
        }
    }

    /// <summary>nginx's "client closed request": the answer was not wanted any more.</summary>
    private const int StatusCodeClientClosedRequest = 499;

    private MappedError MapException(Exception exception, string? requestId)
    {
        if (exception is ApiException apiException)
        {
            return new MappedError(
                apiException.StatusCode,
                new ApiErrorEnvelope(new ApiError(
                    apiException.Code.ToStringCode(),
                    apiException.Message,
                    MergeDetails(apiException.Details, requestId))));
        }

        if (IsTimeout(exception))
        {
            return new MappedError(
                StatusCodes.Status504GatewayTimeout,
                new ApiErrorEnvelope(new ApiError(
                    ApiErrorCode.Timeout.ToStringCode(),
                    "The database took too long and stopped the query. Try a coarser period (month instead of week) or a shorter range.",
                    MergeDetails(null, requestId))));
        }

        var details = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["requestId"] = requestId
        };

        if (_environment.IsDevelopment())
        {
            details["exceptionType"] = exception.GetType().FullName;
            details["stackTrace"] = exception.StackTrace;
        }

        return new MappedError(
            StatusCodes.Status500InternalServerError,
            new ApiErrorEnvelope(new ApiError(
                ApiErrorCode.InternalError.ToStringCode(),
                "An unexpected error occurred.",
                details)));
    }

    /// <summary>The command timeout (Npgsql) or a statement timeout (Postgres cancels with 57014).</summary>
    private static bool IsTimeout(Exception exception)
    {
        for (Exception? e = exception; e is not null; e = e.InnerException)
        {
            if (e is TimeoutException or Npgsql.PostgresException { SqlState: "57014" })
                return true;
        }
        return false;
    }

    private static IReadOnlyDictionary<string, object?> MergeDetails(
        IReadOnlyDictionary<string, object?>? details,
        string? requestId)
    {
        var merged = new Dictionary<string, object?>(StringComparer.Ordinal);

        if (details is not null)
        {
            foreach (var pair in details)
            {
                merged[pair.Key] = pair.Value;
            }
        }

        merged["requestId"] = requestId;
        return merged;
    }

    private sealed record MappedError(int StatusCode, ApiErrorEnvelope Response);
}
