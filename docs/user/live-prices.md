# Live Prices

Bento keeps prices as history in a table of your own, for example the wealth template's `finance_prices` (ISIN, date, price). Where the prices come from is up to you. A **price source** is one of your [functions](formulas.md#your-own-functions) that asks an API of your choice. Bento runs it:

- **when a dashboard opens or refreshes** and the prices it reads are older than a few minutes; the dashboard then shows "Prices as of 14:32",
- **once a day** for closing prices, and the history of securities that have none yet,
- **on demand** from the price source's page.

Formulas such as `LOOKUP_ASOF("finance_prices", "isin", isin, "date", TODAY(), "price")` then see current prices, and net worth over time keeps its history.

## Set It Up (Wealth Template, Yahoo Finance)

1. **Functions → New → Start from "Prices from Yahoo Finance".** The function takes `(symbols, from, to)` and returns `[{ symbol, date, price, time }]`. **Try it** with `["VWCE.DE"]` and two dates; then **Create function**.
2. **Functions → New → Start from "Xetra symbols for ISINs (OpenFIGI)".** This one suggests Yahoo symbols for your ISINs, e.g. `IE00BK5BQT80` → `VWCE.DE`. **Create function.**
3. **Price sources → Add a price source.**
   - Bento proposes the template's tables: prices into `finance_prices` (isin, date, price), securities from `finance_trades.isin`.
   - Give the source a name and **Create source**.
4. **Suggest symbols**, check them (`.DE` is Xetra), **Save**.
5. **Fetch history** once: a few years of daily closes per security.

From then on the Wealth dashboard fetches today's prices when you open it. Yahoo needs no key. It has no official API either, so the template may need a change one day. That change happens in your function, not in Bento.

## Other Providers

Any API works if your function can reach it with `fetch`:

- **Hosts.** List them under **Internet** in the function's editor, e.g. `www.alphavantage.co`. Requests go only to listed hosts, over https, and never to the server's own network.
- **API keys.** Store them under **Functions → Credentials**. A function that lists a credential reads it as `credentials.name`. Values are stored encrypted, never shown again, and masked in logs and errors.
- **What the function returns:** one row per symbol and day, `{ symbol, date: "yyyy-MM-dd", price }`. Add `time` (ISO) for an intraday quote. Rows for other symbols are ignored, and a day without a price is skipped.
- **Free tiers** allow few requests, e.g. 25 a day. Set **Max requests a day** on the source. Once the limit is used, prices stay as they are until the next day, and the source says so.

Functions that call APIs run only as price sources (and in the editor's test run), not in formulas: a formula is computed for many rows and must not wait on the internet.

## How Prices Are Written

- One row per key (ISIN) and day.
  - An existing row gets the new price.
  - A missing day is added.
  - Rows the provider does not mention stay untouched.
  - Prices are rounded to 4 places.
- **Today's row** changes during the day; the daily run writes the closing price.
- A **failing provider** leaves your prices as they are. The source and the dashboard show "update failed" with the reason.
- **Recent runs** on the source's page list each fetch: kind, requests, rows written, error.

## Settings

| Setting | Meaning |
|---|---|
| Fresh for (minutes) | A dashboard fetches again when the last fetch is older (default 15) |
| Daily run at, Time zone | When closing prices and new securities' history are fetched (default 22:30; the time zone is the workspace's unless the source names its own) |
| History from | How far back to fetch for a new security (default: five years) |
| Securities from | The column listing your securities, e.g. `finance_trades.isin`; you can also just enter symbols |
| Max requests a day | Stops fetching for the day after this many requests |

## API

- `GET /api/v1/price-sources` lists the sources with the latest price per key.
- `POST /api/v1/price-sources/refresh` with `{ "tables": ["finance_prices"] }` fetches stale prices, as a dashboard does.
- Admins create and run sources with `POST /api/v1/price-sources` and `POST /api/v1/price-sources/{id}/run?kind=latest|history|daily`, and manage credentials under `/api/v1/admin/credentials`.
