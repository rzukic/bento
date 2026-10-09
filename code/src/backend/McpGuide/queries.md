# Aggregation queries

The `aggregate` tool, widget queries (`data.query`) and saved queries share one format:

```json
{
  "source": "transactions",
  "filter": { "date": { "gte": "2026-01-01" }, "amount": { "lt": 0 } },
  "groupBy": [
    { "column": "category_id", "top": 5, "other": true, "rank": "asc" },
    { "column": "date", "bucket": "month", "as": "month" }
  ],
  "measures": [
    { "name": "spent", "fn": "sum", "column": "amount" },
    { "name": "bookings", "fn": "count" },
    { "name": "share_income", "formula": "SUM(amount WHERE amount > 0) / SUM(amount)" }
  ],
  "sort": ["month", "-spent"],
  "limit": 500
}
```

- `filter`: per column a value (equals) or operators `eq neq gt gte lt lte like in null notnull`; `in` takes an array.
- `groupBy`: columns, link columns (the result adds `<name>_label`), dates with `bucket` `day|week|month|quarter|year` (the value is the bucket's first day); `as` renames. Per group entry `top` N (ranked by the first measure; `rank` `desc` default, `asc`, `abs`) and `other: true` for the rest.
- `measures`: `{name, fn, column}` with `fn` `count sum avg min max count_distinct median percentile` (`"percentile": 90`), `"running": true` (running total over the date group), `"carryIn": true` (start from rows before the range); or `{name, formula}` (topic `formulas`). Optional `color` for charts.
- `asOf`: `{ "bucket": "month", "from": "2025-01-01", "to": "2026-09-30", "as": "month" }` returns one row per period with measures computed over all rows up to `BUCKET_END()`.
- `variables`: `{ "months": 3 }` usable by name in formulas.
- Result: `{ data: [rows], meta: { columns: [{name, type, role}], truncated, warnings } }`.

Saved queries: `save_query` (name, query, isShared), `run_saved_query` (id, extra filter). Dashboard widgets can use `savedQueryId` instead of their own query.
