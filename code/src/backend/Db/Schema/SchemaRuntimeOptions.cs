namespace Bento.Api.Db.Schema;

public sealed class SchemaRuntimeOptions
{
    public bool AutoMigrateOnStartup { get; set; }
    public string? ModelJsonPath { get; set; }
}
