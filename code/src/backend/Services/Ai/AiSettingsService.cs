using System.Globalization;
using Dapper;
using Bento.Api.Db;
using Bento.Api.Exceptions;

namespace Bento.Api.Services.Ai;

/// <summary>
/// The AI settings (Step 4.7): the Anthropic key — stored in the credential vault
/// as <see cref="CredentialName"/>, never returned — the model, and a monthly
/// spending cap; plus every model call with its tokens and an estimated cost.
/// </summary>
public sealed class AiSettingsService
{
    public const string CredentialName = "anthropic_api_key";
    public const string DefaultModel = "claude-opus-5-5";

    /// <summary>Models offered in the settings, most capable first.</summary>
    public static readonly IReadOnlyList<AiModelOption> Models =
    [
        new("claude-opus-5-5", "Claude Opus 5.5", "Most capable; best explanations", 4.00m, 20.00m, 0.20m, 5.00m),
        new("claude-sonnet-5-5", "Claude Sonnet 5.5", "Fast and capable, about half the price", 2.00m, 10.00m, 0.20m, 2.50m),
        new("claude-haiku-5-5", "Claude Haiku 5.5", "Fastest and cheapest, for simple questions", 0.10m, 0.50m, 0.01m, 0.125m),
    ];

    /// <summary>Prices in US dollars per million tokens: input, output, cache read, cache write.</summary>
    public sealed record AiModelOption(string Id, string Name, string Description, decimal Input, decimal Output, decimal CacheRead, decimal CacheWrite);

    public sealed record AiSettings(bool Configured, bool Enabled, string Model, decimal MonthlyLimitUsd);

    public sealed record UsageEntry(long? UserId, string Feature, string Model, AiUsage Usage, int ToolCalls, bool Ok, string? Error, int DurationMs);

    public sealed class UsageRow
    {
        public long Id { get; init; }
        public string CalledAt { get; init; } = "";
        public long? UserId { get; init; }
        public string? Username { get; init; }
        public string Feature { get; init; } = "";
        public string Model { get; init; } = "";
        public long InputTokens { get; init; }
        public long OutputTokens { get; init; }
        public long CacheReadTokens { get; init; }
        public long CacheWriteTokens { get; init; }
        public decimal CostUsd { get; init; }
        public int ToolCalls { get; init; }
        public bool Ok { get; init; }
        public string? Error { get; init; }
        public int DurationMs { get; init; }
    }

    private const decimal DefaultMonthlyLimitUsd = 10m;

    private readonly ISystemPool _systemPool;
    private readonly CredentialService _credentials;

    public AiSettingsService(ISystemPool systemPool, CredentialService credentials)
    {
        _systemPool = systemPool;
        _credentials = credentials;
    }

    public async Task<AiSettings> GetAsync()
    {
        var values = await ReadAllAsync();
        var configured = (await _credentials.MissingAsync([CredentialName])).Count == 0;
        var model = values.GetValueOrDefault("model") is { } m && Models.Any(o => o.Id == m) ? m : DefaultModel;
        var limit = decimal.TryParse(values.GetValueOrDefault("monthly_limit_usd"), NumberStyles.Number, CultureInfo.InvariantCulture, out var l) ? l : DefaultMonthlyLimitUsd;
        var enabled = values.GetValueOrDefault("enabled") != "false";
        return new AiSettings(configured, configured && enabled, model, limit);
    }

    /// <summary>Changes what is given; an empty key removes the stored one.</summary>
    public async Task<AiSettings> SaveAsync(string? apiKey, string? model, decimal? monthlyLimitUsd, bool? enabled)
    {
        if (model is not null && Models.All(o => o.Id != model))
            throw new ApiValidationException($"Unknown model '{model}'.", new Dictionary<string, object?> { ["field"] = "model", ["models"] = Models.Select(o => o.Id).ToList() });
        if (monthlyLimitUsd is < 0 or > 10_000)
            throw new ApiValidationException("The monthly limit must be between 0 and 10,000 dollars.", new Dictionary<string, object?> { ["field"] = "monthlyLimitUsd" });

        if (apiKey is not null)
        {
            var exists = (await _credentials.MissingAsync([CredentialName])).Count == 0;
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                if (exists) await _credentials.DeleteAsync(CredentialName);
            }
            else
            {
                var request = new CredentialRequest { Name = CredentialName, Value = apiKey.Trim(), Description = "Anthropic API key for Bento's AI features" };
                if (exists) await _credentials.UpdateAsync(CredentialName, request);
                else await _credentials.CreateAsync(request);
            }
        }

