# Dashboards (Dashboard JSON)

`list_dashboards` and `get_dashboard` read them; `create_dashboard`, `update_dashboard`, `delete_dashboard` change them (only the owner changes or deletes a dashboard). Look at an existing one before building a new one.

```json
{
  "description": "Where the money goes",
  "dateRange": { "preset": "last-12-months" },
  "variables": [ { "name": "months", "formula": "12" } ],
  "filters": [ { "column": "account_id", "label": "Account" } ],
  "widgets": [
    {
      "id": "spent", "type": "kpi", "title": "Spent in the range",
      "position": { "x": 0, "y": 0, "w": 3, "h": 2 },
      "data": { "query": { "source": "transactions", "measures": [ { "name": "spent", "formula": "-SUM(amount WHERE amount < 0)" } ] }, "dateColumn": "date" },
      "display": { "format": "currency", "compare": true, "higherIsBetter": false }
    },
    {
      "id": "net-per-month", "type": "bar", "title": "Net per month",
      "position": { "x": 0, "y": 2, "w": 8, "h": 5 },
      "data": { "query": { "source": "transactions", "groupBy": [ { "column": "date", "bucket": "month", "as": "month" } ], "measures": [ { "name": "net", "fn": "sum", "column": "amount" } ] }, "dateColumn": "date" },
      "display": { "colorBySign": true }
    }
  ]
}
```

## Layout

A 12-column grid: `position` `{x, y, w, h}` with `x + w ≤ 12`, `h ≤ 24`; key figures are typically `w 3, h 2`, charts `w 6–8, h 4–5`. Widget `id`s are unique slugs (they also name the widget in the address). At most 60 widgets.

## Widget types

| `type` | Shows | Data |
|---|---|---|
| `kpi` | a key figure | one measure; or `data.variable`; second measure = secondary value |
| `line`, `area`, `bar` | charts | first group = horizontal axis (often a date bucket), second group = breakdown; measures = series |
| `donut` | shares | one group, one measure; `display.drill` = a second column to drill into |
| `sankey` | flows | 1–3 groups, one measure; `display.sankey` `levels` or `cashflow` |
| `table` | rows | grouped query, or `data.rows` `{columns, sort, limit}` for raw rows |
| `text` | markdown | `display.text` |
| `budget` | categories vs. monthly limits | `data.budget` `{source, amount, date, category, limit, expenses: "negative"|"positive", period: "month"|"range"}` |

## `data`

- `query` (topic `queries`) or `savedQueryId`.
- `dateColumn`: the column the dashboard's date range filters; without it the widget ignores the range.
- `asOf: true`: values at the end of each period of the date group (balances, net worth).
- `timeShift`: `previous` or `year` — the same series for the period before / a year earlier, dashed.
- `variable` (kpi), `rows` (table), `budget` (budget).
- `range`: the widget's own date range instead of the dashboard's, `{preset}` or `{from, to}` — e.g. a forecast `{"preset": "next-20-years"}` with `asOf` and `BUCKET_END()` in the future, on a dashboard of the last 12 months.

## `display`

| Option | Values | Widgets |
|---|---|---|
| `format` | `auto` (from the column type), `currency`, `number`, `integer`, `percent` (0.22 → 22 %) | all; tables: every measure column |
| `intervals` | `false` hides the viewer's switch (M Q Y), or a list like `["quarter", "year"]`; the widget's own interval always stays | charts and summarized tables by date |
| `stacked`, `totalLine` | true/false; `totalLine` draws the stack's total (the net with mixed signs) | line, area, bar |
| `colorBySign` | true: positive green, negative red | one-series line, area, bar |
| `referenceLines` | `[{value, label?}]`, value a number or a variable's name | charts |
| `annotations` | false hides the dashboard's events | charts by date |
| `compare`, `higherIsBetter`, `trend` | true/false: the change against the period or range before; green when up (false for costs); a small trend line | kpi |
| `goal` | a number or a variable's name, shown as progress | kpi |
| `summary` | true: a total row | table (grouped) |
| `sankey` | `levels` (flows between consecutive groups) or `cashflow` (income categories → income → expense categories and saved) | sankey |
| `shares` | true: node labels as % of the first column | sankey |
| `drill` | a column of the same table to open a slice into | donut |
| `text` | markdown (`#` headings, `-` lists) | text |
| `link` | `{dashboard: id, column, filter?}`: a click on a row opens that dashboard filtered by the row's value of `column` (as its filter `filter`, by default the same column; it applies even without a menu there); the range goes along | table |
| `columns` | per column name (a measure, or a column of `data.rows`): `{format?, signed?, colorBySign?}` — e.g. `{"xirr": {"format": "percent", "signed": true, "colorBySign": true}}` writes +22,2 % in green next to amounts | table |
| `yAxis` | `{min: "auto"}` starts the value axis near the data (prices, values over time), `"zero"` always at 0, or a number | line, area, bar |
| `emptyPeriods` | `zero` (default: an empty period is 0), `gap` (left empty) or `connect` (the line runs over it); with gap and connect the periods before the first value are left out, e.g. a depot before the first purchase | charts by date |

Per measure (in `data.query.measures`, charts only, not sent to the query):
- `show`: `change` (from the period before) or `moving_average`, with `window` (periods, default 3).
- `color`: `blue teal amber violet rose green red gray`.
- `axis`: `right`, a second value axis, e.g. a return next to a value.
- `format`: how that axis and the tooltip write it (`currency number integer percent`).

Saving checks these values: an unknown one (`"sankey": "xyz"`) is an error that names the field.

## Dashboard level

- `dateRange`: `{preset}` with `last-7-days this-month last-3-months last-6-months last-12-months this-year last-year last-3-years last-5-years next-12-months next-5-years next-10-years next-20-years all`, or `{from, to}`. Ranges in the past stop at today; `next-…` presets and a `to` in the future reach ahead (forecasts: charts and `BUCKET_END()` go to the end).
- `variables`: `{name, label?, source?, formula, dateColumn?, input?}` (a key figure showing it writes `label` under the value). With `input` `{kind: "number"|"slider"|"select", min?, max?, step?, options?}` and no `source`, viewers change it above the widgets (an assumption such as a return); `formula` is the default (`0.07`, `"base"`), and the chosen value is in the address (`?var.rate=0.05`). — a measure over `source` rows, or without `source` over other variables (`spent / months`). Widgets use them by name.
- `filters`: `{column, label?}` — a menu narrowing every widget whose table has the column.
- `annotations`: `{date, to?, label, color?}` (`gray blue teal amber rose violet`) on charts by date.
- `refreshSeconds`: 0 (off), 30, 60, 300.
- `quickAdd`: `"auto"` (forms in Quick add for the tables shown), `"none"`, or a list of form ids.

Changes replace the whole document: read it with `get_dashboard`, change it, send it back with `update_dashboard`.

Every change keeps the state before it, marked with this app's name. The user sees it in the dashboard's History and can undo it. If the user asks you to undo something: `GET /api/v1/dashboards/{id}/versions` lists the states, and `POST /api/v1/dashboards/{id}/versions/{versionId}/restore` brings one back (`call_api`).
