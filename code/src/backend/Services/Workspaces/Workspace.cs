namespace Bento.Api.Services.Workspaces;

/// <summary>
/// A separate area of one installation (Step 5.5): its own tables (a Postgres
/// schema or a SQLite file), model, dashboards, forms, functions, imports,
/// rules, apps and settings. The workspace with id 1 is "default": the data
/// that existed before workspaces, in <c>public</c> / <c>data.db</c>.
/// </summary>
public sealed record Workspace(long Id, string Slug, string Name)
{
    public const long DefaultId = 1;
    public const string DefaultSlug = "default";

    public static readonly Workspace Default = new(DefaultId, DefaultSlug, "Default");

    public bool IsDefault => Id == DefaultId;

    /// <summary>The Postgres schema of its tables: <c>public</c> for the default one, <c>ws_&lt;slug&gt;</c> for others.</summary>
    public string DataSchema => IsDefault ? "public" : "ws_" + Slug.Replace('-', '_');

    /// <summary>The SQLite file of its tables: <c>data.db</c> for the default one, <c>data-&lt;slug&gt;.db</c> for others.</summary>
    public string DataFile => IsDefault ? "data.db" : $"data-{Slug}.db";
}

/// <summary>
/// The workspace the current request or background job works in. Set once
/// per request (WorkspaceMiddleware), per calendar device (DAV) and per
/// workspace in each scheduler pass; everything below — data connections,
/// the model, caches, system objects — reads it. Without one: default.
/// </summary>
public static class WorkspaceContext
{
    private static readonly AsyncLocal<Workspace?> CurrentValue = new();

    public static Workspace Current => CurrentValue.Value ?? Workspace.Default;

    public static long Id => Current.Id;

    /// <summary>Works in <paramref name="workspace"/> until disposed (restores the one before).</summary>
    public static IDisposable Use(Workspace workspace)
    {
        var previous = CurrentValue.Value;
        CurrentValue.Value = workspace;
        return new Restore(previous);
    }

    private sealed class Restore(Workspace? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            CurrentValue.Value = previous;
        }
    }
}
