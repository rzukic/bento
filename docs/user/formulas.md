# Formulas

Formulas compute a value from the other columns of a row, like a spreadsheet: `= amount * -1`, `= IF(amount < 0, "expense", "income")`, `= YEAR(booked_on)`. They give the same result on SQLite and Postgres.

## Where To Write Them

In the [Schema Designer](schema-designer.md), give a column the type **computed**. The inspector shows a formula editor:

- column names and functions are suggested as you type (`Ctrl+Space` opens the list); `[` suggests columns by their display name, e.g. `[Booked on]`
- the formula is checked as you type: errors are underlined and explained, for example "Column 'amout' not found in Transactions — did you mean 'amount'?"
- a valid formula names its result ("Gives an amount · uses amount") and shows its values on the first rows of the table — once the table and the columns it uses are applied

Computed columns are saved with the schema. Their values appear in the Data Explorer with the next step (3.5); until then the editor's preview shows them.

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

Dates: `booked_on + 30` is 30 days later, `booked_on - other_date` is the number of days between them, and text such as `"2026-01-31"` compared with a date counts as a date. Date-times are read in UTC: a value saved as `2026-03-01T00:15:00+02:00` is `2026-02-28 22:15`.

## Types

Every formula has a result type, which is checked while you type: text, whole number, number, amount (currency), yes/no, date, date and time. Amounts stay amounts when added, subtracted, multiplied or divided by a number; an amount divided by an amount is a plain number (a ratio). Numbers are compared and rounded to 10 decimal places, so `0.1 * 3 = 0.3` is true.

## Functions

| Group | Functions |
|---|---|
| Logic | `IF(condition, then, [else])`, `AND`, `OR`, `NOT`, `SWITCH(value, match1, result1, …, [default])`, `COALESCE(a, b, …)`, `ISBLANK(value)` |
| Math | `ROUND(number, [digits])`, `FLOOR`, `CEIL`, `ABS`, `MOD(number, divisor)`, `POWER(number, exponent)`, `SQRT` |
| Text | `CONCAT(a, b, …)`, `LEFT(text, count)`, `RIGHT(text, count)`, `MID(text, start, count)`, `LEN`, `UPPER`, `LOWER`, `TRIM`, `REPLACE(text, search, replacement)`, `SEARCH(search, text)`, `CONTAINS(text, search)` |
| Dates | `TODAY()`, `NOW()`, `YEAR`, `MONTH`, `DAY`, `HOUR`, `MINUTE`, `DATEDIFF(start, end, "days" \| "weeks" \| "months" \| "years")`, `DATEADD(date, count, unit)` |

A few details:

- `ROUND` rounds half away from zero to 0–10 places; `FLOOR` and `CEIL` give whole numbers
- `SEARCH` and `CONTAINS` ignore upper/lower case; `REPLACE` matches exactly
- `DATEDIFF` counts whole units from start to end (negative when end is earlier) and ignores the time of day; Jan 31 to Feb 28 is 0 months
- `DATEADD` keeps month ends in their month: Jan 31 + 1 month is Feb 28, Feb 29 + 1 year is Feb 28
- `TODAY()` and `NOW()` are UTC

The full list with descriptions and examples is at `GET /api/v1/formulas/functions` and in the editor's suggestions.

## Not Yet

- values of related records (`orders.total`) and sums over them (`SUM(transactions.amount)`) — step 3.5
- formulas in dashboard widgets (`SUM(amount WHERE amount > 0)`), running sums and as-of lookups — step 3.6
- computed columns that use other computed columns
