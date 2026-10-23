# Functions, import sources and price sources (admin)

## JavaScript functions

`list_functions`, `get_function` read them; `save_function`, `test_function`, `delete_function` change them (admin). Formulas call them with `CUSTOM("name", …)`.

```json
{
  "name": "risk_class",
  "description": "Risk class from volatility",
  "arguments": [ { "name": "volatility", "type": "float" } ],
  "resultType": "text",
  "code": "if (volatility === null) return null;\nif (volatility > 0.3) return \"high\";\nreturn volatility > 0.15 ? \"medium\" : \"low\";",
  "mode": "row"
}
```

- `name`: small letters, digits, `_`; cannot change later.
- `arguments[].type` and `resultType`: `float`, `integer`, `currency`, `text`, `boolean`, `date`, `datetime`, `any` (a result of `any` is for import and price sources).
- `code` is the body of a JavaScript function whose parameters are the arguments. Dates arrive as `"yyyy-MM-dd"`, empty values as `null`. Return the declared type (`null` = empty).
- Sandbox: no files, no Bento access. Per call: 1 s and 256 MB allocated (rows mode 5 s / 512 MB, network 30 s / 512 MB); freed memory counts too, so keep big loops lean. At most 20 arguments.
- `mode: "rows"`: called once per group with a list per argument; returns a list with one value per row. Used as `CUSTOM_ROWS("name", PARTITION(account_id), ORDER(date, id), amount)` in computed columns only (e.g. running totals, average cost).
- `allowedHosts` + `credentials`: the function may `fetch` those hosts (https) and read `credentials.name`; such functions run as import sources and price sources, not in formulas.
- Use `test_function` with `values` (one per argument; a list per argument in rows mode) before saving.
- Where `CUSTOM` works: a computed column (`CUSTOM("risk_class", volatility)` — stored, recalculated in the background), a dashboard measure, a dashboard variable. Always the whole formula.

## Import sources: any API into a table

A saved import can read from a function instead of a URL: `config.function` in place of `config.url` (`POST /api/v1/import/{table}/sources`, topic `api`). The function takes no argument, or one — when the import last worked (ISO date-time, `null` the first time), to ask only for what is new — and returns `any` (a list of objects, or an object holding one; nested fields become `a.b`) or `text` (CSV). Its result is mapped, keyed, categorised and scheduled like a fetched file. `POST /api/v1/import/function` `{function}` runs it once and returns it as a file for `import/analyze`. A function that reads credentials needs an admin. Templates: "Rows from a JSON API", "Daily weather (Open-Meteo)".

## Price sources

A function `(symbols, from, to) → [{ symbol, date, price, time? }]` plus where the prices go. `list_price_sources` reads them; `run_price_source` (id, kind `latest|history|daily`) fetches now. Config (via `call_api` `POST /api/v1/price-sources` with `{name, enabled, config}`):

```json
{ "function": "yahoo_prices", "table": "prices", "keyColumn": "isin", "dateColumn": "date", "priceColumn": "price",
  "keysFrom": "trades.isin", "symbols": { "IE00BK5BQT80": "VWCE.DE" }, "freshMinutes": 15, "dailyAt": "22:30" }
```

Templates ("Rows from a JSON API", "Daily weather (Open-Meteo)", "Prices from Yahoo Finance", "Xetra symbols for ISINs (OpenFIGI)", average cost, FIFO): `call_api` `GET /api/v1/functions/templates`.