        if (model is not null) await WriteAsync("model", model);
        if (monthlyLimitUsd is { } limit) await WriteAsync("monthly_limit_usd", limit.ToString(CultureInfo.InvariantCulture));
        if (enabled is { } on) await WriteAsync("enabled", on ? "true" : "false");
        return await GetAsync();
    }

    /// <summary>The key, or a clear refusal when AI is off, not set up, or this month's limit is used up.</summary>
    public async Task<(string ApiKey, AiSettings Settings)> RequireReadyAsync()
    {
        var settings = await GetAsync();
        if (!settings.Configured)
            throw new ApiUnprocessableEntityException("AI is not set up yet: an admin adds an Anthropic API key on the AI page.");
        if (!settings.Enabled)
            throw new ApiUnprocessableEntityException("AI is switched off on the AI page.");
        var spent = await SpentThisMonthAsync();
        if (spent >= settings.MonthlyLimitUsd)
            throw new ApiUnprocessableEntityException(
                $"This month's AI budget is used up (${spent.ToString("0.00", CultureInfo.InvariantCulture)} of ${settings.MonthlyLimitUsd.ToString("0.00", CultureInfo.InvariantCulture)}). An admin can raise it on the AI page.",
                new Dictionary<string, object?> { ["code"] = "ai_budget_exhausted" });
        var key = (await _credentials.ResolveAsync([CredentialName]))[CredentialName];
        return (key, settings);
    }

    public static decimal Cost(string model, AiUsage usage)
    {
        var price = Models.FirstOrDefault(o => model.StartsWith(o.Id, StringComparison.Ordinal)) ?? Models[0];
        return (usage.InputTokens * price.Input + usage.OutputTokens * price.Output +
                usage.CacheReadTokens * price.CacheRead + usage.CacheWriteTokens * price.CacheWrite) / 1_000_000m;
    }

    public async Task RecordAsync(UsageEntry entry)
    {
        using var conn = await _systemPool.GetConnectionAsync();
        await conn.ExecuteAsync(
            $"""
            INSERT INTO {UsageTable} (called_at, user_id, feature, model, input_tokens, output_tokens, cache_read_tokens, cache_write_tokens, cost_usd, tool_calls, ok, error, duration_ms)
            VALUES ({Ts("@Now")}, @UserId, @Feature, @Model, @Input, @Output, @CacheRead, @CacheWrite, @Cost, @ToolCalls, @Ok, @Error, @DurationMs)
            """,
            new
            {
                Now = Iso(DateTime.UtcNow), entry.UserId, entry.Feature, entry.Model,
                Input = entry.Usage.InputTokens, Output = entry.Usage.OutputTokens,
                CacheRead = entry.Usage.CacheReadTokens, CacheWrite = entry.Usage.CacheWriteTokens,
                Cost = Cost(entry.Model, entry.Usage), entry.ToolCalls, entry.Ok,
                Error = entry.Error is { Length: > 2000 } longError ? longError[..2000] : entry.Error, entry.DurationMs
            });
    }

    public async Task<decimal> SpentThisMonthAsync()
    {
        var now = DateTime.UtcNow;
        var start = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        using var conn = await _systemPool.GetConnectionAsync();
        return await conn.ExecuteScalarAsync<decimal?>($"SELECT SUM(cost_usd) FROM {UsageTable} WHERE called_at >= {Ts("@Start")}", new { Start = Iso(start) }) ?? 0m;
    }

    public async Task<IReadOnlyList<UsageRow>> ListUsageAsync(long? userId, int limit)
    {
        using var conn = await _systemPool.GetConnectionAsync();
        var rows = await conn.QueryAsync<UsageRow>(
            $"""
            SELECT a.id, {SystemSqlDialect.TimestampText(_systemPool.Provider, "a.called_at")} AS CalledAt, a.user_id AS UserId, u.username,
                   a.feature, a.model, a.input_tokens AS InputTokens, a.output_tokens AS OutputTokens,
                   a.cache_read_tokens AS CacheReadTokens, a.cache_write_tokens AS CacheWriteTokens, a.cost_usd AS CostUsd,
                   a.tool_calls AS ToolCalls, a.ok, a.error, a.duration_ms AS DurationMs
            FROM {UsageTable} a
            LEFT JOIN {Users} u ON u.id = a.user_id
            {(userId is null ? "" : "WHERE a.user_id = @UserId")}
            ORDER BY a.called_at DESC, a.id DESC
            LIMIT @Limit
            """,
            new { UserId = userId, Limit = Math.Clamp(limit, 1, 500) });
        return rows.ToList();
    }

    private async Task<Dictionary<string, string?>> ReadAllAsync()
    {
        using var conn = await _systemPool.GetConnectionAsync();
        var rows = await conn.QueryAsync<(string Key, string? Value)>($"SELECT key, value FROM {SettingsTable}");
        return rows.ToDictionary(r => r.Key, r => r.Value, StringComparer.Ordinal);
    }

    private async Task WriteAsync(string key, string value)
    {
        using var conn = await _systemPool.GetConnectionAsync();
        await conn.ExecuteAsync(
            $"INSERT INTO {SettingsTable} (key, value) VALUES (@Key, @Value) ON CONFLICT (key) DO UPDATE SET value = excluded.value",
            new { Key = key, Value = value });
    }

    private static string Iso(DateTime value) => value.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    private string Ts(string parameter) => SystemSqlDialect.TimestampParam(_systemPool.Provider, parameter);
    private string SettingsTable => _systemPool.Provider == DatabaseProvider.Postgres ? "_system.ai_settings" : "ai_settings";
    private string UsageTable => _systemPool.Provider == DatabaseProvider.Postgres ? "_system.ai_usage" : "ai_usage";
    private string Users => _systemPool.Provider == DatabaseProvider.Postgres ? "_system.users" : "users";
}
