# Aggregation queries

The `aggregate` tool, widget queries (`data.query`) and saved queries share one format:

```json
{
  "source": "time_entries",
  "filter": { "date": { "gte": "2026-01-01" }, "hours": { "gt": 0 } },
  "groupBy": [
    { "column": "project_id", "top": 5, "other": true },
    { "column": "date", "bucket": "month", "as": "month" }
  ],
  "measures": [
    { "name": "hours", "fn": "sum", "column": "hours" },
    { "name": "entries", "fn": "count" },
    { "name": "billable_share", "formula": "SUM(hours WHERE status = \"billable\") / SUM(hours)" }
  ],
  "sort": ["month", "-hours"],
  "limit": 500
}
```

- `filter`: per column a value (equals) or operators `eq neq gt gte lt lte like in null notnull`; `in` takes an array.
- `groupBy`: columns, link columns (the result adds `<name>_label`), dates with `bucket` `day|week|month|quarter|year` (the value is the bucket's first day; weeks start on the workspace's first weekday and quarters and years in its first month — Settings → Workspace); `as` renames. Per group entry `top` N (ranked by the first measure; `rank` `desc` default, `asc`, `abs`) and `other: true` for the rest.
- `measures`: `{name, fn, column}` with `fn` `count sum avg min max count_distinct median percentile` (`"percentile": 90`), `"running": true` (running total over the date group), `"carryIn": true` (start from rows before the range); or `{name, formula}` (topic `formulas`). Optional `color` for charts.
- `asOf`: `{ "bucket": "month", "from": "2025-01-01", "to": "2026-09-30", "as": "month" }` returns one row per period with measures computed over all rows up to `BUCKET_END()`. Without `from` ("all time") the periods begin with the earliest date the formulas compare with `BUCKET_END()`/`BUCKET_START()` (e.g. the first trade; not the dates a `LOOKUP_ASOF` searches), or a year before `to`; at most 5000, the newest kept.
- `variables`: `{ "months": 3 }` usable by name in formulas.
- Result: `{ data: [rows], meta: { columns: [{name, type, role}], truncated, warnings } }`.

Saved queries: `save_query` (name, query, isShared), `run_saved_query` (id, extra filter). Dashboard widgets can use `savedQueryId` instead of their own query.
