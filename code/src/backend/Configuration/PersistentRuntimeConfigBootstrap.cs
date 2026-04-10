using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Repere.Api.Configuration;

public static class PersistentRuntimeConfigBootstrap
{
    private static readonly object WarningLock = new();
    private static readonly HashSet<string> WarnedInsecureConfigPaths = new(StringComparer.Ordinal);
    private const UnixFileMode SecureConfigFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode SecureDataDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    public static IReadOnlyDictionary<string, string?> BuildOverrides(
        IConfiguration configuration,
        ILogger? logger = null)
    {
        var dataDir = configuration["Backend:DataDir"];
        if (string.IsNullOrWhiteSpace(dataDir))
        {
            dataDir = "./data";
        }

        EnsureDataDirectoryExists(dataDir, logger);

        var configPath = Path.Combine(dataDir, "config.json");
        var overrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Backend:PersistentConfigPath"] = configPath
        };

        if (!string.IsNullOrWhiteSpace(configuration["Backend:Jwt:Secret"]))
        {
            return overrides;
        }

        var secret = ReadPersistedJwtSecret(configPath, logger);
        if (string.IsNullOrWhiteSpace(secret))
        {
            secret = GenerateJwtSecret();
            WritePersistedJwtSecret(configPath, secret, logger);
        }

        overrides["Backend:Jwt:Secret"] = secret;
        return overrides;
    }

    private static string? ReadPersistedJwtSecret(string configPath, ILogger? logger)
    {
        if (!File.Exists(configPath))
        {
            return null;
        }

        WarnIfConfigPathIsOverPermissive(configPath, logger);

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(configPath));
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Persistent runtime config '{configPath}' contains invalid JSON.",
                ex);
        }

        if (root is not JsonObject obj)
        {
            throw new InvalidOperationException(
                $"Persistent runtime config '{configPath}' must contain a JSON object.");
        }

        return obj["Backend"]?["Jwt"]?["Secret"]?.GetValue<string>();
    }

    private static void WritePersistedJwtSecret(string configPath, string secret, ILogger? logger)
    {
        JsonObject root;
        if (File.Exists(configPath))
        {
            WarnIfConfigPathIsOverPermissive(configPath, logger);

            JsonNode? parsed;
            try
            {
                parsed = JsonNode.Parse(File.ReadAllText(configPath));
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    $"Persistent runtime config '{configPath}' contains invalid JSON.",
                    ex);
            }

            if (parsed is not JsonObject obj)
            {
                throw new InvalidOperationException(
                    $"Persistent runtime config '{configPath}' must contain a JSON object.");
            }

            root = obj;
        }
        else
        {
            root = new JsonObject();
        }

        var backend = root["Backend"] as JsonObject ?? new JsonObject();
        root["Backend"] = backend;

        var jwt = backend["Jwt"] as JsonObject ?? new JsonObject();
        backend["Jwt"] = jwt;

        jwt["Secret"] = secret;

        var json = root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true
        });

        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(configPath, json);
            return;
        }

        if (File.Exists(configPath))
        {
            File.WriteAllText(configPath, json);
            return;
        }

        using var stream = new FileStream(
            configPath,
            new FileStreamOptions
            {
                Access = FileAccess.Write,
                Mode = FileMode.CreateNew,
                Share = FileShare.None,
                Options = FileOptions.None,
                UnixCreateMode = SecureConfigFileMode
            });
        using var writer = new StreamWriter(stream);
        writer.Write(json);
    }

    private static void EnsureDataDirectoryExists(string dataDir, ILogger? logger)
    {
        Directory.CreateDirectory(dataDir);

        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(dataDir, SecureDataDirectoryMode);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            logger?.LogDebug(
                ex,
                "Could not tighten permissions for runtime data directory '{DataDir}'.",
                dataDir);
        }
    }

    private static void WarnIfConfigPathIsOverPermissive(string configPath, ILogger? logger)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(configPath))
        {
            return;
        }

        UnixFileMode mode;
        try
        {
            mode = File.GetUnixFileMode(configPath);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            logger?.LogDebug(
                ex,
                "Could not inspect permissions for runtime config '{ConfigPath}'.",
                configPath);
            return;
        }

        const UnixFileMode insecureMask =
            UnixFileMode.GroupRead |
            UnixFileMode.GroupWrite |
            UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead |
            UnixFileMode.OtherWrite |
            UnixFileMode.OtherExecute;

        if ((mode & insecureMask) == 0)
        {
            return;
        }

        var shouldWarn = false;
        lock (WarningLock)
        {
            if (WarnedInsecureConfigPaths.Add(configPath))
            {
                shouldWarn = true;
            }
        }

        if (!shouldWarn)
        {
            return;
        }

        logger?.LogWarning(
            "Persistent runtime config '{ConfigPath}' has over-permissive mode '{Mode}'. Rotate the JWT secret and tighten the file to 0600.",
            configPath,
            Convert.ToString((int)mode, 8));
    }

    private static string GenerateJwtSecret()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
