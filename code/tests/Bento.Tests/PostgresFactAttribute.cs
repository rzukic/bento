namespace Bento.Tests;

[AttributeUsage(AttributeTargets.Method)]
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (!PostgresTestSettings.IsConfigured)
        {
            Skip = $"Set {PostgresTestSettings.AdminConnectionEnvironmentVariable} to enable Postgres-backed integration tests.";
        }
    }
}

/// <summary>
/// Every Postgres-backed test class joins this collection: they share the
/// cluster-wide test roles (created and altered per test database), which
/// fail with "tuple concurrently updated" when two classes run in parallel.
/// </summary>
[CollectionDefinition(Name)]
public sealed class PostgresCollection
{
    public const string Name = "Postgres";
}
