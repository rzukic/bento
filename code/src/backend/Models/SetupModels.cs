using System.Text.Json.Serialization;

namespace Bento.Api.Models;

/// <summary>
/// Public, secret-free description of the installation state used by the
/// first-run setup wizard. Everything an administrator may need beyond this
/// lives in <c>/admin/health/details</c>.
/// </summary>
public sealed record SetupStatusResponse(
    [property: JsonPropertyName("initialized")] bool Initialized,
    [property: JsonPropertyName("auth_mode")] string AuthMode,
    [property: JsonPropertyName("auth_required")] bool AuthRequired,
    [property: JsonPropertyName("registration_bootstrap_open")] bool RegistrationBootstrapOpen,
    [property: JsonPropertyName("database_provider")] string DatabaseProvider,
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("sample_data_available")] bool SampleDataAvailable,
    [property: JsonPropertyName("version")] string Version,
    // How numbers, dates and amounts are written in this workspace (null: each browser's language).
    [property: JsonPropertyName("locale")] string? Locale = null,
    [property: JsonPropertyName("currency")] string Currency = "EUR",
    // The calendar: the time zone "today" and schedules are in (the server's when not chosen), week and year start.
    [property: JsonPropertyName("time_zone")] string TimeZone = "UTC",
    [property: JsonPropertyName("time_zone_set")] bool TimeZoneSet = false,
    [property: JsonPropertyName("week_start")] string WeekStart = "monday",
    [property: JsonPropertyName("year_start")] int YearStart = 1);

/// <summary>Summary returned after the demo workspace has been loaded.</summary>
public sealed record SampleDataResponse(
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("tables_created")] IReadOnlyList<string> TablesCreated,
    [property: JsonPropertyName("rows_inserted")] IReadOnlyDictionary<string, int> RowsInserted);
