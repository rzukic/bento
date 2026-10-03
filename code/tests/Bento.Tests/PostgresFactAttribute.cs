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
