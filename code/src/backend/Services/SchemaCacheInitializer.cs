namespace Bento.Api.Services;

/// <summary>
/// Loads the schema cache of every workspace on startup, after database
/// bootstrap and auto-migration. Registered as a hosted service so it runs during application startup.
/// </summary>
public sealed class SchemaCacheInitializer : IHostedService
{
    private readonly SchemaCache _schemaCache;
    private readonly Workspaces.WorkspaceService? _workspaces;

    public SchemaCacheInitializer(SchemaCache schemaCache, Workspaces.WorkspaceService? workspaces = null)
    {
        _schemaCache = schemaCache;
        _workspaces = workspaces;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var all = _workspaces is null ? [Workspaces.WorkspaceContext.Current] : await _workspaces.ReloadAsync();
        foreach (var workspace in all)
        {
            using var _ = Workspaces.WorkspaceContext.Use(workspace);
            await _schemaCache.ReloadAsync();
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
