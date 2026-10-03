using System.Text.Json;
using Bento.Api.Db.Schema;
using Bento.Api.Exceptions;

namespace Bento.Api.Services;

public sealed record SampleDataLoadResult(
    int SchemaVersion,
    IReadOnlyList<string> TablesCreated,
    IReadOnlyDictionary<string, int> RowsInserted);

/// <summary>
/// One-time loader for the demo workspace offered by the setup wizard. It only
/// runs against an empty installation (schema version 0) and goes through the
/// regular schema apply and CRUD paths, so validation, migrations and history
/// behave exactly as they would for user-entered data.
/// </summary>
public static class SampleDataLoader
{
    private const string SchemaResource = "Bento.Api.SampleData.schema.json";
    private const string RowsResource = "Bento.Api.SampleData.rows.json";

    /// <summary>True while the embedded demo workspace can still be loaded.</summary>
    public static bool IsAvailable(int currentSchemaVersion) => currentSchemaVersion == 0;

    public static async Task<SampleDataLoadResult> LoadAsync(
        ModelJsonStore modelJsonStore,
        SchemaService schemaService,
        SchemaCache schemaCache,
        CrudService crudService,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var currentModel = await modelJsonStore.GetCurrentAsync();
        var currentVersion = currentModel?.Version ?? 0;
        if (!IsAvailable(currentVersion) || schemaCache.GetTableNames().Count > 0)
        {
            throw new ApiConflictException(
                "Sample data can only be loaded into an empty installation.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["schemaVersion"] = currentVersion,
                    ["tables"] = schemaCache.GetTableNames()
                });
        }

        var schemaModel = ModelJsonSerializer.Parse(ReadResource(SchemaResource));
        var applyResult = await schemaService.ApplySchemaAsync(schemaModel);
        if (!applyResult.Applied)
        {
            var errors = string.Join("; ", applyResult.Errors.Select(e => $"{e.Code}: {e.Message}"));
            throw new InvalidOperationException($"Embedded sample schema could not be applied: {errors}");
        }

        await schemaCache.ReloadAsync();

        using var rowsDocument = JsonDocument.Parse(ReadResource(RowsResource));
        var rowsInserted = new Dictionary<string, int>(StringComparer.Ordinal);

        // Insertion order follows the document: parents before children, so the
        // reference ids in the fixture (1..n) line up with the generated ids.
        foreach (var tableRows in rowsDocument.RootElement.EnumerateObject())
        {
            var table = schemaCache.GetTable(tableRows.Name)
                ?? throw new InvalidOperationException($"Sample rows reference unknown table '{tableRows.Name}'.");

            var count = 0;
            foreach (var row in tableRows.Value.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                await crudService.CreateAsync(table, row);
                count++;
            }

            rowsInserted[tableRows.Name] = count;
        }

        return new SampleDataLoadResult(
            SchemaVersion: applyResult.TargetModel.Version,
            TablesCreated: schemaModel.Tables.Select(t => t.Name).ToList(),
            RowsInserted: rowsInserted);
    }

    private static string ReadResource(string logicalName)
    {
        using var stream = typeof(SampleDataLoader).Assembly.GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException($"Embedded resource '{logicalName}' is missing from the backend assembly.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
