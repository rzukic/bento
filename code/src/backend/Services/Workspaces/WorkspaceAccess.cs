using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Bento.Api.Db;
using Bento.Api.Exceptions;
using Dapper;

namespace Bento.Api.Services.Workspaces;

/// <summary>
/// Who may open which workspace (Step 5.5 part B). Installation admins
/// (<c>users.role = admin</c>) are admins in every workspace; everyone else
/// opens the workspaces they are a member of, with the role they have there
/// (admin, editor, viewer or api). Kept in memory once read; every change goes
/// through here.
/// </summary>
public sealed class WorkspaceAccess
{
    public static readonly string[] Roles = ["admin", "editor", "viewer", "api"];

    private readonly ISystemPool _systemPool;
    private readonly ConcurrentDictionary<long, IReadOnlyDictionary<long, string>> _byUser = new();

    public WorkspaceAccess(ISystemPool systemPool)
    {
        _systemPool = systemPool;
    }

    private bool Pg => _systemPool.Provider == DatabaseProvider.Postgres;
    private string Members => Pg ? "_system.workspace_members" : "workspace_members";
    private string Users => Pg ? "_system.users" : "users";

    public sealed record Member(long UserId, string Username, string Role, string InstallationRole);

    /// <summary>The user's role in the workspace, or null when they may not open it.</summary>
    public async Task<string?> RoleInAsync(long userId, string? installationRole, long workspaceId)
    {
        if (installationRole == "admin")
            return "admin";
        return (await RolesOfAsync(userId)).GetValueOrDefault(workspaceId);
    }

    /// <summary>The workspaces a user is a member of, with their role in each (installation admins: none needed).</summary>
    public async Task<IReadOnlyDictionary<long, string>> RolesOfAsync(long userId)
    {
        if (_byUser.TryGetValue(userId, out var known))
            return known;
        using var conn = await _systemPool.GetConnectionAsync();
        var rows = await conn.QueryAsync<MemberRow>(
            $"SELECT workspace_id AS WorkspaceId, user_id AS UserId, role FROM {Members} WHERE user_id = @UserId", new { UserId = userId });
        var roles = rows.ToDictionary(r => r.WorkspaceId, r => r.Role);
        _byUser[userId] = roles;
        return roles;
    }

    public async Task<IReadOnlyList<Member>> MembersAsync(long workspaceId)
    {
        using var conn = await _systemPool.GetConnectionAsync();
        var rows = await conn.QueryAsync<MemberRow>(
            $"""
            SELECT m.workspace_id AS WorkspaceId, u.id AS UserId, u.username, m.role, u.role AS InstallationRole
            FROM {Members} m INNER JOIN {Users} u ON u.id = m.user_id
            WHERE m.workspace_id = @Ws ORDER BY LOWER(u.username)
            """, new { Ws = workspaceId });
        return rows.Select(r => new Member(r.UserId, r.Username, r.Role, r.InstallationRole)).ToList();
    }

    // Postgres hands ids back as int, SQLite as long: a class maps both.
    private sealed class MemberRow
    {
        public long WorkspaceId { get; init; }
        public long UserId { get; init; }
        public string Username { get; init; } = "";
        public string Role { get; init; } = "";
        public string InstallationRole { get; init; } = "";
    }

    /// <summary>Adds the user to the workspace, or changes their role there.</summary>
    public async Task SetMemberAsync(long workspaceId, long userId, string? role)
    {
        var normalized = NormalizeRole(role);
        using var conn = await _systemPool.GetConnectionAsync();
        if (await conn.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {Users} WHERE id = @Id", new { Id = userId }) == 0)
            throw new ApiNotFoundException($"User {userId} not found.");
        await conn.ExecuteAsync($"DELETE FROM {Members} WHERE workspace_id = @Ws AND user_id = @UserId", new { Ws = workspaceId, UserId = userId });
        await conn.ExecuteAsync(
            $"INSERT INTO {Members} (workspace_id, user_id, role, created_at) VALUES (@Ws, @UserId, @Role, {SystemSqlDialect.TimestampParam(_systemPool.Provider, "@Now")})",
            new { Ws = workspaceId, UserId = userId, Role = normalized, Now = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) });
        _byUser.TryRemove(userId, out _);
    }

    public async Task RemoveMemberAsync(long workspaceId, long userId)
    {
        using var conn = await _systemPool.GetConnectionAsync();
        await conn.ExecuteAsync($"DELETE FROM {Members} WHERE workspace_id = @Ws AND user_id = @UserId", new { Ws = workspaceId, UserId = userId });
        _byUser.TryRemove(userId, out _);
    }

    /// <summary>Forgets what was read (a workspace or user went away).</summary>
    public void Invalidate() => _byUser.Clear();

    public static string NormalizeRole(string? role)
    {
        var normalized = role?.Trim().ToLowerInvariant();
        return normalized is not null && Roles.Contains(normalized)
            ? normalized
            : throw new ApiValidationException("The role is admin, editor, viewer or api.", new Dictionary<string, object?> { ["field"] = "role" });
    }

    /// <summary>The highest of the roles, for what a person may tick on the consent page.</summary>
    public static string? Highest(IEnumerable<string?> roles)
    {
        string? best = null;
        foreach (var role in roles)
        {
            if (Rank(role) > Rank(best))
                best = role;
        }
        return best;
    }

    private static int Rank(string? role) => role switch { "admin" => 4, "editor" => 3, "viewer" => 2, "api" => 1, _ => 0 };

    // ── The workspaces a key or a connection may use ─────────────────────

    /// <summary>Stored as a JSON list of workspace ids; null: every workspace.</summary>
    public static IReadOnlyList<long>? ReadWorkspaces(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<long[]>(json) ?? [];
        }
        catch (JsonException)
        {
            return [Workspace.DefaultId];
        }
    }

    public static string? WriteWorkspaces(IReadOnlyCollection<long>? workspaces) =>
        workspaces is null ? null : JsonSerializer.Serialize(workspaces.Distinct().Order().ToArray());

    public static bool Allows(IReadOnlyList<long>? workspaces, long workspaceId) => workspaces is null || workspaces.Contains(workspaceId);

    // ── Paths that belong to the installation, not to one workspace ──────

    private static readonly string[] InstallationAreas =
    [
        "/auth", "/workspaces", "/oauth", "/mcp", "/admin/users", "/admin/api-keys", "/admin/credentials",
        "/admin/notifications/channels", "/admin/problems", "/admin/health", "/ai/settings", "/ai/usage", "/ai/status"
    ];

    /// <summary>
    /// Users, sign-in, workspaces, keys, connections, the vault, notification
    /// channels, AI settings, problems: one per installation, reached from any
    /// workspace with the installation role. Everything else works inside the
    /// workspace and needs a role there.
    /// </summary>
    public static bool IsInstallationPath(string path)
    {
        var p = path.TrimEnd('/').ToLowerInvariant();
        const string api = "/" + ApiRoutes.Prefix;
        if (!p.StartsWith(api, StringComparison.Ordinal))
            return false;
        var rest = p[api.Length..];
        return InstallationAreas.Any(area => rest == area || rest.StartsWith(area + "/", StringComparison.Ordinal));
    }
}
