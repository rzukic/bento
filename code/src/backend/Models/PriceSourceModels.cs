namespace Bento.Api.Models;

/// <summary>
/// A price source (Step 4.2): a function that fetches prices, and where they go.
/// The function takes (symbols, from, to) and returns rows
/// <c>[{ symbol, date, price, time? }]</c>; Bento writes one row per key and date
/// into the target table (today's row is updated during the day).
/// </summary>
public sealed class PriceSourceConfig
{
    /// <summary>The function that fetches prices: arguments symbols (any), from (date), to (date); result any.</summary>
    public string? Function { get; set; }

    /// <summary>Optional: suggests symbols for keys, (keys) → [{ key, symbol }] or { key: symbol }.</summary>
    public string? LookupFunction { get; set; }

    /// <summary>The prices table and its columns, e.g. finance_prices: isin, date, price.</summary>
    public string? Table { get; set; }
    public string? KeyColumn { get; set; }
    public string? DateColumn { get; set; }
    public string? PriceColumn { get; set; }

    /// <summary>Where the securities come from, as table.column, e.g. finance_trades.isin.</summary>
    public string? KeysFrom { get; set; }

    /// <summary>The provider's symbol per key (VWCE.DE for IE00BK5BQT80); a key without one is sent as it is.</summary>
    public Dictionary<string, string>? Symbols { get; set; }

    /// <summary>How far back to fetch for a new key (yyyy-MM-dd); empty: five years.</summary>
    public string? HistoryFrom { get; set; }

    /// <summary>Opening or refreshing a dashboard fetches again when the last fetch is older than this.</summary>
    public int? FreshMinutes { get; set; }

    /// <summary>Time of the daily run (closing prices, new keys), in <see cref="TimeZone"/>.</summary>
    public string? DailyAt { get; set; }
    public string? TimeZone { get; set; }

    /// <summary>Stops fetching for the day once the function made this many requests (free API tiers).</summary>
    public int? MaxRequestsPerDay { get; set; }
}

public sealed class PriceSourceRequest
{
    public string? Name { get; init; }
    public bool? Enabled { get; init; }
    public PriceSourceConfig? Config { get; init; }
}

public sealed record PriceSourceKey(string Key, string Symbol, bool Mapped, string? LastDate, decimal? LastPrice);

public sealed record PriceSourceStatus(
    string? LastRunAt,
    string? LastSuccessAt,
    string? LastQuoteAt,
    string? LastError,
    int RequestsToday,
    bool Running);

public sealed record PriceSourceResponse(
    long Id,
    string Name,
    bool Enabled,
    PriceSourceConfig Config,
    PriceSourceStatus Status,
    IReadOnlyList<PriceSourceKey> Keys);

public sealed record PriceSourceRun(
    long Id,
    string Kind,
    string StartedAt,
    string? FinishedAt,
    int Requests,
    int RowsWritten,
    string? LastQuoteAt,
    string? Error);

/// <summary>The outcome of a refresh: whether any price changed, and each source's state.</summary>
public sealed record PriceRefreshResponse(bool Changed, string? LastQuoteAt, IReadOnlyList<PriceRefreshSource> Sources);

public sealed record PriceRefreshSource(long Id, string Name, string Table, bool Fetched, int RowsWritten, string? LastQuoteAt, string? Error);

public sealed class PriceRefreshRequest
{
    /// <summary>Only sources writing to these tables (e.g. those a dashboard reads); empty: all.</summary>
    public List<string>? Tables { get; init; }
    /// <summary>Fetch even when the prices are fresh.</summary>
    public bool Force { get; init; }
}

public sealed record SymbolSuggestion(string Key, string Symbol);
