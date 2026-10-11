using System.Globalization;
using System.Text.RegularExpressions;
using Dapper;
using Bento.Api.Db;
using Bento.Api.Exceptions;

namespace Bento.Api.Services;

/// <summary>
/// Workspace-wide settings an admin chooses (beta feedback: amounts showed as
/// "€1,250.00" in an English browser): the locale numbers and dates are written
/// in (null: each browser's language) and the currency of amounts.
/// </summary>
public sealed partial class WorkspaceSettings
{
    public const string DefaultCurrency = "EUR";

    private readonly ISystemPool _systemPool;

    public WorkspaceSettings(ISystemPool systemPool)
    {
        _systemPool = systemPool;
    }

    public sealed record Values(string? Locale, string Currency);

    public async Task<Values> GetAsync()
    {
        using var conn = await _systemPool.GetConnectionAsync();
        var rows = (await conn.QueryAsync<(string Key, string? Value)>($"SELECT key, value FROM {Table}"))
            .ToDictionary(r => r.Key, r => r.Value, StringComparer.Ordinal);
        return new Values(
            rows.TryGetValue("locale", out var locale) && !string.IsNullOrWhiteSpace(locale) ? locale : null,
            rows.TryGetValue("currency", out var currency) && !string.IsNullOrWhiteSpace(currency) ? currency! : DefaultCurrency);
    }

    /// <summary>Validates and stores both; a null or empty locale means each browser's language.</summary>
    public async Task<Values> SetAsync(string? locale, string? currency)
    {
        locale = string.IsNullOrWhiteSpace(locale) ? null : locale.Trim();
        if (locale is not null)
        {
            try
            {
                locale = CultureInfo.GetCultureInfo(locale, predefinedOnly: true).Name;
            }
            catch (CultureNotFoundException)
            {
                throw new ApiValidationException($"'{locale}' is not a language and region like de-AT or en-US.", new Dictionary<string, object?> { ["field"] = "locale" });
            }
        }
        var code = string.IsNullOrWhiteSpace(currency) ? DefaultCurrency : currency.Trim().ToUpperInvariant();
        if (!CurrencyCode().IsMatch(code))
            throw new ApiValidationException($"'{currency}' is not a currency code like EUR or USD.", new Dictionary<string, object?> { ["field"] = "currency" });

        using var conn = await _systemPool.GetConnectionAsync();
        foreach (var (key, value) in new[] { ("locale", locale), ("currency", (string?)code) })
        {
            await conn.ExecuteAsync($"DELETE FROM {Table} WHERE key = @Key", new { Key = key });
            await conn.ExecuteAsync($"INSERT INTO {Table} (key, value) VALUES (@Key, @Value)", new { Key = key, Value = value });
        }
        return new Values(locale, code);
    }

    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex CurrencyCode();

    private string Table => _systemPool.Provider == DatabaseProvider.Postgres ? "_system.workspace_settings" : "workspace_settings";
}
