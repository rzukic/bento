namespace Bento.Api.Db.Schema;

public class SchemaAutoMigrationService : IHostedService
{
    private readonly SchemaRuntimeOptions _options;
    private readonly SchemaService _schemaService;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<SchemaAutoMigrationService> _logger;

    public SchemaAutoMigrationService(
        SchemaRuntimeOptions options,
        SchemaService schemaService,
        IHostEnvironment environment,
        ILogger<SchemaAutoMigrationService> logger)
    {
        _options = options;
        _schemaService = schemaService;
        _environment = environment;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.AutoMigrateOnStartup)
            return;

        if (string.IsNullOrWhiteSpace(_options.ModelJsonPath))
        {
            _logger.LogWarning("Schema auto-migrate is enabled, but no schema file path is configured.");
            return;
        }

        var schemaPath = ResolvePath(_options.ModelJsonPath);
        if (!File.Exists(schemaPath))
        {
            _logger.LogWarning("Schema auto-migrate is enabled, but the schema file does not exist: {SchemaPath}", schemaPath);
            return;
        }

        try
        {
            var json = await File.ReadAllTextAsync(schemaPath, cancellationToken);
            var requestedModel = ModelJsonSerializer.Parse(json);
            var result = await _schemaService.ApplySchemaAsync(requestedModel, confirmDestructive: false);

            if (result.Errors.Count > 0)
            {
                _logger.LogError(
                    "Schema auto-migrate validation failed for {SchemaPath}: {ErrorCodes}",
                    schemaPath,
                    string.Join(", ", result.Errors.Select(e => e.Code)));
                return;
            }

            if (result.RequiresConfirmation)
            {
                _logger.LogWarning(
                    "Schema auto-migrate skipped because the pending migration is destructive and requires explicit confirmation. File: {SchemaPath}",
                    schemaPath);
                return;
            }

            if (result.Applied && result.Migration is not null)
            {
                _logger.LogInformation(
                    "Schema auto-migrate applied successfully from version {VersionFrom} to {VersionTo}.",
                    result.Migration.VersionFrom,
                    result.Migration.VersionTo);
                return;
            }

            _logger.LogInformation("Schema auto-migrate found no pending changes.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Schema auto-migrate failed while processing {SchemaPath}.", schemaPath);
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private string ResolvePath(string path) =>
        Path.IsPathRooted(path)
            ? path
            : Path.GetFullPath(Path.Combine(_environment.ContentRootPath, path));
}
