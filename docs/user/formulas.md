# Formulas

Formulas compute a value from the other columns of a row, like a spreadsheet: `= amount * -1`, `= IF(amount < 0, "expense", "income")`, `= YEAR(booked_on)`. They can read linked records (`account.name`) and add up related rows (`= opening_balance + SUM(transactions.amount)`). They give the same result on SQLite and Postgres.

## Where To Write Them

In the [Schema Designer](schema-designer.md), give a column the type **computed**. The inspector shows a formula editor:

- column names, links and functions are suggested as you type (`Ctrl+Space` opens the list); after `account.` the linked table's columns; `[` suggests columns by their display name, e.g. `[Booked on]`
- the formula is checked as you type against the draft, including tables and links not applied yet: errors are underlined and explained, for example "Column 'amout' not found in Transactions — did you mean 'amount'?"
- a valid formula names its result ("Gives an amount · uses amount") and shows its values on the first rows of the table — once the table and the columns it uses are applied

A formula that does not check, or computed columns that use each other in a circle ("Circular reference: accounts.a → accounts.b → accounts.a"), cannot be applied. The result type is stored with the column (`resultType`) and can be fixed by hand in JSON mode; the formula must then produce it.

## Live Values

Computed columns are never stored: every read computes them from the current data. In the Data Explorer they appear like other columns, read-only (marked "Calculated" in the record editor) and formatted by their result — amounts in euros, dates, yes/no. They can be filtered and sorted, exported, used in dashboard widgets (grouped by, summed) and read through the API like stored columns (`?filter[balance][gt]=0&sort=-balance&fields=name,balance`).

## Writing A Formula

The leading `=` is optional. Spaces and line breaks do not matter.

| Write | Meaning |
|---|---|
| `amount`, `[Booked on]` | a column by name, or by display name in brackets |
| `12`, `0.19`, `"text"`, `'text'` | numbers (with a point), text in double or single quotes (`""` inside double quotes is one quote) |
| `TRUE`, `FALSE`, `NULL` | yes, no, empty |
| `+ - * /` | arithmetic; `/` never rounds (7 / 2 = 3.5) and dividing by 0 gives empty |
| `%`, `^` | remainder of whole numbers, power (`-2^2` is −4) |
| `&` | joins text: `counterparty & " (" & YEAR(booked_on) & ")"`; numbers appear without trailing zeros, empty values as nothing |
| `=` or `==`, `<>` or `!=`, `< <= > >=` | comparisons; text ignores upper/lower case, including umlauts |
| `AND` / `&&`, `OR` / `\|\|`, `NOT` / `!` | combine conditions; also as functions `AND(a, b)` |
| `,` or `;` | separate function arguments |

## Links And Related Rows

A dot follows a link to another table:

- **One linked record** — through a reference column or its link name: `account_id.name`, `account.name`, `category.parent.name`. Empty when nothing is linked.
- **Related rows** — through a 1:N or N:M relation, from the table the others link to. Several rows only go into an aggregate:

| Write | Gives |
|---|---|
| `SUM(transactions.amount)` | the total; 0 when there are none |
| `COUNT(transactions)`, `COUNT(transactions.note)` | the number of rows, or of non-empty values |
| `AVG(transactions.amount)`, `MIN(…)`, `MAX(transactions.booked_on)` | average, smallest, largest (numbers and dates); empty when there are none |
| `SUM(transactions.amount, transactions.amount < 0)` or `SUMIF(…)` | only the rows meeting the condition; also `COUNTIF`, `AVGIF` |
| `SUM(transactions.amount * -1)` | an expression over each related row |

A link is named like in the API: the reference column (`account_id`), its link name (`account`), the relation's name (`account_transactions`) or — when only one link leads there — the other table's name (`transactions`). Inside an aggregate, columns without a link are the current row's: `COUNT(transactions, transactions.amount > limit)`.

Computed columns can use other computed columns, also of linked tables: `SUM(transactions.net)`.

Dates: `booked_on + 30` is 30 days later, `booked_on - other_date` is the number of days between them, and text such as `"2026-01-31"` compared with a date counts as a date. Date-times are read in UTC: a value saved as `2026-03-01T00:15:00+02:00` is `2026-02-28 22:15`.

## Types

