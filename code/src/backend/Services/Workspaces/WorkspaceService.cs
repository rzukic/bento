using System.Globalization;
using System.Text.RegularExpressions;
using Bento.Api.Db;
using Bento.Api.Exceptions;
using Dapper;
using Npgsql;

namespace Bento.Api.Services.Workspaces;

/// <summary>
/// The workspaces of the installation (Step 5.5): listed for everyone, made,
/// renamed and deleted by admins. A new workspace gets its own data store — a
/// Postgres schema <c>ws_&lt;slug&gt;</c> the data role may use, or a SQLite
/// file — and an empty model; deleting one drops its store and everything
/// that belonged to it.
/// </summary>
public sealed partial class WorkspaceService
{
    /// <summary>System tables whose rows belong to one workspace (<c>workspace_id</c>).</summary>
    public static readonly string[] ScopedTables =
    [
        "schema_versions", "migrations", "dashboards", "dashboard_versions", "system_views", "user_table_explorer_preferences",
        "import_profiles", "saved_queries", "functions", "function_versions", "forms", "price_sources", "price_source_runs",
        "notification_rules", "import_sources", "import_runs", "category_rules", "apps", "app_events", "problem_log", "mcp_calls", "share_links"
    ];

    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal) { "default", "api", "admin", "new", "w", "mcp", "dav", "public", "system" };

    private readonly ISystemPool _systemPool;
    private readonly DatabaseConfig _dbConfig;
    private readonly ILogger<WorkspaceService>? _logger;
    private volatile IReadOnlyList<Workspace> _all = [Workspace.Default];

    public WorkspaceService(ISystemPool systemPool, DatabaseConfig dbConfig, ILogger<WorkspaceService>? logger = null)
    {
        _systemPool = systemPool;
        _dbConfig = dbConfig;
        _logger = logger;
    }

    private bool Pg => _systemPool.Provider == DatabaseProvider.Postgres;
    private string Table => Pg ? "_system.workspaces" : "workspaces";
    private string Prefix => Pg ? "_system." : "";

    /// <summary>Every workspace, as last read: the default one first.</summary>
    public IReadOnlyList<Workspace> All => _all;

    public Workspace? Find(string? slug) =>
        slug is null ? null : _all.FirstOrDefault(w => string.Equals(w.Slug, slug.Trim(), StringComparison.OrdinalIgnoreCase));

    public Workspace? Find(long id) => _all.FirstOrDefault(w => w.Id == id);

    public async Task<IReadOnlyList<Workspace>> ReloadAsync()
    {
        using var conn = await _systemPool.GetConnectionAsync();
        var rows = (await conn.QueryAsync<(long Id, string Slug, string Name)>($"SELECT id, slug, name FROM {Table} ORDER BY id"))
            .Select(r => new Workspace(r.Id, r.Slug, r.Name)).ToList();
        if (rows.All(w => w.Id != Workspace.DefaultId))
            rows.Insert(0, Workspace.Default);
        _all = rows;
        return rows;
    }

    public async Task<Workspace> CreateAsync(string? name, string? slug)
    {
        var trimmed = RequireName(name);
        var key = (string.IsNullOrWhiteSpace(slug) ? SlugOf(trimmed) : slug.Trim()).ToLowerInvariant();
        if (!SlugPattern().IsMatch(key) || Reserved.Contains(key))
            throw Invalid("slug", "The short name is 2–40 lower-case letters, digits and dashes, starting with a letter (not default, api, admin …).");
        await ReloadAsync();
        if (Find(key) is not null)
            throw Invalid("slug", $"There is a workspace “{key}” already.");

        var workspace = new Workspace(0, key, trimmed);
        // The store first: a workspace whose tables cannot be made is not listed.
        await CreateStoreAsync(workspace);
        using (var conn = await _systemPool.GetConnectionAsync())
        {
            var id = await conn.ExecuteScalarAsync<long>(
                $"INSERT INTO {Table} (slug, name, created_at) VALUES (@Slug, @Name, {SystemSqlDialect.TimestampParam(_systemPool.Provider, "@Now")}) RETURNING id",
                new { Slug = key, Name = trimmed, Now = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) });
            workspace = workspace with { Id = id };
        }
        await ReloadAsync();
        _logger?.LogInformation("Workspace {Slug} created.", key);
        return workspace;
    }

    public async Task<Workspace> RenameAsync(long id, string? name)
    {
        var workspace = Find(id) ?? (await ReloadAsync()).FirstOrDefault(w => w.Id == id) ?? throw new ApiNotFoundException($"Workspace {id} not found.");
        var trimmed = RequireName(name);
        using (var conn = await _systemPool.GetConnectionAsync())
            await conn.ExecuteAsync($"UPDATE {Table} SET name = @Name WHERE id = @Id", new { Id = id, Name = trimmed });
        await ReloadAsync();
        return workspace with { Name = trimmed };
    }

    /// <summary>Deletes a workspace with its tables and everything in it; <paramref name="confirm"/> must be its short name.</summary>
    public async Task DeleteAsync(long id, string? confirm)
    {
        var workspace = (await ReloadAsync()).FirstOrDefault(w => w.Id == id) ?? throw new ApiNotFoundException($"Workspace {id} not found.");
        if (workspace.IsDefault)
            throw Invalid("id", "The default workspace stays.");
        if (!string.Equals(confirm?.Trim(), workspace.Slug, StringComparison.Ordinal))
            throw Invalid("confirm", $"Type “{workspace.Slug}” to delete the workspace with all its tables, dashboards and settings.");

        // All or nothing: a workspace is never left half deleted.
        using (var conn = await _systemPool.GetConnectionAsync())
        {
            if (conn.State != System.Data.ConnectionState.Open)
                conn.Open();
            using var transaction = conn.BeginTransaction();
            var apps = (await conn.QueryAsync<long>($"SELECT id FROM {Prefix}apps WHERE workspace_id = @Id", new { Id = id }, transaction)).ToArray();
            // Postgres takes the list as an array (Dapper does not expand IN @list for it).
            if (apps.Length > 0)
                await conn.ExecuteAsync($"DELETE FROM {Prefix}app_passwords WHERE {(Pg ? "app_id = ANY(@Apps)" : "app_id IN @Apps")}", new { Apps = apps }, transaction);
            foreach (var table in ScopedTables)
                await conn.ExecuteAsync($"DELETE FROM {Prefix}{table} WHERE workspace_id = @Id", new { Id = id }, transaction);
            await conn.ExecuteAsync($"DELETE FROM {Prefix}workspace_members WHERE workspace_id = @Id", new { Id = id }, transaction);
            await conn.ExecuteAsync($"DELETE FROM {Prefix}workspace_settings WHERE key LIKE @Suffix", new { Suffix = "%@" + id.ToString(CultureInfo.InvariantCulture) }, transaction);
            if (Pg)
                await conn.ExecuteAsync($"DROP SCHEMA IF EXISTS {Q(workspace.DataSchema)} CASCADE", transaction: transaction);
            await conn.ExecuteAsync($"DELETE FROM {Table} WHERE id = @Id", new { Id = id }, transaction);
            transaction.Commit();
        }
        if (!Pg)
        {
            SqliteConnectionCleanup();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                var path = Path.Combine(_dbConfig.DataConnection, workspace.DataFile + suffix);
                if (File.Exists(path))
                    File.Delete(path);
            }
        }
        await ReloadAsync();
        _logger?.LogInformation("Workspace {Slug} deleted.", workspace.Slug);
    }

    /// <summary>
    /// A Postgres schema owned by the system role (it runs the migrations), which
    /// the data role may read and write — tables and sequences made later
    /// included. A SQLite workspace's file is made on first use.
    /// </summary>
    private async Task CreateStoreAsync(Workspace workspace)
    {
        if (!Pg)
            return;
        var schema = Q(workspace.DataSchema);
        var dataRole = new NpgsqlConnectionStringBuilder(_dbConfig.DataConnection).Username;
        using var conn = await _systemPool.GetConnectionAsync();
        var systemRole = await conn.ExecuteScalarAsync<string>("SELECT current_user");
        try
        {
            await conn.ExecuteAsync($"CREATE SCHEMA IF NOT EXISTS {schema}");
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        {
            throw new ApiUnprocessableEntityException(
                $"The database role {systemRole} may not create schemas. Grant it: GRANT CREATE ON DATABASE <database> TO {systemRole}; (the Docker setup does this on start).");
        }
        if (!string.IsNullOrEmpty(dataRole) && !string.Equals(dataRole, systemRole, StringComparison.Ordinal))
        {
            var role = Q(dataRole);
            await conn.ExecuteAsync($"""
                GRANT USAGE ON SCHEMA {schema} TO {role};
                ALTER DEFAULT PRIVILEGES IN SCHEMA {schema} GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO {role};
                ALTER DEFAULT PRIVILEGES IN SCHEMA {schema} GRANT USAGE, SELECT ON SEQUENCES TO {role};
                """);
        }
    }

    /// <summary>Closes pooled SQLite connections so a deleted workspace's file can go.</summary>
    private static void SqliteConnectionCleanup() => Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

    /// <summary>"Kalender & Betrieb" → "kalender-betrieb".</summary>
    public static string SlugOf(string name)
    {
        var normalized = name.ToLowerInvariant().Replace("ä", "ae").Replace("ö", "oe").Replace("ü", "ue").Replace("ß", "ss");
        var slug = NotSlug().Replace(normalized, "-").Trim('-');
        if (slug.Length > 40)
            slug = slug[..40].TrimEnd('-');
        return slug.Length >= 2 && char.IsLetter(slug[0]) ? slug : "ws-" + slug;
    }

    private static string RequireName(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            throw Invalid("name", "Give the workspace a name, e.g. Finances.");
        if (trimmed.Length > 100)
            throw Invalid("name", "The name is too long.");
        return trimmed;
    }

    private static string Q(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    private static ApiValidationException Invalid(string field, string message) =>
        new(message, new Dictionary<string, object?> { ["field"] = field });

    [GeneratedRegex("^[a-z][a-z0-9-]{1,39}$")]
    private static partial Regex SlugPattern();

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NotSlug();
}
