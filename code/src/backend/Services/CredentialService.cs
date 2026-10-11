using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Bento.Api.Db;
using Bento.Api.Exceptions;
using Bento.Api.Formula;
using Dapper;

namespace Bento.Api.Services;

public sealed record CredentialResponse(string Name, string? Description, string CreatedAt, string? UpdatedAt, string? LastUsedAt, IReadOnlyList<string> UsedBy, bool Readable);

public sealed class CredentialRequest
{
    public string? Name { get; init; }
    public string? Value { get; init; }
    public string? Description { get; init; }
}

/// <summary>
/// The credential vault (Part 3 R3.6, Step 4.2): API keys for price sources
/// and other functions that call APIs. Values are encrypted with AES-GCM under
/// a key derived from the server's JWT secret, never returned by the API, and
/// only handed to the functions that name them. Changing the JWT secret makes
/// stored values unreadable; they are then entered again.
/// </summary>
public sealed class CredentialService
{
    private const string Prefix = "v1:";

    private readonly ISystemPool _systemPool;
    private readonly CustomFunctionCatalog _functions;
    private readonly byte[]? _key;

    public CredentialService(ISystemPool systemPool, CustomFunctionCatalog functions, IConfiguration configuration)
    {
        _systemPool = systemPool;
        _functions = functions;
        var secret = configuration["Backend:Jwt:Secret"];
        _key = string.IsNullOrWhiteSpace(secret)
            ? null
            : HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(secret), 32, info: Encoding.UTF8.GetBytes("bento-credentials"));
    }

    private bool Pg => _systemPool.Provider == DatabaseProvider.Postgres;
    private string Table => Pg ? "_system.credentials" : "credentials";
    private string NowParam => SystemSqlDialect.TimestampParam(_systemPool.Provider, "@Now");

    private sealed class Row
    {
        public string Name { get; init; } = "";
        public string? Description { get; init; }
        public string EncryptedValue { get; init; } = "";
        public string CreatedAt { get; init; } = "";
        public string? UpdatedAt { get; init; }
        public string? LastUsedAt { get; init; }
    }

    private string Columns =>
        "name, description, encrypted_value AS EncryptedValue, " +
        $"{SystemSqlDialect.TimestampText(_systemPool.Provider, "created_at")} AS CreatedAt, " +
        $"{SystemSqlDialect.TimestampText(_systemPool.Provider, "updated_at")} AS UpdatedAt, " +
        $"{SystemSqlDialect.TimestampText(_systemPool.Provider, "last_used_at")} AS LastUsedAt";

    public async Task<IReadOnlyList<CredentialResponse>> ListAsync()
    {
        using var conn = await _systemPool.GetConnectionAsync();
        var rows = await conn.QueryAsync<Row>($"SELECT {Columns} FROM {Table} ORDER BY name");
        return rows.Select(ToResponse).ToList();
    }

    public async Task<CredentialResponse> CreateAsync(CredentialRequest request)
    {
        var name = request.Name?.Trim().ToLowerInvariant() ?? "";
        if (!CustomFunctionRules.IsValidName(name))
            throw Invalid("name", "A credential name uses small letters, digits and _ and starts with a letter, e.g. alphavantage_key. Functions read it as credentials.name.");
        var value = RequireValue(request.Value);
        using var conn = await _systemPool.GetConnectionAsync();
        if (await FindAsync(conn, name) is not null)
            throw new ApiConflictException($"A credential named '{name}' already exists; replace its value instead.", new Dictionary<string, object?> { ["field"] = "name" });
        await conn.ExecuteAsync(
            $"INSERT INTO {Table} (name, description, encrypted_value, created_at, updated_at) VALUES (@Name, @Description, @Value, {NowParam}, {NowParam})",
            new { Name = name, Description = Describe(request.Description), Value = Encrypt(value), Now = UtcNowIso() });
        return ToResponse((await FindAsync(conn, name))!);
    }

    public async Task<CredentialResponse> UpdateAsync(string name, CredentialRequest request)
    {
        using var conn = await _systemPool.GetConnectionAsync();
        var existing = await FindAsync(conn, name) ?? throw new ApiNotFoundException($"Credential '{name}' not found.");
        await conn.ExecuteAsync(
            $"UPDATE {Table} SET description = @Description, encrypted_value = @Value, updated_at = {NowParam} WHERE name = @Name",
            new
            {
                existing.Name,
                Description = request.Description is null ? existing.Description : Describe(request.Description),
                Value = request.Value is null ? existing.EncryptedValue : Encrypt(RequireValue(request.Value)),
                Now = UtcNowIso()
            });
        return ToResponse((await FindAsync(conn, existing.Name))!);
    }

    public async Task DeleteAsync(string name)
    {
        using var conn = await _systemPool.GetConnectionAsync();
        var existing = await FindAsync(conn, name) ?? throw new ApiNotFoundException($"Credential '{name}' not found.");
        var users = UsedBy(existing.Name);
        if (users.Count > 0)
            throw new ApiConflictException($"'{existing.Name}' is used by {string.Join(", ", users)}; remove it from those functions first.",
                new Dictionary<string, object?> { ["functions"] = users });
        await conn.ExecuteAsync($"DELETE FROM {Table} WHERE name = @Name", new { existing.Name });
    }

    /// <summary>Whether every name is stored (for checking a function's list).</summary>
    public async Task<IReadOnlyList<string>> MissingAsync(IEnumerable<string> names)
    {
        using var conn = await _systemPool.GetConnectionAsync();
        var stored = (await conn.QueryAsync<string>($"SELECT name FROM {Table}")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return names.Where(n => !stored.Contains(n)).ToList();
    }

    /// <summary>The plain values a function may read; a missing or unreadable one is an error.</summary>
    public async Task<IReadOnlyDictionary<string, string>> ResolveAsync(IReadOnlyCollection<string> names)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (names.Count == 0)
            return values;
        using var conn = await _systemPool.GetConnectionAsync();
        foreach (var name in names)
        {
            var row = await FindAsync(conn, name)
                ?? throw new ApiUnprocessableEntityException($"The credential '{name}' is not stored; add it under Functions → Credentials.");
            values[name] = Decrypt(row.EncryptedValue)
                ?? throw new ApiUnprocessableEntityException($"The credential '{name}' cannot be read (the server's secret changed); enter its value again.");
        }
        await conn.ExecuteAsync($"UPDATE {Table} SET last_used_at = {NowParam} WHERE {(Pg ? "name = ANY(@Names)" : "name IN @Names")}", new { Names = names.ToArray(), Now = UtcNowIso() });
        return values;
    }

    private List<string> UsedBy(string name) =>
        _functions.Functions.Values
            .Where(f => f.Credentials.Contains(name, StringComparer.OrdinalIgnoreCase))
            .Select(f => f.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

    private CredentialResponse ToResponse(Row row) =>
        new(row.Name, row.Description, row.CreatedAt, row.UpdatedAt, row.LastUsedAt, UsedBy(row.Name), Decrypt(row.EncryptedValue) is not null);

    private async Task<Row?> FindAsync(System.Data.IDbConnection conn, string name) =>
        await conn.QuerySingleOrDefaultAsync<Row>($"SELECT {Columns} FROM {Table} WHERE LOWER(name) = LOWER(@Name)", new { Name = name.Trim() });

    // ── Encryption ───────────────────────────────────────────────────────

    /// <summary>Encrypts a secret kept elsewhere (e.g. a notification channel's password) under the vault's key.</summary>
    public string Protect(string value) => Encrypt(value);

    /// <summary>A secret from <see cref="Protect"/>, or null when it cannot be read (the server's secret changed).</summary>
    public string? Unprotect(string stored) => Decrypt(stored);

    private string Encrypt(string value)
    {
        var key = _key ?? throw new ApiUnprocessableEntityException("The server has no JWT secret, so credentials cannot be stored.");
        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var plain = Encoding.UTF8.GetBytes(value);
        var cipher = new byte[plain.Length];
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];
        using var aes = new AesGcm(key, tag.Length);
        aes.Encrypt(nonce, plain, cipher, tag);
        return Prefix + Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }

    private string? Decrypt(string stored)
    {
        if (_key is null || !stored.StartsWith(Prefix, StringComparison.Ordinal))
            return null;
        try
        {
            var bytes = Convert.FromBase64String(stored[Prefix.Length..]);
            var nonceSize = AesGcm.NonceByteSizes.MaxSize;
            var tagSize = AesGcm.TagByteSizes.MaxSize;
            var plain = new byte[bytes.Length - nonceSize - tagSize];
            using var aes = new AesGcm(_key, tagSize);
            aes.Decrypt(bytes.AsSpan(0, nonceSize), bytes.AsSpan(nonceSize + tagSize), bytes.AsSpan(nonceSize, tagSize), plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception e) when (e is CryptographicException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    private static string RequireValue(string? value)
    {
        if (string.IsNullOrEmpty(value))
            throw Invalid("value", "Enter the value, e.g. the API key.");
        if (value.Length > 4000)
            throw Invalid("value", "The value is too long (at most 4000 characters).");
        return value;
    }

    private static string? Describe(string? description)
    {
        var trimmed = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        if (trimmed is { Length: > 500 })
            throw Invalid("description", "The description is too long (at most 500 characters).");
        return trimmed;
    }

    private static string UtcNowIso() => DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

    private static ApiValidationException Invalid(string field, string message) =>
        new(message, new Dictionary<string, object?> { ["field"] = field });
}
