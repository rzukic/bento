# The Wealth & cash-flow template

Most Bento installations for personal finance use this template (Dashboards → New → Wealth & cash flow). Table names may carry a prefix (`finance_transactions`) when the plain name was taken — check `describe_schema`.

## Tables

| Table | Columns |
|---|---|
| `accounts` | `name`, `kind` (giro, savings, depot, credit, cash), `opened_on`, `opening_balance`, computed `balance = COALESCE(opening_balance, 0) + SUM(transactions.amount)` |
| `categories` | `name`, `kind` (income, expense, savings, transfer), `parent_id` (main category, self-reference), `budget` (monthly limit) |
| `transactions` | `date`, `amount` (**negative = money out**), `payee`, `purpose`, `account_id`, `category_id`, computed `main_category = COALESCE(category_id.parent_id.name, category_id.name)` |
| `trades` | `date`, `isin`, `name`, `kind` (buy, sell), `shares` (positive), `price`, `fees`, `account_id`; computed `signed_shares`, `invested = signed_shares * price + COALESCE(fees, 0)`, `value` (today's value via `LOOKUP_ASOF` on prices), `realized` and `cost_change` (average cost via the function `avg_cost`) |
| `prices` | `isin`, `date`, `price` — history, filled by a price source |
| `dividends` | `date`, `isin`, `name`, `amount`, `tax`, `account_id` |

## Conventions

- Spending is negative, income positive. "Spent" = `-SUM(amount WHERE amount < 0)`.
- A transfer between own accounts is two bookings (−/+) with a transfer category; leave them out of income/expense analyses with a filter on the category's kind.
- Securities are keyed by ISIN; prices by ISIN and date.

## Useful measures

- Net worth: on `accounts`, `SUM(balance + SUM(trades.value))`.
- Balance at each month end (asOf): `SUM(COALESCE(opening_balance, 0) + SUM(transactions.amount WHERE transactions.date <= BUCKET_END()))`.
- Depot at each month end: `SUM(SUM(trades.signed_shares * LOOKUP_ASOF("prices", "isin", trades.isin, "date", BUCKET_END(), "price") WHERE trades.date <= BUCKET_END()))`.
- Unrealised gain: on `trades`, `SUM(value - cost_change)`; realised: `SUM(realized)`.
- XIRR: on `accounts`, `XIRR(FLOWS(-trades.invested, trades.date), FLOWS(dividends.amount - COALESCE(dividends.tax, 0), dividends.date), SUM(SUM(trades.value)), TODAY())`.
- Spending by main category this month: `aggregate` on transactions, filter amount < 0 and the month, group by `main_category`.

## Adding a booking

`create_row` on transactions with `date` (today unless told otherwise), `amount` (negative for spending), `payee`, `account_id` and `category_id` (look both up with `list_rows`; prefer the account used last — the "Add transaction" form's defaults show what the user usually picks).

## Dashboards

**Cash flow** (balance, net per month, spending by main category with drill, budgets this month as a target widget, money flow as a balance Sankey) and **Wealth** (net worth, depot value, gains, allocation, positions, dividends, XIRR, time-weighted return, depot vs. benchmark — the benchmark ISIN is the dashboard variable `benchmark`).
