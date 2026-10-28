using System.Globalization;
using Dapper;
using Bento.Api.Db;

namespace Bento.Api.Services.Mcp;

/// <summary>
/// Every MCP tool call with who made it, through which credential, its
/// arguments (shortened), the outcome and the time it took. Kept 90 days.
/// </summary>
public sealed class McpCallLog
{
    private const int MaxArgumentsLength = 2000;
    private static readonly TimeSpan Retention = TimeSpan.FromDays(90);
    private static readonly TimeSpan PruneInterval = TimeSpan.FromHours(1);

    private readonly ISystemPool _systemPool;
    private DateTime _lastPrune = DateTime.MinValue;

    public McpCallLog(ISystemPool systemPool)
    {
        _systemPool = systemPool;
    }

    public sealed record Entry(long? UserId, string Via, string? Credential, string Tool, string? Arguments, bool Ok, string? Error, int DurationMs, long? GrantId = null);

    public sealed class CallRow
    {
        public long Id { get; init; }
        public string CalledAt { get; init; } = "";
        public long? UserId { get; init; }
        public string? Username { get; init; }
        public string Via { get; init; } = "";
        public string? Credential { get; init; }
        public string Tool { get; init; } = "";
        public string? Arguments { get; init; }
        public bool Ok { get; init; }
        public string? Error { get; init; }
        public int DurationMs { get; init; }
    }

    public async Task WriteAsync(Entry entry)
    {
        var now = DateTime.UtcNow;
        var arguments = entry.Arguments is { Length: > MaxArgumentsLength } longArguments ? longArguments[..MaxArgumentsLength] + "…" : entry.Arguments;
        var error = entry.Error is { Length: > MaxArgumentsLength } longError ? longError[..MaxArgumentsLength] + "…" : entry.Error;

        using var conn = await _systemPool.GetConnectionAsync();
        await conn.ExecuteAsync(
            $"""
            INSERT INTO {Table} (workspace_id, called_at, user_id, via, credential, tool, arguments, ok, error, duration_ms, grant_id)
            VALUES (@Ws, {Ts("@Now")}, @UserId, @Via, @Credential, @Tool, @Arguments, @Ok, @Error, @DurationMs, @GrantId)
            """,
            new { Now = Iso(now), entry.UserId, entry.Via, entry.Credential, entry.Tool, Arguments = arguments, entry.Ok, Error = error, entry.DurationMs, entry.GrantId, Ws = Workspaces.WorkspaceContext.Id });

        if (now - _lastPrune >= PruneInterval)
        {
            _lastPrune = now;
            await conn.ExecuteAsync($"DELETE FROM {Table} WHERE called_at < {Ts("@Cutoff")}", new { Cutoff = Iso(now - Retention) });
        }
    }

    /// <summary>
    /// The calls of one connected app. Calls logged before the connection was
    /// recorded with each call count when app name, user and time fit.
    /// </summary>
    public async Task<IReadOnlyList<CallRow>> ListForGrantAsync(long grantId, long grantUserId, string clientName, string grantCreatedAt, int limit)
    {
        using var conn = await _systemPool.GetConnectionAsync();
        var rows = await conn.QueryAsync<CallRow>(
            $"""
            SELECT c.id, {SystemSqlDialect.TimestampText(_systemPool.Provider, "c.called_at")} AS CalledAt, c.user_id AS UserId, u.username,
                   c.via, c.credential, c.tool, c.arguments, c.ok, c.error, c.duration_ms AS DurationMs
            FROM {Table} c
            LEFT JOIN {Users} u ON u.id = c.user_id
            WHERE c.grant_id = @GrantId
               OR (c.grant_id IS NULL AND c.via = 'oauth' AND c.credential = @ClientName AND c.user_id = @UserId AND c.called_at >= {Ts("@Since")})
            ORDER BY c.called_at DESC, c.id DESC
            LIMIT @Limit
            """,
            new { GrantId = grantId, ClientName = clientName, UserId = grantUserId, Since = grantCreatedAt, Limit = Math.Clamp(limit, 1, 500) });
        return rows.ToList();
    }

    /// <summary>The newest calls: the user's own, or everyone's for an admin.</summary>
    public async Task<IReadOnlyList<CallRow>> ListAsync(long? userId, int limit)
    {
        using var conn = await _systemPool.GetConnectionAsync();
        var rows = await conn.QueryAsync<CallRow>(
            $"""
            SELECT c.id, {SystemSqlDialect.TimestampText(_systemPool.Provider, "c.called_at")} AS CalledAt, c.user_id AS UserId, u.username,
                   c.via, c.credential, c.tool, c.arguments, c.ok, c.error, c.duration_ms AS DurationMs
            FROM {Table} c
            LEFT JOIN {Users} u ON u.id = c.user_id
            {(userId is null ? "" : "WHERE c.user_id = @UserId")}
            ORDER BY c.called_at DESC, c.id DESC
            LIMIT @Limit
            """,
            new { UserId = userId, Limit = Math.Clamp(limit, 1, 500) });
        return rows.ToList();
    }

    private static string Iso(DateTime value) => value.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    private string Ts(string parameter) => SystemSqlDialect.TimestampParam(_systemPool.Provider, parameter);
    private string Table => _systemPool.Provider == DatabaseProvider.Postgres ? "_system.mcp_calls" : "mcp_calls";
    private string Users => _systemPool.Provider == DatabaseProvider.Postgres ? "_system.users" : "users";
}
