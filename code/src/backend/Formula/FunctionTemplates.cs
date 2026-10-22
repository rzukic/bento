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
    string Code,
    string Mode = "row");

/// <summary>
/// Function templates (Step 4.2). Nothing here is built into Bento's behaviour:
/// a template only fills the editor; the saved function is the user's to change.
/// The cost basis templates (Step 4.3b) run over a group of rows (CUSTOM_ROWS).
/// </summary>
public static class FunctionTemplates
{
    public static readonly IReadOnlyList<FunctionTemplate> All =
    [
        // Step 5.11: sources for a saved import — any JSON API, on a schedule.
        new(
            "json-api-rows",
            "Rows from a JSON API",
            "A starting point for a saved import from any JSON API: fetches the items page by page and returns them as rows. Change the host and the address; for a key, store it under Credentials, add it to the function and send it as a header.",
            "api_rows",
            [new("since", "text")],
            "any",
            ["api.example.com"],
            [],
            """
            // Rows for a saved import (Data → a table → Import → From a function).
            // since: when the import last worked (ISO date-time), null the first time — ask only for what is new.
            // Return a list of objects; the import maps their fields to columns (nested fields as a.b).
            const rows = [];
            let url = `https://api.example.com/v1/items?updated_since=${encodeURIComponent(since ?? "1970-01-01T00:00:00Z")}`;
            for (let page = 0; url && page < 50; page++) {
              const r = await fetch(url, { headers: { Accept: "application/json" /*, Authorization: `Bearer ${credentials.api_token}` */ } });
              if (!r.ok) throw new Error(`The API answered ${r.status}`);
              const body = await r.json();
              rows.push(...(body.items ?? body.data ?? body));
              url = body.next ?? null;
            }
            return rows;
            """),
        new(
            "open-meteo-daily",
            "Daily weather (Open-Meteo)",
            "The last 30 days of weather for a place — highest and lowest temperature, rain, sunshine — from Open-Meteo, free and without a key. Set the place's latitude and longitude; import it daily with the date as the key.",
            "daily_weather",
            [],
            "any",
            ["api.open-meteo.com"],
            [],
            """
            // Daily weather for one place from Open-Meteo (free, no key). Set your place:
            const latitude = 52.52, longitude = 13.41; // Berlin
            const r = await fetch(`https://api.open-meteo.com/v1/forecast?latitude=${latitude}&longitude=${longitude}` +
              "&daily=temperature_2m_max,temperature_2m_min,precipitation_sum,sunshine_duration&past_days=30&forecast_days=1&timezone=auto");
            if (!r.ok) throw new Error(`Open-Meteo answered ${r.status}`);
            const d = (await r.json()).daily;
            return d.time.map((date, i) => ({
              date,
              temp_max: d.temperature_2m_max[i],
              temp_min: d.temperature_2m_min[i],
              rain_mm: d.precipitation_sum[i],
              sun_hours: Math.round(d.sunshine_duration[i] / 360) / 10,
            }));
            """),
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
            """),
        new(
            "avg-cost",
            "Realised gains by average cost",
            "Runs over the trades of one security in date order (CUSTOM_ROWS): what a sale gained over the average cost of the shares sold, and how each trade changes the cost of the position. The method German banks use for funds.",
            AverageCost,
            [new("shares", "float"), new("price", "float"), new("fees", "float"), new("what", "text")],
            "float",
            [],
            [],
            """
            // Average cost, over one security's trades in order (CUSTOM_ROWS): each argument is a list with a value per trade.
            // shares: + bought, − sold; price: per share; fees: per trade; what: "realized", "cost", "held" or "average".
            //   realized — what a sale gained: proceeds − fees − the average cost of the shares sold (0 for buys)
            //   cost     — how the trade changes the position's cost: + paid for a buy, − the cost of the shares sold
            //   held, average — shares and average cost per share after the trade
            // Returns a list with a value per trade.
            const round = (x) => Math.round(x * 1e8) / 1e8;
            let held = 0;
            let cost = 0;
            return shares.map((n, i) => {
              const p = price[i] ?? 0;
              const f = fees[i] ?? 0;
              let realized = 0;
              let change = 0;
              if (n > 0) {
                change = n * p + f;
              } else if (n < 0) {
                const sold = Math.min(-n, held);
                const out = held > 0 ? (cost * sold) / held : 0;
                change = -out;
                realized = -n * p - f - out;
              }
              held += n;
              cost += change;
              if (Math.abs(held) < 1e-9) cost = 0;
              const values = { realized, cost: change, held, average: held > 0 ? cost / held : 0 };
              if (!(what[i] in values)) throw new Error(`what is "realized", "cost", "held" or "average", not "${what[i]}"`);
              return round(values[what[i]]);
            });
            """,
            "rows"),
        new(
            "fifo-cost",
            "Realised gains by FIFO",
            "Runs over the trades of one security in date order (CUSTOM_ROWS): a sale uses up the oldest shares first (first in, first out), as for shares and ETFs in Germany. Gives the realised gain and the change in the position's cost per trade.",
            "fifo_cost",
            [new("shares", "float"), new("price", "float"), new("fees", "float"), new("what", "text")],
            "float",
            [],
            [],
            """
            // First in, first out, over one security's trades in order (CUSTOM_ROWS): each argument is a list with a value per trade.
            // shares: + bought, − sold; price: per share; fees: per trade; what: "realized", "cost", "held" or "average".
            //   realized — what a sale gained: proceeds − fees − the cost of the oldest shares still held (0 for buys)
            //   cost     — how the trade changes the position's cost: + paid for a buy, − the cost of the shares sold
            //   held, average — shares and average cost per share after the trade
            // Returns a list with a value per trade.
            const round = (x) => Math.round(x * 1e8) / 1e8;
            const lots = []; // { n: shares left, unit: cost per share including the buy's fees }
            return shares.map((n, i) => {
              const p = price[i] ?? 0;
              const f = fees[i] ?? 0;
              let realized = 0;
              let change = 0;
              if (n > 0) {
                change = n * p + f;
                lots.push({ n, unit: change / n });
              } else if (n < 0) {
                let left = -n;
                let out = 0;
                while (left > 1e-12 && lots.length > 0) {
                  const take = Math.min(left, lots[0].n);
                  out += take * lots[0].unit;
                  lots[0].n -= take;
                  left -= take;
                  if (lots[0].n <= 1e-12) lots.shift();
                }
                change = -out;
                realized = -n * p - f - out;
              }
              const held = lots.reduce((sum, lot) => sum + lot.n, 0);
              const cost = lots.reduce((sum, lot) => sum + lot.n * lot.unit, 0);
              const values = { realized, cost: change, held, average: held > 0 ? cost / held : 0 };
              if (!(what[i] in values)) throw new Error(`what is "realized", "cost", "held" or "average", not "${what[i]}"`);
              return round(values[what[i]]);
            });
            """,
            "rows")
    ];

    /// <summary>The name the wealth template's trades call for realised gains (Step 4.3b).</summary>
    public const string AverageCost = "avg_cost";
}
