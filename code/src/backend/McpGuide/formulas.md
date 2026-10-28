# Formulas

One language for two places:

- **Row formulas** — computed columns: a value per row from its columns and links. `= amount * -1`, `= IF(amount < 0, "expense", "income")`.
- **Measures** — dashboard widgets, variables, the `aggregate` tool: a value per group of rows (per month, per category). Aggregates work over the group's rows: `SUM(amount WHERE amount < 0)`.

The leading `=` is optional. `check_formula` checks a formula against a table and previews it; `list_formula_functions` lists every function with an example.

## Syntax

| Write | Meaning |
|---|---|
| `amount`, `[Booked on]` | a column, or by display name in brackets |
| `12`, `0.19`, `"text"`, `'text'` | numbers (point as decimal), text |
| `TRUE`, `FALSE`, `NULL` | |
| `+ - * /`, `%`, `^` | arithmetic (`/` never rounds; division by 0 is empty), remainder, power |
| `&` | join text |
| `=`/`==`, `<>`/`!=`, `< <= > >=` | compare (text ignores case) |
| `AND`/`&&`, `OR`/`\|\|`, `NOT`/`!` | logic; group with parentheses, also right after WHERE: `SUM(x WHERE (a OR b) AND c)` |
| `IN`, `NOT IN` | `category IN ("Rent", "Food")` = `category = "Rent" OR category = "Food"` |

Dates: `date + 30` is 30 days later, `date - other_date` is days between; `"2026-01-31"` compares as a date. Amounts stay amounts under + − × ÷ by a number.

## Links in row formulas

- One linked row: `account_id.name`, `category_id.parent_id.name` (empty when nothing is linked).
- Related rows through a 1:N relation, from the parent: `SUM(transactions.amount)`, `COUNT(transactions)`, `MAX(transactions.date)`, `SUM(transactions.amount WHERE transactions.amount < 0)`, `SUMIF(transactions.amount, transactions.amount < 0)`.
- A link is named by the reference column (`account_id`), its link name (`account`), the relation name, or the other table's name when only one link leads there.

## Measures

- Aggregates over the group: `SUM`, `COUNT()`, `AVG`, `MIN`, `MAX` (also text), `MEDIAN`, `PERCENTILE(x, 90)`, `LAST(x, date)`/`FIRST(x, date)` (the value of the latest/earliest row; also over related rows: `LAST(trades.price, trades.date)`), `COUNT_DISTINCT` via `fn`; with `WHERE`: `SUM(amount WHERE amount > 0)`, `COUNT(WHERE category = "Rent")`.
- A row value outside an aggregate is an error: `SUM(amount) * 2` is fine, `amount * 2` is not.
- Related rows inside: `SUM(SUM(transactions.amount))` on accounts.
- `RUNNING_SUM(SUM(amount))`: running total over the date groups. `ROLLING_SUM(SUM(amount), 12)` / `ROLLING_AVG(SUM(amount), 3)`: over the last 12 / 3 periods (rows of periods with data; every period with asOf).
- Values at period ends (query `asOf`, widget `data.asOf`): `BUCKET_START()`, `BUCKET_END()`, e.g. a balance `SUM(amount WHERE date <= BUCKET_END())`.
- `LOOKUP_ASOF(table, match_column, match_value, date_column, date, value_column)`: the latest row on or before a date — a price: `LOOKUP_ASOF("prices", "isin", isin, "date", BUCKET_END(), "price")`. Also in row formulas with `TODAY()`.
- Dashboard variables by name: `SUM(amount) / months`.

## Returns (whole measure only)

- `XIRR(amount, date [WHERE …])` or with flows: `XIRR(FLOWS(-trades.invested, trades.date), FLOWS(dividends.amount, dividends.date), SUM(SUM(trades.value)), TODAY())` — paid out negative, received positive.
- `TWR(value_at_period_end, flow [, 100])`; with dated flows `TWR(value, FLOWS(amount, date), …)` uses Modified Dietz. Needs `asOf` (values at each period end).
- `REBASE(measure [, 100])`: divide by the first value in the range.
- `IRR`, `NPV`, `PMT`, `FV`, `PV`, `NPER(rate, payment, present_value, [future_value])` (periods until a goal) like Excel.

## Functions (summary)

Logic `IF SWITCH COALESCE ISBLANK AND OR NOT`; math `ROUND FLOOR CEIL ABS MOD POWER SQRT`; text `CONCAT LEFT RIGHT MID LEN UPPER LOWER TRIM REPLACE SEARCH CONTAINS`; dates `TODAY` (the workspace's day, in its time zone) `NOW` (UTC) `YEAR MONTH DAY HOUR MINUTE WEEKDAY (1 Monday … 7 Sunday) DATEDIFF(start, end, "days"|"weeks"|"months"|"years") DATEADD(date, n, unit)`; custom `CUSTOM("name", …)` and `CUSTOM_ROWS("name", PARTITION(…), ORDER(…), …)` (topic `functions`).

## Not supported

Returns inside larger formulas (`XIRR(…) * 100`), aggregates over related rows of related rows (add a computed column on the related table and sum it), `CUSTOM` inside a larger formula.
