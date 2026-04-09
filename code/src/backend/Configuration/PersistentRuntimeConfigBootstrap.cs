using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Repere.Api.Configuration;

public static class PersistentRuntimeConfigBootstrap
{
    public static IReadOnlyDictionary<string, string?> BuildOverrides(IConfiguration configuration)
    {
        var dataDir = configuration["Backend:DataDir"];
        if (string.IsNullOrWhiteSpace(dataDir))
        {
            dataDir = "./data";
        }

        Directory.CreateDirectory(dataDir);

        var configPath = Path.Combine(dataDir, "config.json");
        var overrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Backend:PersistentConfigPath"] = configPath
        };

        if (!string.IsNullOrWhiteSpace(configuration["Backend:Jwt:Secret"]))
        {
            return overrides;
        }

        var secret = ReadPersistedJwtSecret(configPath);
        if (string.IsNullOrWhiteSpace(secret))
        {
            secret = GenerateJwtSecret();
            WritePersistedJwtSecret(configPath, secret);
        }

        overrides["Backend:Jwt:Secret"] = secret;
        return overrides;
    }

    private static string? ReadPersistedJwtSecret(string configPath)
    {
        if (!File.Exists(configPath))
        {
            return null;
        }

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

    private static void WritePersistedJwtSecret(string configPath, string secret)
    {
        JsonObject root;
        if (File.Exists(configPath))
        {
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

        File.WriteAllText(
            configPath,
            root.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true
            }));
    }

    private static string GenerateJwtSecret()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
