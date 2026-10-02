using System.Globalization;
using System.Data.Common;
using Repere.Api.Db;

namespace Repere.Api.Configuration;

public static class BackendRuntimeConfiguration
{
    public static IReadOnlyDictionary<string, string?> BuildEnvironmentOverrides(Func<string, string?> getEnvironmentVariable)
    {
        var overrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        SetIfPresent("BACKEND_PORT", "Backend:Port");
        SetIfPresent("AUTH_MODE", "Backend:AuthMode");
        SetIfPresent("JWT_SECRET", "Backend:Jwt:Secret");
        SetIfPresent("JWT_ISSUER", "Backend:Jwt:Issuer");
        SetIfPresent("JWT_AUDIENCE", "Backend:Jwt:Audience");
        SetIfPresent("JWT_ACCESS_TOKEN_LIFETIME_MINUTES", "Backend:Jwt:AccessTokenLifetimeMinutes");
        SetIfPresent("JWT_REFRESH_TOKEN_LIFETIME_DAYS", "Backend:Jwt:RefreshTokenLifetimeDays");
        SetIfPresent("AUTH_REFRESH_COOKIE_NAME", "Backend:AuthCookie:RefreshCookieName");
        SetIfPresent("AUTH_REFRESH_COOKIE_SECURE", "Backend:AuthCookie:RequireSecure");
        SetIfPresent("AUTH_REFRESH_COOKIE_SAME_SITE", "Backend:AuthCookie:SameSite");
        SetIfPresent("AUTH_REFRESH_COOKIE_PATH", "Backend:AuthCookie:Path");
        SetIfPresent("AUTH_RATE_LIMIT_PER_MINUTE", "Backend:AuthRateLimitPerMinute");
        SetIfPresent("MAX_BODY_SIZE", "Backend:MaxBodySize");
        SetIfPresent("DATABASE_URL", "Backend:DatabaseUrl");
        SetIfPresent("DATABASE_PROVIDER", "Database:Provider");
        SetIfPresent("DATABASE_SYSTEM_CONNECTION", "Database:SystemConnection");
        SetIfPresent("DATABASE_DATA_CONNECTION", "Database:DataConnection");
        SetIfPresent("POSTGRES_SUPERUSER_PASSWORD", "Backend:PostgresPackaging:SuperuserPassword");

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

        var trustedProxies = SplitCsv(getEnvironmentVariable("TRUSTED_PROXIES"));
        for (var i = 0; i < trustedProxies.Length; i++)
        {
            overrides[$"Backend:TrustedProxies:{i}"] = trustedProxies[i];
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

    public static BackendRuntimeOptions BindAndValidateRuntimeOptions(
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var options = new BackendRuntimeOptions();
        try
        {
            configuration.GetSection("Backend").Bind(options);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Backend:AuthMode", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"AUTH_MODE '{configuration["Backend:AuthMode"]}' is invalid. Use open, standard, or vault.",
                ex);
        }

        var configuredAuthMode = configuration["Backend:AuthMode"];
        if (string.IsNullOrWhiteSpace(configuredAuthMode)
            || !Enum.TryParse<BackendAuthMode>(configuredAuthMode, ignoreCase: true, out var authMode))
        {
            throw new InvalidOperationException(
                $"AUTH_MODE '{configuredAuthMode}' is invalid. Use open, standard, or vault.");
        }

        options.AuthMode = authMode;
        options.AuthCookie.EffectiveRequireSecure = options.AuthCookie.RequireSecure ?? environment.IsProduction();
        Validate(options);
        ValidatePackagedPostgresSecrets(configuration);
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

        // Fail fast on malformed TRUSTED_PROXIES entries instead of silently trusting nothing.
        _ = TrustedProxyConfiguration.Parse(options.TrustedProxies);

        if (options.AuthMode == BackendAuthMode.Vault)
        {
            throw new InvalidOperationException(
                "AUTH_MODE 'vault' is reserved for a later phase and is not implemented yet. Use open or standard.");
        }

        if (options.AuthRateLimitPerMinute <= 0)
        {
            throw new InvalidOperationException("AUTH_RATE_LIMIT_PER_MINUTE must be greater than 0.");
        }

        if (options.Jwt.AccessTokenLifetimeMinutes <= 0)
        {
            throw new InvalidOperationException("JWT_ACCESS_TOKEN_LIFETIME_MINUTES must be greater than 0.");
        }

        if (options.Jwt.RefreshTokenLifetimeDays <= 0)
        {
            throw new InvalidOperationException("JWT_REFRESH_TOKEN_LIFETIME_DAYS must be greater than 0.");
        }

        if (string.IsNullOrWhiteSpace(options.Jwt.Issuer))
        {
            throw new InvalidOperationException("JWT_ISSUER must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(options.Jwt.Audience))
        {
            throw new InvalidOperationException("JWT_AUDIENCE must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(options.AuthCookie.RefreshCookieName))
        {
            throw new InvalidOperationException("AUTH_REFRESH_COOKIE_NAME must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(options.AuthCookie.SameSite))
        {
            throw new InvalidOperationException("AUTH_REFRESH_COOKIE_SAME_SITE must not be empty.");
        }

        options.AuthCookie.SameSite = options.AuthCookie.SameSite.Trim().ToLowerInvariant() switch
        {
            "strict" => "Strict",
            "lax" => "Lax",
            "none" => "None",
            _ => options.AuthCookie.SameSite.Trim()
        };

        if (options.AuthCookie.SameSite is not ("Strict" or "Lax" or "None"))
        {
            throw new InvalidOperationException(
                $"AUTH_REFRESH_COOKIE_SAME_SITE '{options.AuthCookie.SameSite}' is invalid. Use Strict, Lax, or None.");
        }

        if (string.IsNullOrWhiteSpace(options.AuthCookie.Path))
        {
            throw new InvalidOperationException("AUTH_REFRESH_COOKIE_PATH must not be empty.");
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

    private static void ValidatePackagedPostgresSecrets(IConfiguration configuration)
    {
        var configuredProvider = configuration["Database:Provider"];
        if (!Enum.TryParse<DatabaseProvider>(configuredProvider, ignoreCase: true, out var provider)
            || provider != DatabaseProvider.Postgres)
        {
            return;
        }

        EnsureSafePostgresSecret(
            "POSTGRES_SUPERUSER_PASSWORD",
            configuration["Backend:PostgresPackaging:SuperuserPassword"]);
        EnsureSafePostgresConnectionPassword(
            "DATABASE_SYSTEM_CONNECTION",
            configuration["Database:SystemConnection"]);
        EnsureSafePostgresConnectionPassword(
            "DATABASE_DATA_CONNECTION",
            configuration["Database:DataConnection"]);
    }

    private static void EnsureSafePostgresConnectionPassword(string envName, string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        string? password;
        try
        {
            var builder = new DbConnectionStringBuilder
            {
                ConnectionString = connectionString
            };

            password = builder.TryGetValue("Password", out var value)
                ? Convert.ToString(value, CultureInfo.InvariantCulture)
                : null;
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException(
                $"Could not validate {envName} because the configured connection string is invalid.",
                ex);
        }

        EnsureSafePostgresSecret(envName, password);
    }

    private static void EnsureSafePostgresSecret(string envName, string? value)
    {
        if (!IsKnownUnsafePostgresSecret(value))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Postgres startup refused because {envName} is using a committed dev default or placeholder value. " +
            "Set a real secret before starting the packaged Postgres deployment.");
    }

    private static bool IsKnownUnsafePostgresSecret(string? value)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        return normalized.Equals("repere_dev", StringComparison.Ordinal)
            || normalized.Equals("repere_system_dev", StringComparison.Ordinal)
            || normalized.Equals("repere_data_dev", StringComparison.Ordinal)
            || normalized.Equals("CHANGE_ME", StringComparison.OrdinalIgnoreCase);
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
