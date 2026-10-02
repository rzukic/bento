using System.Text.Json.Serialization;

namespace Repere.Api.Models;

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
    [property: JsonPropertyName("version")] string Version);
