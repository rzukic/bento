using System.Globalization;
using Repere.Api.Db;

namespace Repere.Api.Configuration;

public static class BackendRuntimeConfiguration
{
    public static IReadOnlyDictionary<string, string?> BuildEnvironmentOverrides(Func<string, string?> getEnvironmentVariable)
    {
        var overrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        SetIfPresent("BACKEND_PORT", "Backend:Port");
        SetIfPresent("AUTH_MODE", "Backend:AuthMode");
        SetIfPresent("JWT_SECRET", "Backend:JwtSecret");
        SetIfPresent("MAX_BODY_SIZE", "Backend:MaxBodySize");
        SetIfPresent("DATABASE_URL", "Backend:DatabaseUrl");
        SetIfPresent("DATABASE_PROVIDER", "Database:Provider");
        SetIfPresent("DATABASE_SYSTEM_CONNECTION", "Database:SystemConnection");
        SetIfPresent("DATABASE_DATA_CONNECTION", "Database:DataConnection");

        var logLevel = getEnvironmentVariable("LOG_LEVEL");
        if (!string.IsNullOrWhiteSpace(logLevel))
        {
            overrides["Backend:LogLevel"] = logLevel;
            overrides["Logging:LogLevel:Default"] = NormalizeLogLevel(logLevel);
        }

        var allowedOrigins = SplitCsv(getEnvironmentVariable("ALLOWED_ORIGINS"));
        for (var i = 0; i < allowedOrigins.Length; i++)
        {
            overrides[$"Backend:AllowedOrigins:{i}"] = allowedOrigins[i];
        }

        var dataDir = getEnvironmentVariable("DATA_DIR");
        if (!string.IsNullOrWhiteSpace(dataDir))
        {
            overrides["Backend:DataDir"] = dataDir;

            var hasExplicitDatabaseUrl = !string.IsNullOrWhiteSpace(getEnvironmentVariable("DATABASE_URL"));
            var hasExplicitSystemConnection = !string.IsNullOrWhiteSpace(getEnvironmentVariable("DATABASE_SYSTEM_CONNECTION"));
            var hasExplicitDataConnection = !string.IsNullOrWhiteSpace(getEnvironmentVariable("DATABASE_DATA_CONNECTION"));

            if (!hasExplicitDatabaseUrl && !hasExplicitSystemConnection && !hasExplicitDataConnection)
            {
                overrides["Database:SystemConnection"] = dataDir;
                overrides["Database:DataConnection"] = dataDir;
            }
        }

        var port = getEnvironmentVariable("BACKEND_PORT");
        if (!string.IsNullOrWhiteSpace(port))
        {
            overrides["urls"] = $"http://0.0.0.0:{port}";
        }

        return overrides;

        void SetIfPresent(string envName, string configKey)
        {
            var value = getEnvironmentVariable(envName);
            if (!string.IsNullOrWhiteSpace(value))
            {
                overrides[configKey] = value;
            }
        }
    }

    public static BackendRuntimeOptions BindAndValidateRuntimeOptions(IConfiguration configuration)
    {
        var options = new BackendRuntimeOptions();
        configuration.GetSection("Backend").Bind(options);
        Validate(options);
        return options;
    }

    public static void ApplyDatabaseOverrides(DatabaseConfig databaseConfig, BackendRuntimeOptions runtimeOptions)
    {
        if (string.IsNullOrWhiteSpace(runtimeOptions.DatabaseUrl))
        {
            ValidateDatabaseConfig(databaseConfig);
            return;
        }

        var databaseUri = ParseDatabaseUrl(runtimeOptions.DatabaseUrl);
        var scheme = databaseUri.Scheme.ToLowerInvariant();

        if (scheme is "postgres" or "postgresql")
        {
            throw new InvalidOperationException(
                "DATABASE_URL currently supports SQLite convenience paths only. " +
                "For Postgres, configure DATABASE_SYSTEM_CONNECTION and DATABASE_DATA_CONNECTION " +
                "to preserve the required dual-pool isolation.");
        }

        if (scheme != "sqlite")
        {
            throw new InvalidOperationException(
                $"Unsupported DATABASE_URL scheme '{databaseUri.Scheme}'. Expected sqlite://...");
        }

        var rootPath = GetSqliteDataRoot(databaseUri);

        databaseConfig.Provider = DatabaseProvider.Sqlite;
        databaseConfig.SystemConnection = rootPath;
        databaseConfig.DataConnection = rootPath;

        ValidateDatabaseConfig(databaseConfig);
    }

