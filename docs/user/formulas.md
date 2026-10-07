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
| `MEDIAN(transactions.amount)`, `PERCENTILE(transactions.amount, 90)` | the middle value, or the value 90 % of the rows stay below (interpolated between neighbours) |
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
| Related rows | `SUM`, `COUNT`, `AVG`, `MIN`, `MAX`, `MEDIAN`, `PERCENTILE`, `SUMIF`, `COUNTIF`, `AVGIF` (see above) |
| Lookup | `LOOKUP_ASOF(table, match_column, match_value, date_column, date, value_column)` |
| Dashboards | `RUNNING_SUM(measure)`, `BUCKET_START()`, `BUCKET_END()` (see below) |
| Dates | `TODAY()`, `NOW()`, `YEAR`, `MONTH`, `DAY`, `HOUR`, `MINUTE`, `DATEDIFF(start, end, "days" \| "weeks" \| "months" \| "years")`, `DATEADD(date, count, unit)` |
| Finance | `PMT(rate, periods, present_value, [future_value], [at_start])`, `FV(rate, periods, payment, [present_value], [at_start])`, `PV(rate, periods, payment, [future_value], [at_start])`, `NPV(rate, value1, …)` — like Excel |
| Returns (dashboards) | `XIRR`, `IRR`, `FLOWS`, `TWR`, `REBASE` (see [below](#returns)) |
| Custom | `CUSTOM("name", argument, …)` — your own JavaScript (see [below](#your-own-functions)); `CUSTOM_ROWS("name", PARTITION(…), ORDER(…), …)` over a group of rows (see [below](#functions-over-a-group-of-rows)) |

A few details:

- `ROUND` rounds half away from zero to 0–10 places; `FLOOR` and `CEIL` give whole numbers
- `SEARCH` and `CONTAINS` ignore upper/lower case; `REPLACE` matches exactly
- `DATEDIFF` counts whole units from start to end (negative when end is earlier) and ignores the time of day; Jan 31 to Feb 28 is 0 months
- `DATEADD` keeps month ends in their month: Jan 31 + 1 month is Feb 28, Feb 29 + 1 year is Feb 28
- `TODAY()` and `NOW()` are UTC

The full list with descriptions and examples is at `GET /api/v1/formulas/functions` and in the editor's suggestions.

## Formulas In Dashboards

A widget value or a dashboard variable can be a formula over many rows — a **measure**. There, `SUM`, `COUNT`, `AVG`, `MIN`, `MAX`, `MEDIAN` and `PERCENTILE` work on the rows of each group (each month, each category):

| Write | Gives |
|---|---|
| `SUM(amount WHERE amount > 0)` | income; also `SUMIF(amount, amount > 0)` |
| `COUNT()`, `COUNT(WHERE category = "Miete")` | the number of rows, all or meeting a condition; also `COUNTIF(…)` |
| `(SUM(amount WHERE amount > 0) + SUM(amount WHERE amount < 0)) / SUM(amount WHERE amount > 0)` | a savings rate |
| `MEDIAN(amount WHERE amount < 0)`, `PERCENTILE(amount, 90 WHERE amount < 0)` | a typical expense; a large one (90th percentile) |
| `RUNNING_SUM(SUM(amount))` | a running total over the months, e.g. a balance |
| `SUM(SUM(transactions.amount))` on accounts | an aggregate of related rows inside one over the group |
| `SUM(amount) / months` | with the dashboard variable `months` |

Outside an aggregate a measure cannot use a single row's value: `amount * 2` is an error, `SUM(amount) * 2` is fine. Grouped by month, a key figure compares the last month with the one before.

**By period end** (see [Dashboards](dashboards.md#values-at-the-end-of-each-period)), `BUCKET_START()` and `BUCKET_END()` are the first and last day of each period: `SUM(amount WHERE booked_on <= BUCKET_END())` is the balance at each month end.

**`LOOKUP_ASOF(table, match_column, match_value, date_column, date, value_column)`** finds the latest row on or before a date — a price at a month end: `LOOKUP_ASOF("prices", "ticker", ticker, "date", BUCKET_END(), "price")`. It also works in computed columns, e.g. the price of a holding today with `TODAY()`.

`WHERE` also works in aggregates over related rows: `SUM(transactions.amount WHERE transactions.amount < 0)`.

## Returns

Dashboard measures can compute returns. Each of these is the whole measure; show it as a percentage in the widget.

**`XIRR` — the return a year of dated cash flows**, like Excel: money paid out is negative, money received positive.

| Write | Gives |
|---|---|
| `XIRR(amount, date)` | over the rows of the group, e.g. a table of payments |
| `XIRR(amount, date WHERE kind = "depot")` | only some rows |
| `XIRR(FLOWS(-trades.invested, trades.date), FLOWS(dividends.net, dividends.date), SUM(SUM(trades.value)), TODAY())` | on accounts: money put in from the trades, dividends received, and today's value as the last flow |

`FLOWS(amount, date [WHERE condition])` collects the flows of the group's rows, or with a link those of each row's related rows. A single flow is a pair: an amount (a measure such as `SUM(SUM(trades.value))`) and its date (`TODAY()` or `"2026-12-31"`). `IRR` works the same with flows at equal intervals and gives the rate per period. Without money both paid out and received there is no rate; the widget says so.

**`TWR(value, flow [, 100])` — the time-weighted return**, for widgets with values at the end of each period:
- `value` is the portfolio at each period end. `flow` is the money put in during the period; money taken out, such as dividends paid out, is negative.
- Each period grows by value / (previous value + flow), and the periods multiply. A key figure shows the return in the range; with a third argument the chart shows an index, e.g. 100 at the start.
- The return starts at the end of the period before the range, so the first month counts too.

**With dated flows — `TWR(value, FLOWS(amount, date), … [, 100])`** — money counts only for the days it was in the portfolio (Modified Dietz): 1000 € bought on the 15th of a 31-day month counts as 17/31 of 1000 € at the start of the month. A month's growth is (value − flows + weighted flows) / (previous value + weighted flows). Use this when you buy in the middle of a month; the plain form treats all money as there from the start. The Wealth template uses it:

```
TWR(<depot at month end>, FLOWS(trades.invested, trades.date), FLOWS(COALESCE(dividends.tax, 0) - dividends.amount, dividends.date))
```

**`REBASE(measure [, 100])`** divides by the first value in the range: two lines starting at 100, e.g. the depot and a benchmark's price, `REBASE(LOOKUP_ASOF("prices", "isin", benchmark, "date", BUCKET_END(), "price"))`.

The Wealth template has all three ready (see [Dashboards](dashboards.md#wealth--cash-flow-in-one-click)).

## Your Own Functions

When a formula would get long — a rule with many cases, a calculation in steps, parsing text — write it once in JavaScript under **Functions** (sidebar) and call it from any formula with `CUSTOM("name", …)`. Admins write and change functions; everyone sees them.

A function has a name (small letters, digits and `_`; it cannot change later), arguments with a type, the type it gives, and its code — the body of a JavaScript function whose parameters are the arguments:

```js
// risk_class(volatility: Number) → Text
if (volatility === null) return null;
if (volatility > 0.3) return "high";
return volatility > 0.15 ? "medium" : "low";
```

- Numbers arrive as numbers, text as strings, yes/no as `true`/`false`, dates as `"yyyy-MM-dd"` and date-times as ISO text in UTC (`"2026-10-05T14:30:00Z"`); empty values as `null`. An argument typed **Any value** gets the value as it is.
- Return a value of the declared type: a number, text, `true`/`false`, a `Date` or `"yyyy-MM-dd"` for dates. `null` or `undefined` gives an empty value; anything else is an error ("The function returned text, not a number").
- **Try it** runs the code with values you type, shows the result, `console.log` output and how long it took — nothing is saved.
- Each save with new code is a version; **History** lists them and loads an older one into the editor.

**Where `CUSTOM` works.** It is always the whole formula; build on its result elsewhere.

| Where | Write | What happens |
|---|---|---|
| Computed column | `CUSTOM("risk_class", volatility)` | The value is **stored** and recalculated in the background when the row, the function or the schema changes (and once a minute). Editing a row in the explorer shows the new value at once; after an import it follows within moments. Stored values filter, sort and sum like any column, and other formulas can use them: `UPPER(risk) & "!"`, `COUNT(WHERE risk = "high")`. |
| Dashboard measure | `CUSTOM("score", SUM(amount), COUNT())` | The arguments are measures over each group; the function runs on every result row. |
| Dashboard variable | `CUSTOM("months_left", cash, burn_rate)` | Runs once with the other variables' values. |

When the function fails on a row (it throws, runs too long, returns the wrong type), that value stays empty. The Functions page lists the computed columns with failed rows and the first error; in dashboards the widget names the failure.

**Safety.** Functions run sandboxed on the server: no files, no access to Bento itself, no `eval`. Each call stops after 100 ms, 16 MB of memory or very deep recursion.

**Calling APIs.** A function may list hosts under **Internet**. It then gets `fetch` for those hosts (https only, never the server's own network) and can read stored API keys as `credentials.name`. Such functions run as price sources — see [Live Prices](live-prices.md) — not in formulas, and may take up to 30 seconds.

A function used by a computed column cannot be deleted, and a change to its arguments or result type that would break such a column is refused with the column named.

### Functions Over A Group Of Rows

Some values depend on the rows before them: the average cost of a position after each buy and sale, a running balance per account, a rank within a group. Tick **Runs over a group of rows** in the function's editor. The function then runs once per group: each argument is a **list** with a value per row, in order, and it returns a list with a value per row.

```js
// running_total(amount: Number) → Number, over rows
let sum = 0;
return amount.map((a) => (sum += a ?? 0));
```

Call it in a computed column with `CUSTOM_ROWS`:

```
CUSTOM_ROWS("running_total", PARTITION(account_id), ORDER(date, id), amount)
```

- `PARTITION(…)` puts rows with the same values into one group (here: per account). Without it, all rows are one group.
- `ORDER(…)` sorts a group's rows, ascending, empty values first; the key decides between equal rows.
- The values are stored like those of `CUSTOM`. When a row changes, is added or deleted, its whole group is computed again.
- **Try it** takes a list per argument, e.g. `[10, -5]`.
- Functions over rows work only in computed columns, cannot call the internet, and get 2 seconds per group.

**Templates.** Two templates compute what a sale gained, per security (`PARTITION(account_id, isin)`, `ORDER(date, id)`). Their arguments are shares (+ bought, − sold), price, fees and what to give back:

| `what` | Gives per trade |
|---|---|
| `"realized"` | What a sale gained: proceeds − fees − the cost of the shares sold (0 for buys) |
| `"cost"` | How the trade changes the position's cost: + paid for a buy, − the cost of the shares sold |
| `"held"`, `"average"` | Shares and average cost per share after the trade |

- **Realised gains by average cost** (`avg_cost`): the shares sold cost the average of all held; German banks use it for funds. The Wealth template uses it.
- **Realised gains by FIFO** (`fifo_cost`): the oldest shares go first, as for shares and ETFs in Germany's tax rules.

Example: buy 10 at 100 (+1 fee), buy 10 at 120 (+1), sell 5 at 130 (−2). By average cost the 20 shares cost 2202, 110.10 each, so the sale gains 650 − 2 − 550.50 = **97.50**. By FIFO the 5 shares come from the first buy at 100.10, so it gains **147.50**. `SUM(cost)` over a position is what the shares still held cost, and `SUM(value - cost)` its unrealised gain.

## Not Yet

- formulas in saved queries are not updated when a column they use is renamed (stored columns in saved queries are)
- aggregates over related rows of related rows (add a computed column on the related table and sum that)
- returns inside larger formulas (`XIRR(…) * 100`), `RUNNING_SUM` of a return, top values ranked by a return
- the app's text search does not look into computed columns
- `CUSTOM(…)` inside a larger formula, or summed with `RUNNING_SUM`; top values ranked by a `CUSTOM` measure
- functions in TypeScript
