using System.Globalization;
using System.Security.Claims;
using Dapper;
using Bento.Api.Authorization;
using Bento.Api.Db;

namespace Bento.Api.Services;

/// <summary>
/// Problem cases, for admins and for improving Bento during the beta:
/// <list type="bullet">
/// <item><c>slow</c>: a query (widget, variable, MCP aggregate, …) that took longer than
///   <c>Backend:SlowQueryMs</c> (3 s), with the query.</item>
/// <item><c>timeout</c>: the database stopped a query.</item>
/// <item><c>error</c>: an unexpected server error (500), with where it happened.</item>
/// </list>
/// Each entry says who, through which app, and from where in the app
/// (<c>X-Bento-Source</c>, e.g. a dashboard's widget). Kept 90 days, at most 2000.
/// Stays on this server: nothing is sent anywhere.
/// </summary>
public sealed class ProblemLog
{
    public const string SourceHeader = "X-Bento-Source";
    /// <summary>The query a request ran (JSON), for the error entry of that request.</summary>
    public const string QueryItem = "bento.problem.query";
    /// <summary>Set once the request's problem is logged, so it is not logged twice.</summary>
    public const string LoggedItem = "bento.problem.logged";

    private const int MaxText = 8000;
    private const int MaxEntries = 2000;
    private static readonly TimeSpan Retention = TimeSpan.FromDays(90);
    private static readonly TimeSpan PruneInterval = TimeSpan.FromHours(1);

    private readonly ISystemPool _systemPool;
    private readonly IHttpContextAccessor _http;
    private readonly ILogger<ProblemLog> _logger;
    private DateTime _lastPrune = DateTime.MinValue;

    public ProblemLog(ISystemPool systemPool, IHttpContextAccessor http, IConfiguration configuration, ILogger<ProblemLog> logger)
    {
        _systemPool = systemPool;
        _http = http;
        _logger = logger;
        SlowMs = configuration.GetValue("Backend:SlowQueryMs", 3000);
    }

    /// <summary>Queries taking longer than this many milliseconds are logged as slow.</summary>
    public int SlowMs { get; }

    public sealed class ProblemRow
    {
        public long Id { get; init; }
        public string LoggedAt { get; init; } = "";
        public string Kind { get; init; } = "";
        public string? Method { get; init; }
        public string? Path { get; init; }
        public int? Status { get; init; }
        public int DurationMs { get; init; }
        public long? UserId { get; init; }
        public string? Username { get; init; }
        public string? Via { get; init; }
        public string? Source { get; init; }
        public string Message { get; init; } = "";
        public string? Detail { get; init; }
    }

    /// <summary>Logs a problem of the current request; never throws.</summary>
    public async Task RecordAsync(string kind, string message, string? detail, long durationMs, int? status = null)
    {
        var context = _http.HttpContext;
        try
        {
            var user = context?.User;
            var now = DateTime.UtcNow;
            using var conn = await _systemPool.GetConnectionAsync();
            await conn.ExecuteAsync(
                $"""
                INSERT INTO {Table} (workspace_id, logged_at, kind, method, path, status, duration_ms, user_id, via, source, message, detail)
                VALUES (@Ws, {Ts("@Now")}, @Kind, @Method, @Path, @Status, @DurationMs, @UserId, @Via, @Source, @Message, @Detail)
                """,
                new
                {
                    Now = Iso(now),
                    Kind = kind,
                    Method = context?.Request.Method,
                    Path = Cut(context?.Request.Path.Value, 500),
                    Status = status,
                    DurationMs = (int)Math.Min(durationMs, int.MaxValue),
                    UserId = user?.GetUserId(),
                    Via = user is null ? null : Via(user),
                    Source = Cut(Source(context), 500),
                    Message = Cut(message, MaxText) ?? "",
                    Detail = Cut(detail, MaxText),
                    Ws = Workspaces.WorkspaceContext.Id
                });
            if (context is not null)
                context.Items[LoggedItem] = true;

            if (now - _lastPrune >= PruneInterval)
            {
                _lastPrune = now;
                await conn.ExecuteAsync($"DELETE FROM {Table} WHERE logged_at < {Ts("@Cutoff")}", new { Cutoff = Iso(now - Retention) });
                await conn.ExecuteAsync(
                    $"DELETE FROM {Table} WHERE id <= (SELECT id FROM {Table} ORDER BY id DESC LIMIT 1 OFFSET {MaxEntries})");
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "A {Kind} problem could not be logged: {Message}", kind, message);
        }
    }

    public async Task<IReadOnlyList<ProblemRow>> ListAsync(int limit)
    {
        using var conn = await _systemPool.GetConnectionAsync();
        var rows = await conn.QueryAsync<ProblemRow>(
            $"""
            SELECT p.id, {SystemSqlDialect.TimestampText(_systemPool.Provider, "p.logged_at")} AS LoggedAt, p.kind, p.method, p.path, p.status,
                   p.duration_ms AS DurationMs, p.user_id AS UserId, u.username, p.via, p.source, p.message, p.detail
            FROM {Table} p
            LEFT JOIN {Users} u ON u.id = p.user_id
            ORDER BY p.id DESC
            LIMIT @Limit
            """,
            new { Limit = Math.Clamp(limit, 1, 500) });
        return rows.ToList();
    }

    public async Task<int> ClearAsync()
    {
        using var conn = await _systemPool.GetConnectionAsync();
        return await conn.ExecuteAsync($"DELETE FROM {Table}");
    }

    /// <summary>Where in the app the request came from; the browser sends it URI-encoded (titles may have umlauts).</summary>
    private static string? Source(HttpContext? context)
    {
        var raw = context?.Request.Headers[SourceHeader].ToString();
        if (string.IsNullOrEmpty(raw))
            return null;
        try
        {
            return Uri.UnescapeDataString(raw);
        }
        catch (UriFormatException)
        {
            return raw;
        }
    }

    /// <summary>The app or key a request came through, as in the dashboard history.</summary>
    private static string? Via(ClaimsPrincipal user) => user.FindFirstValue("auth_method") switch
    {
        "oauth" => user.FindFirstValue("oauth_client") ?? "MCP client",
        "api_key" => "API key #" + user.FindFirstValue("api_key_id"),
        _ => null
    };

    private static string? Cut(string? text, int max) => text is { Length: > 0 } ? text.Length > max ? text[..max] + "…" : text : null;
    private static string Iso(DateTime value) => value.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    private string Ts(string parameter) => SystemSqlDialect.TimestampParam(_systemPool.Provider, parameter);
    private string Table => _systemPool.Provider == DatabaseProvider.Postgres ? "_system.problem_log" : "problem_log";
    private string Users => _systemPool.Provider == DatabaseProvider.Postgres ? "_system.users" : "users";
}