    public static string NormalizeLogLevel(string logLevel)
    {
        return logLevel.Trim().ToLowerInvariant() switch
        {
            "trace" => "Trace",
            "debug" => "Debug",
            "info" => "Information",
            "information" => "Information",
            "warn" => "Warning",
            "warning" => "Warning",
            "error" => "Error",
            "critical" => "Critical",
            _ => logLevel.Trim()
        };
    }

    public static long ParseByteSize(string size)
    {
        if (string.IsNullOrWhiteSpace(size))
        {
            throw new InvalidOperationException("MAX_BODY_SIZE must not be empty.");
        }

        var trimmed = size.Trim();
        var splitIndex = 0;
        while (splitIndex < trimmed.Length && (char.IsDigit(trimmed[splitIndex]) || trimmed[splitIndex] == '.'))
        {
            splitIndex++;
        }

        if (splitIndex == 0)
        {
            throw new InvalidOperationException($"MAX_BODY_SIZE value '{size}' is invalid.");
        }

        var numberPart = trimmed[..splitIndex];
        var suffix = trimmed[splitIndex..].Trim().ToLowerInvariant();

        if (!double.TryParse(numberPart, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) || value <= 0)
        {
            throw new InvalidOperationException($"MAX_BODY_SIZE value '{size}' is invalid.");
        }

        var multiplier = suffix switch
        {
            "" or "b" => 1d,
            "kb" => 1024d,
            "mb" => 1024d * 1024d,
            "gb" => 1024d * 1024d * 1024d,
            _ => throw new InvalidOperationException(
                $"MAX_BODY_SIZE value '{size}' uses unsupported size suffix '{suffix}'. Use b, kb, mb, or gb.")
        };

        return checked((long)Math.Round(value * multiplier, MidpointRounding.AwayFromZero));
    }

    public static string[] SplitCsv(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToArray();
    }

    public static void Validate(BackendRuntimeOptions options)
    {
        if (options.Port is < 1 or > 65535)
        {
            throw new InvalidOperationException($"BACKEND_PORT must be between 1 and 65535. Got '{options.Port}'.");
        }

        options.LogLevel = NormalizeLogLevel(options.LogLevel);
        if (options.LogLevel is not ("Trace" or "Debug" or "Information" or "Warning" or "Error" or "Critical"))
        {
            throw new InvalidOperationException(
                $"LOG_LEVEL '{options.LogLevel}' is invalid. Use trace, debug, info, warn, error, or critical.");
        }

        ParseByteSize(options.MaxBodySize);

        options.AuthMode = options.AuthMode.Trim().ToLowerInvariant();
        if (options.AuthMode is not ("open" or "standard" or "vault"))
        {
            throw new InvalidOperationException(
                $"AUTH_MODE '{options.AuthMode}' is invalid. Use open, standard, or vault.");
        }

        if (string.IsNullOrWhiteSpace(options.DataDir))
        {
            throw new InvalidOperationException("DATA_DIR must not be empty.");
        }

        if (!string.IsNullOrWhiteSpace(options.DatabaseUrl))
        {
            _ = ParseDatabaseUrl(options.DatabaseUrl);
        }
    }

    private static void ValidateDatabaseConfig(DatabaseConfig databaseConfig)
    {
        if (string.IsNullOrWhiteSpace(databaseConfig.SystemConnection))
        {
            throw new InvalidOperationException("Database:SystemConnection must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(databaseConfig.DataConnection))
        {
            throw new InvalidOperationException("Database:DataConnection must not be empty.");
        }
    }

    private static Uri ParseDatabaseUrl(string databaseUrl)
    {
        if (!Uri.TryCreate(databaseUrl, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException(
                $"DATABASE_URL '{databaseUrl}' is invalid. Expected an absolute sqlite://... or postgresql://... URL.");
        }

        return uri;
    }

    private static string GetSqliteDataRoot(Uri databaseUri)
    {
        var path = Uri.UnescapeDataString(databaseUri.AbsolutePath);

        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("DATABASE_URL for sqlite must include a path.");
        }

        if (path.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
        {
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new InvalidOperationException(
                    $"DATABASE_URL '{databaseUri}' does not contain a usable SQLite data directory.");
            }

            return directory;
        }

        return path;
    }
}
