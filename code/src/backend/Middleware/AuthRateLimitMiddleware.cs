using Bento.Api.Configuration;
using Bento.Api.Exceptions;

namespace Bento.Api.Middleware;

public sealed class AuthRateLimitMiddleware
{
    private readonly RequestDelegate _next;
    private readonly BackendRuntimeOptions _runtimeOptions;
    private readonly AuthRateLimiter _rateLimiter;

    public AuthRateLimitMiddleware(
        RequestDelegate next,
        BackendRuntimeOptions runtimeOptions,
        AuthRateLimiter rateLimiter)
    {
        _next = next;
        _runtimeOptions = runtimeOptions;
        _rateLimiter = rateLimiter;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var category = Classify(context.Request);
        if (category == RateLimitCategory.None)
        {
            await _next(context);
            return;
        }

        var path = context.Request.Path.Value ?? "/" + ApiRoutes.Auth;
        var limitPerMinute = category == RateLimitCategory.Session
            ? _runtimeOptions.AuthSessionRateLimitPerMinute
            : _runtimeOptions.AuthRateLimitPerMinute;

        // RemoteIpAddress is the real client behind a trusted proxy because the
        // forwarded-headers middleware (registered only when TRUSTED_PROXIES is set)
        // rewrites it before this middleware runs; otherwise it is the TCP peer.
        var clientIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        // The bucket key includes the (lower-cased) path, so session and credential
        // routes never share a budget.
        var outcome = _rateLimiter.CheckRequest(
            $"{context.Request.Method}:{path.ToLowerInvariant()}:{clientIp}",
            limitPerMinute,
            DateTimeOffset.UtcNow);

        if (!outcome.Allowed)
        {
            throw new ApiTooManyRequestsException(
                "Too many auth attempts. Try again later.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["field"] = "auth",
                    ["path"] = path,
                    ["limitPerMinute"] = limitPerMinute,
                    ["retryAfterSeconds"] = outcome.RetryAfterSeconds
                });
        }

        await _next(context);
    }

    private enum RateLimitCategory
    {
        None,
        Credential,
        Session
    }

    private static readonly string[] SessionRoutes =
    [
        "/" + ApiRoutes.Auth + "/refresh",
        "/" + ApiRoutes.Auth + "/logout"
    ];

    private static RateLimitCategory Classify(HttpRequest request)
    {
        if (!HttpMethods.IsPost(request.Method))
        {
            return RateLimitCategory.None;
        }

        // OAuth registration and tokens (MCP clients) are public; a token request needs
        // a code or refresh token of 256 bits, so they get the generous session limit.
        if (request.Path.StartsWithSegments("/" + ApiRoutes.OAuth, StringComparison.OrdinalIgnoreCase))
        {
            return request.Path.Equals("/" + ApiRoutes.OAuthToken, StringComparison.OrdinalIgnoreCase) ||
                   request.Path.Equals("/" + ApiRoutes.OAuthRegister, StringComparison.OrdinalIgnoreCase)
                ? RateLimitCategory.Session
                : RateLimitCategory.None;
        }

        // Public first-run setup endpoints (TASK-144) share the credential limit.
        if (request.Path.StartsWithSegments("/" + ApiRoutes.Setup, StringComparison.OrdinalIgnoreCase))
        {
            return RateLimitCategory.Credential;
        }

        if (!request.Path.StartsWithSegments("/" + ApiRoutes.Auth, StringComparison.OrdinalIgnoreCase)
            || string.Equals(request.Path.Value, "/" + ApiRoutes.Auth, StringComparison.OrdinalIgnoreCase))
        {
            return RateLimitCategory.None;
        }

        foreach (var route in SessionRoutes)
        {
            if (string.Equals(request.Path.Value?.TrimEnd('/'), route, StringComparison.OrdinalIgnoreCase))
            {
                return RateLimitCategory.Session;
            }
        }

        return RateLimitCategory.Credential;
    }
}

public sealed class AuthRateLimiter
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly object _gate = new();
    private readonly Dictionary<string, AuthRateLimitBucket> _buckets = new(StringComparer.Ordinal);
    private DateTimeOffset _nextSweepAt = DateTimeOffset.MinValue;

    /// <summary>
    /// Number of buckets currently held in memory. Expired buckets are swept lazily on
    /// access, at most once per window, so this stays bounded by the number of distinct
    /// keys seen in roughly the last two windows.
    /// </summary>
    public int ActiveBucketCount
    {
        get
        {
            lock (_gate)
            {
                return _buckets.Count;
            }
        }
    }

    public AuthRateLimitResult CheckRequest(string key, int limitPerMinute, DateTimeOffset now)
    {
        lock (_gate)
        {
            EvictExpiredBuckets(now);

            if (!_buckets.TryGetValue(key, out var bucket) || now >= bucket.WindowStartedAt.Add(Window))
            {
                bucket = new AuthRateLimitBucket(now, 0);
                _buckets[key] = bucket;
            }

            if (bucket.Count >= limitPerMinute)
            {
                var retryAfter = bucket.WindowStartedAt.Add(Window) - now;
                return new AuthRateLimitResult(
                    Allowed: false,
                    RetryAfterSeconds: Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)));
            }

            bucket.Count++;
            return new AuthRateLimitResult(Allowed: true, RetryAfterSeconds: 0);
        }
    }

    // Caller holds _gate.
    private void EvictExpiredBuckets(DateTimeOffset now)
    {
        if (now < _nextSweepAt)
        {
            return;
        }

        var expiredKeys = new List<string>();
        foreach (var pair in _buckets)
        {
            if (now >= pair.Value.WindowStartedAt.Add(Window))
            {
                expiredKeys.Add(pair.Key);
            }
        }

        foreach (var key in expiredKeys)
        {
            _buckets.Remove(key);
        }

        _nextSweepAt = now.Add(Window);
    }

    private sealed class AuthRateLimitBucket
    {
        public AuthRateLimitBucket(DateTimeOffset windowStartedAt, int count)
        {
            WindowStartedAt = windowStartedAt;
            Count = count;
        }

        public DateTimeOffset WindowStartedAt { get; }

        public int Count { get; set; }
    }
}

public sealed record AuthRateLimitResult(bool Allowed, int RetryAfterSeconds);
