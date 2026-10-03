using Bento.Api.Db.Schema;

namespace Bento.Api.Services;

/// <summary>
/// Holds the current Model JSON in memory for fast table lookups.
/// Reloaded after schema changes so new/removed tables are visible immediately.
/// </summary>
public sealed class SchemaCache
{
    private readonly ModelJsonStore _store;
    private readonly ILogger<SchemaCache> _logger;
    private readonly object _lock = new();

    private volatile ModelJson _current = new() { Version = 0 };
    private readonly List<Action<ModelJson>> _listeners = [];

    public SchemaCache(ModelJsonStore store, ILogger<SchemaCache> logger)
    {
        _store = store;
        _logger = logger;
    }

    public ModelJson Current => _current;

    public TableDef? GetTable(string tableName)
    {
        return _current.Tables.FirstOrDefault(
            t => string.Equals(t.Name, tableName, StringComparison.OrdinalIgnoreCase));
    }

    public bool HasTable(string tableName)
    {
        return GetTable(tableName) is not null;
    }

    public IReadOnlyList<string> GetTableNames()
    {
        return _current.Tables.Select(t => t.Name).ToList();
    }

    /// <summary>
    /// Reload the schema from the store. Called at startup and after schema changes.
    /// </summary>
    public async Task ReloadAsync()
    {
        var model = await _store.GetCurrentAsync() ?? new ModelJson { Version = 0 };

        _current = model;
        _logger.LogInformation(
            "Schema cache reloaded: version {Version}, {TableCount} tables.",
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
