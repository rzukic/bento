namespace Repere.Api.Services;

/// <summary>
/// Loads the schema cache on startup, after database bootstrap and auto-migration.
/// Registered as a hosted service so it runs during application startup.
/// </summary>
public sealed class SchemaCacheInitializer : IHostedService
{
    private readonly SchemaCache _schemaCache;

    public SchemaCacheInitializer(SchemaCache schemaCache)
    {
        _schemaCache = schemaCache;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _schemaCache.ReloadAsync();
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
