using Bento.Api.Db.Schema;

namespace Bento.Api.Services;

/// <summary>
/// Holds each workspace's Model JSON in memory for fast table lookups;
/// <see cref="Current"/> is the model of the current workspace (Step 5.5).
/// Reloaded after schema changes so new/removed tables are visible immediately.
/// </summary>
public sealed class SchemaCache
{
    private readonly ModelJsonStore _store;
    private readonly ILogger<SchemaCache> _logger;
    private readonly object _lock = new();

    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, ModelJson> _models = new();
    private readonly List<Action<ModelJson>> _listeners = [];

    public SchemaCache(ModelJsonStore store, ILogger<SchemaCache> logger)
    {
        _store = store;
        _logger = logger;
    }

    /// <summary>The model of the current workspace; empty until it is loaded.</summary>
    public ModelJson Current => _models.GetValueOrDefault(Workspaces.WorkspaceContext.Id) ?? Empty;

    private static readonly ModelJson Empty = new() { Version = 0 };

    public TableDef? GetTable(string tableName)
    {
        return Current.Tables.FirstOrDefault(
            t => string.Equals(t.Name, tableName, StringComparison.OrdinalIgnoreCase));
    }

    public bool HasTable(string tableName)
    {
        return GetTable(tableName) is not null;
    }

    public IReadOnlyList<string> GetTableNames()
    {
        return Current.Tables.Select(t => t.Name).ToList();
    }

    /// <summary>
    /// Reload the current workspace's schema from the store. Called at startup
    /// (per workspace) and after schema changes.
    /// </summary>
    public async Task ReloadAsync()
    {
        var model = await _store.GetCurrentAsync() ?? new ModelJson { Version = 0 };

        _models[Workspaces.WorkspaceContext.Id] = model;
        _logger.LogInformation(
            "Schema cache reloaded for {Workspace}: version {Version}, {TableCount} tables.",
            Workspaces.WorkspaceContext.Current.Slug,
            model.Version,
            model.Tables.Count);

        NotifyListeners(model);
    }

    /// <summary>
    /// Register a callback for schema change notifications.
    /// </summary>
    public void OnSchemaChanged(Action<ModelJson> listener)
    {
        lock (_lock)
        {
            _listeners.Add(listener);
        }
    }

    private void NotifyListeners(ModelJson model)
    {
        List<Action<ModelJson>> snapshot;
        lock (_lock)
        {
            snapshot = [.. _listeners];
        }

        foreach (var listener in snapshot)
        {
            try
            {
                listener(model);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Schema change listener threw an exception.");
            }
        }
    }
}
