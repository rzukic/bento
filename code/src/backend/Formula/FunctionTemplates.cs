namespace Bento.Api.Formula;

/// <summary>A starting point for a function: copied into the editor, then the user's own code.</summary>
public sealed record FunctionTemplate(
    string Id,
    string Title,
    string Description,
    string Name,
    IReadOnlyList<CustomFunctionArgument> Arguments,
    string ResultType,
    IReadOnlyList<string> AllowedHosts,
    IReadOnlyList<string> Credentials,
    string Code);

/// <summary>
/// Function templates (Step 4.2). Nothing here is built into Bento's behaviour:
/// a template only fills the editor; the saved function is the user's to change.
/// </summary>
public static class FunctionTemplates
{
    public static readonly IReadOnlyList<FunctionTemplate> All =
    [
        new(
            "yahoo-prices",
            "Prices from Yahoo Finance",
            "Daily closing prices and today's price (about 15 minutes late) for Yahoo symbols such as VWCE.DE (Xetra). No key needed; Yahoo has no official API, so this may stop working.",
            "yahoo_prices",
            [new("symbols", "any"), new("from", "date"), new("to", "date")],
            "any",
            ["query1.finance.yahoo.com"],
            [],
            """
            // Prices from Yahoo Finance (unofficial, no key).
            // symbols: Yahoo symbols, e.g. ["VWCE.DE", "EUNL.DE"] (.DE = Xetra); from, to: "yyyy-MM-dd".
            // Returns one row per symbol and trading day: { symbol, date, price, time }.
            const toDay = (seconds, offset) => new Date((seconds + (offset ?? 0)) * 1000).toISOString().slice(0, 10);
            const rows = [];
            const add = (symbol, meta, timestamps, closes) => {
              (timestamps ?? []).forEach((t, i) => {
                const date = toDay(t, meta.gmtoffset);
                if (closes?.[i] != null && date >= from && date <= to) rows.push({ symbol, date, price: closes[i] });
              });
              // Today's price as it is now.
              if (meta.regularMarketPrice != null && meta.regularMarketTime) {
                const date = toDay(meta.regularMarketTime, meta.gmtoffset);
                if (date < from || date > to) return;
                const time = new Date(meta.regularMarketTime * 1000).toISOString();
                const today = rows.find((r) => r.symbol === symbol && r.date === date);
                if (today) Object.assign(today, { price: meta.regularMarketPrice, time });
                else rows.push({ symbol, date, price: meta.regularMarketPrice, time });
              }
            };

            if ((Date.parse(to) - Date.parse(from)) / 86400000 <= 7) {
              // The last days of up to 20 symbols per request.
              for (let i = 0; i < symbols.length; i += 20) {
                const list = symbols.slice(i, i + 20).map(encodeURIComponent).join(",");
                const r = await fetch(`https://query1.finance.yahoo.com/v7/finance/spark?symbols=${list}&range=5d&interval=1d`);
                if (!r.ok) throw new Error(`Yahoo answered ${r.status}`);
                for (const item of (await r.json()).spark?.result ?? []) {
                  const series = item.response?.[0];
                  if (series) add(item.symbol, series.meta, series.timestamp, series.indicators?.quote?.[0]?.close);
                }
              }
            } else {
              // A longer history: one request per symbol.
              const period1 = Math.floor(Date.parse(from) / 1000);
              const period2 = Math.floor(Date.parse(to) / 1000) + 86400;
              for (const symbol of symbols) {
                const r = await fetch(`https://query1.finance.yahoo.com/v8/finance/chart/${encodeURIComponent(symbol)}?period1=${period1}&period2=${period2}&interval=1d`);
                if (!r.ok) {
                  console.log(symbol, "answered", r.status);
                  continue;
                }
                const series = (await r.json()).chart?.result?.[0];
                if (series) add(symbol, series.meta, series.timestamp, series.indicators?.quote?.[0]?.close);
              }
            }
            return rows;
            """),
        new(
            "openfigi-xetra",
            "Xetra symbols for ISINs (OpenFIGI)",
            "Suggests Yahoo symbols for ISINs: the German ticker from OpenFIGI plus \".DE\" (Xetra). Free, no key.",
            "isin_to_yahoo",
            [new("keys", "any")],
            "any",
            ["api.openfigi.com"],
            [],
            """
            // Suggests Yahoo symbols for ISINs via OpenFIGI (free, no key: 10 ISINs per request).
            // keys: ISINs, e.g. ["IE00BK5BQT80"]; returns { "IE00BK5BQT80": "VWCE.DE" }.
            const german = ["GR", "GY", "GF", "GD", "GM", "GS", "GH", "GT", "GI"];
            const result = {};
            for (let i = 0; i < keys.length; i += 10) {
              const chunk = keys.slice(i, i + 10);
              const r = await fetch("https://api.openfigi.com/v3/mapping", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify(chunk.map((isin) => ({ idType: "ID_ISIN", idValue: isin }))),
              });
              if (!r.ok) throw new Error(`OpenFIGI answered ${r.status}`);
              (await r.json()).forEach((answer, j) => {
                const listing = (answer.data ?? []).find((d) => german.includes(d.exchCode));
                if (listing) result[chunk[j]] = `${listing.ticker}.DE`;
              });
            }
            return result;
            """)
    ];
}
