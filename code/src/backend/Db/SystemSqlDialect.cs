namespace Bento.Api.Db;

/// <summary>
/// Small provider-specific SQL fragments for the hand-written system-table
/// statements (views, explorer preferences). SQLite stores JSON and timestamps
/// as TEXT, so plain parameters work there; Postgres needs explicit casts to
/// JSONB / TIMESTAMPTZ and an ISO-8601 projection when reading timestamps back
/// into string properties.
/// </summary>
internal static class SystemSqlDialect
{
    /// <summary>Bind a JSON document parameter, e.g. <c>@Config</c>.</summary>
    public static string JsonParam(DatabaseProvider provider, string parameter) =>
        provider == DatabaseProvider.Postgres ? $"CAST({parameter} AS JSONB)" : parameter;

    /// <summary>Bind an ISO-8601 timestamp string parameter, e.g. <c>@Now</c>.</summary>
    public static string TimestampParam(DatabaseProvider provider, string parameter) =>
        provider == DatabaseProvider.Postgres ? $"CAST({parameter} AS TIMESTAMPTZ)" : parameter;

    /// <summary>
    /// Project a timestamp column as an ISO-8601 UTC string so Dapper can map it
    /// onto a <c>string</c> property identically on both providers.
    /// </summary>
    public static string TimestampText(DatabaseProvider provider, string column) =>
        provider == DatabaseProvider.Postgres
            ? $"to_char({column} AT TIME ZONE 'UTC', 'YYYY-MM-DD\"T\"HH24:MI:SS.US\"Z\"')"
            : column;
}