Every formula has a result type, which is checked while you type: text, whole number, number, amount (currency), yes/no, date, date and time. Amounts stay amounts when added, subtracted, multiplied or divided by a number; an amount divided by an amount is a plain number (a ratio). Numbers are compared and rounded to 10 decimal places, so `0.1 * 3 = 0.3` is true.

## Functions

| Group | Functions |
|---|---|
| Logic | `IF(condition, then, [else])`, `AND`, `OR`, `NOT`, `SWITCH(value, match1, result1, …, [default])`, `COALESCE(a, b, …)`, `ISBLANK(value)` |
| Math | `ROUND(number, [digits])`, `FLOOR`, `CEIL`, `ABS`, `MOD(number, divisor)`, `POWER(number, exponent)`, `SQRT` |
| Text | `CONCAT(a, b, …)`, `LEFT(text, count)`, `RIGHT(text, count)`, `MID(text, start, count)`, `LEN`, `UPPER`, `LOWER`, `TRIM`, `REPLACE(text, search, replacement)`, `SEARCH(search, text)`, `CONTAINS(text, search)` |
| Related rows | `SUM`, `COUNT`, `AVG`, `MIN`, `MAX`, `SUMIF`, `COUNTIF`, `AVGIF` (see above) |
| Lookup | `LOOKUP_ASOF(table, match_column, match_value, date_column, date, value_column)` |
| Dashboards | `RUNNING_SUM(measure)`, `BUCKET_START()`, `BUCKET_END()` (see below) |
| Dates | `TODAY()`, `NOW()`, `YEAR`, `MONTH`, `DAY`, `HOUR`, `MINUTE`, `DATEDIFF(start, end, "days" \| "weeks" \| "months" \| "years")`, `DATEADD(date, count, unit)` |

A few details:

- `ROUND` rounds half away from zero to 0–10 places; `FLOOR` and `CEIL` give whole numbers
- `SEARCH` and `CONTAINS` ignore upper/lower case; `REPLACE` matches exactly
- `DATEDIFF` counts whole units from start to end (negative when end is earlier) and ignores the time of day; Jan 31 to Feb 28 is 0 months
- `DATEADD` keeps month ends in their month: Jan 31 + 1 month is Feb 28, Feb 29 + 1 year is Feb 28
- `TODAY()` and `NOW()` are UTC

The full list with descriptions and examples is at `GET /api/v1/formulas/functions` and in the editor's suggestions.

## Formulas In Dashboards

A widget value or a dashboard variable can be a formula over many rows — a **measure**. There, `SUM`, `COUNT`, `AVG`, `MIN` and `MAX` work on the rows of each group (each month, each category):

| Write | Gives |
|---|---|
| `SUM(amount WHERE amount > 0)` | income; also `SUMIF(amount, amount > 0)` |
| `COUNT()`, `COUNT(WHERE category = "Miete")` | the number of rows, all or meeting a condition; also `COUNTIF(…)` |
| `(SUM(amount WHERE amount > 0) + SUM(amount WHERE amount < 0)) / SUM(amount WHERE amount > 0)` | a savings rate |
| `RUNNING_SUM(SUM(amount))` | a running total over the months, e.g. a balance |
| `SUM(SUM(transactions.amount))` on accounts | an aggregate of related rows inside one over the group |
| `SUM(amount) / months` | with the dashboard variable `months` |

Outside an aggregate a measure cannot use a single row's value: `amount * 2` is an error, `SUM(amount) * 2` is fine. Grouped by month, a key figure compares the last month with the one before.

**By period end** (see [Dashboards](dashboards.md#values-at-the-end-of-each-period)), `BUCKET_START()` and `BUCKET_END()` are the first and last day of each period: `SUM(amount WHERE booked_on <= BUCKET_END())` is the balance at each month end.

**`LOOKUP_ASOF(table, match_column, match_value, date_column, date, value_column)`** finds the latest row on or before a date — a price at a month end: `LOOKUP_ASOF("prices", "ticker", ticker, "date", BUCKET_END(), "price")`. It also works in computed columns, e.g. the price of a holding today with `TODAY()`.

`WHERE` also works in aggregates over related rows: `SUM(transactions.amount WHERE transactions.amount < 0)`.

## Not Yet

- formulas in saved queries are not updated when a column they use is renamed (stored columns in saved queries are)
- aggregates over related rows of related rows (add a computed column on the related table and sum that)
- the app's text search does not look into computed columns
