# Dashboards

Dashboards show key figures and charts from your tables for any date range. Everyone who can read data can make dashboards of their own and share them; only the owner changes or deletes a dashboard.

## Start From Your Data

Open **Dashboards** in the sidebar. For every table with a date and an amount (a bank account, say), Bento offers **Monthly cash flow**:

- **Balance** — the running total of all amounts, starting from everything before the range, with the change against the month before
- **Net per month** and **Spent in the range**
- **Balance over time** (area) and **Net per month** (bars)
- **Spending by** the table's category column, and **Top spending** by payee

Or type a name and choose **Create** for an empty dashboard.

## Wealth & Cash Flow In One Click

Admins see **Wealth & cash flow** under *New dashboard*. **Set up** adds new tables — Accounts, Categories, Transactions, Trades (depot buys and sells with ISIN, shares, price, fees), Prices and Dividends — two dashboards and a form:

- **Cash flow**: the cash-flow dashboard of the new transactions, with an **Account** filter, spending by main category (a slice shows its subcategories) and **Budgets this month**
- **Wealth**:
  - Key figures: net worth, depot value, unrealised gain, dividends, and realised gains in the range.
  - Charts: net worth at each month end (cash and depot stacked, with a total line), allocation by security, positions (shares, cost, value, unrealised and realised gain), dividends per month.
  - Returns: the depot's **return a year (XIRR)** from trades, dividends and today's value, its **time-weighted return** in the range, and **depot vs. benchmark** with both at 100 at the start of the range. The benchmark is the ISIN in the dashboard variable *benchmark* (MSCI World by default); its prices come from the Prices table, e.g. through a [price source](live-prices.md).
  - Gains by **average cost**: each trade gets a realised gain and a change in cost (columns *Realised gain* and *Cost (±)* of Trades), computed by your function `avg_cost` (see [Functions over a group of rows](formulas.md#functions-over-a-group-of-rows)). **Set up** creates it from its template unless you have one. For FIFO, give `avg_cost` the code of the FIFO template.
  - The time-weighted return counts money for the days it was in the depot (Modified Dietz), so a buy in the middle of a month does not distort it.
- **Add transaction**: a [form](forms.md) for quick entry, under *Quick add* in the sidebar and as a button on both dashboards.

Existing tables stay as they are; when a name is taken, the new tables get a prefix (e.g. `finance_transactions`). Categories can have a **main category** (Food → Groceries, Restaurants) and a **monthly budget**; transactions get *Main category* to group by. **Example rows** fills in two accounts, categories with budgets, three months of bookings and this month's first purchase, an ETF with month-end prices, a partial sale and a dividend, so you can see it working — delete them in the Data Explorer when you add your own. Fill the tables with CSV imports: your bank's export into Transactions, your broker's into Trades and Dividends. Add a single purchase with **Add transaction**, also on the phone. Prices come from a [price source](live-prices.md) or by hand.

### A Depot With Its Own Cash

The template's depot holds only securities: a dividend leaves it, so the time-weighted return counts it as money taken out, not as return. Most brokers keep a clearing account (*Verrechnungskonto*) with the depot, where dividends and sale proceeds stay. To measure the depot with its cash, change the measures of **Time-weighted return** and **Depot vs. benchmark** — no new tables needed:

- **Value** at month end: the securities, plus the depot's cash — what you transferred in, minus what trades cost, plus net dividends. Book transfers to the depot in Transactions with the category *Investing* (id 5 in the template; use yours):

  ```
  SUM(SUM(trades.signed_shares * LOOKUP_ASOF("prices", "isin", trades.isin, "date", BUCKET_END(), "price") WHERE trades.date <= BUCKET_END()))
    - SUM(SUM(transactions.amount WHERE transactions.category_id = 5 AND transactions.date <= BUCKET_END()))
    - SUM(SUM(trades.invested WHERE trades.date <= BUCKET_END()))
    + SUM(SUM(dividends.amount - COALESCE(dividends.tax, 0) WHERE dividends.date <= BUCKET_END()))
  ```

- **Money in**: only the transfers, `FLOWS(-transactions.amount, transactions.date WHERE transactions.category_id = 5)`.

Together: `TWR(<value>, FLOWS(-transactions.amount, transactions.date WHERE transactions.category_id = 5), 100)`. Dividends then stay in the depot and count as return; buying and selling within the depot moves money between securities and cash and changes nothing.

## Date Range And Refresh

The range at the top (last 7 days, this month, last 3/6/12 months, this year, last year, last 3 or 5 years, all time, or a custom from–to) applies to every widget that has a date: the one it is grouped by, or the one chosen in its settings. The range is part of the address (`?range=last-12-months`), so a bookmark keeps it. Charts by month show every month of the range, also those without bookings; a running total stays level through them.

**Filters**: in edit mode, **Filters** adds menus above the widgets for a column — an account, a category, a status. Choosing values narrows every widget, variable and drill-down whose table has that column (other widgets stay as they are); several values mean "one of these". The choice is part of the address (`?filter.kategorie_id=2`), so a bookmark keeps it, and **Reset filters** clears it. A widget's own condition on the same column is replaced by the bar's choice.

Widgets refresh when you come back to the tab and with the refresh button; in edit mode you can let the dashboard refresh every 30 seconds, minute or 5 minutes.

## Edit A Dashboard

Choose **Edit**:

- **Add widget**: key figure, line, area or bar chart, donut, Sankey, table or text
- Drag a widget by its title; resize it by its lower right corner; the others make room
- The gear opens the **widget editor** (modelled on Kibana Lens):
  - **Fields** on the left — dates, numbers, categories, text — with search. Drag a field onto the chart or onto a slot, or click its **+**: a date becomes the horizontal axis per month, a number a value (sum), a category the axis or the breakdown
  - **The chart** in the middle, live, with the chart types above it, a sentence saying what it shows ("Sum of Betrag (EUR) by Buchungstag per month, split by Top 5 Kategorie + other"), other ways to show the same fields (**suggestions**) and **View data** for the numbers behind it
  - **Data** on the right: the chart's slots — *Horizontal axis*, *Vertical axis* and *Breakdown* for line, area and bar charts; *Slice by* and *Size by* for donuts; *Value* and *Secondary value* for key figures; *Rows* and *Metrics* for tables. Click an item to set it up:
    - a value: **Count, Sum, Average, Minimum, Maximum, Unique count, Median, Percentile** of a field — or a **Formula** — plus a running total when the axis is a date, a **colour** (green for income and red for expenses follow the theme; with a breakdown the values take the palette), and **Show as**: the value, the **change from the period before**, or a **moving average** over 2–24 periods (the first points stay empty until there are enough)
    - with two or more values on a date axis: **Value axis → Right** puts a value on a second axis with its own scale, **written as** percent, number or amount — e.g. the depot's value on the left and its return on the right
    - a date: the **interval** (day to year)
    - a category: **all values** or the **top 3–50**, largest or smallest first, with the rest as **Other** (a breakdown starts with the top 5)
  - **Only rows where** conditions, and which date the dashboard's range applies to
  - **Compare with** (charts by date without a breakdown): **the period before** or **the same period a year earlier** — each value again for that range, dashed (lighter bars in a bar chart); needs a range with a start and an end
  - **Style**: number format, stacked (with a breakdown) with an optional **total line** (the net when values are positive and negative), **colour by sign** for one series (green above 0, red below), **reference lines** at a number or a variable with a label (a budget, a target), **show annotations** (see below), and per type the options below
- Switching the chart type keeps the fields; options that no longer apply are dropped
- **Shared** makes the dashboard visible to everyone; **Save** keeps the layout and the current date range as the dashboard's default

Donuts show their total in the middle and each slice's share in the tooltip. A Sankey can label its nodes with shares of the first column (for a cash flow, of the income) under **Style → Show shares**.

Tooltips list the series as they sit at that date (stacks top to bottom), mark the one under the pointer and show a stack's total. Stacks put positive values above 0 and negative ones below — for income and expenses, stacked bars with a total line read best.

### Key Figures

- **For**: *the whole range* (one number for all rows in the range, compared with the range before it) or *the latest period* (the last month, compared with the month before)
- **Secondary value**, e.g. the number of bookings under the amount
- **Style**: compare with the period before, **higher is better** (switch off for costs, so a rise is red), **trend line**, and a **goal** — a number or a variable — shown as progress

### Tables

- **Summarized**: rows per group with metrics, optionally a **total row**
- **Individual rows**: the rows themselves — the columns you pick, sorted and limited — with the widget's conditions and the dashboard range

## Sankey: Where The Money Goes

A **Sankey** shows flows. Drop one to three categories into **Levels** and a number into **Size by**, then choose under **Flows**:

- **Income → expenses** (one level, the default): each category's total decides its side — income categories on the left flow into *Income*, which flows out to the expense categories on the right; what is left flows to **Saved** (or comes **From savings** when you spent more). A second level details the expenses (category → subcategory)
- **Between levels**: a flow between each pair of levels, e.g. category → subcategory → payee, sized by the amounts

Levels keep the top 8 values by amount (income and expenses alike) and group the rest as Other. The cash-flow dashboard made from a table includes a **Money flow** Sankey.

## Form Buttons

Editors see buttons such as **+ Add transaction** in a dashboard's header: they open a [form](forms.md) in a dialog, and the dashboard reloads after saving. Which ones, you choose in edit mode under **Quick add**:

- **Automatic** (default): the forms in Quick add for the tables the dashboard shows — as a widget's source, or through a link in a measure.
- **These forms**: the ones you tick, in that order — also forms not in Quick add.
- **None**: no buttons; the sidebar's Quick add stays.

## Months, Quarters, Years

Charts by date show **M Q Y** in their corner, outside edit mode — also for viewers. Switch a chart of monthly bars to quarters or years without changing the dashboard; charts by day or week also offer their own interval. The choice is part of the address (`?interval.net-per-month=quarter`), so a bookmark keeps it. Values at the end of each period follow: net worth per quarter is the value at each quarter's end.

## Budgets

The **Budgets** widget (*Add widget → Budgets*) puts each category's monthly limit against what was spent:

- **Limits** live in a number column of the categories table, e.g. *Monthly budget*. Categories without a limit are left out; what they spent shows as one line under the bars.
- **Spending** comes from a table with a date, an amount and a link to the categories, e.g. Transactions. Bank accounts store spending as negative amounts; an expenses table may store it as positive ones — the widget's settings say which.
- **Period**: the **last month of the dashboard's range** (this month, by default), or the **whole range**, where limits count once per month of the range.
- Each row shows spent of limit, a bar and the share: from 80 % in amber, above 100 % in red. Click a row for its bookings.

The Wealth template's cash-flow dashboard has **Budgets this month**.

## Click Through To The Rows

Outside edit mode, click a bar, a point, a donut slice, a Sankey node or flow, or a key figure's number: the **Data Explorer** opens on the widget's table with the rows behind it — the widget's conditions, the dashboard's range and what you clicked (the month, the category; for a cash-flow Sankey also income or expenses). Running totals and values at the end of each period list every row up to that period's end. Use the browser's back button to return.

**Donuts one level down**: under *Style*, **Drill into** names a second column, e.g. *Category* for a donut by *Main category*. A slice then shows what is in it inside the donut (*All › Food*: Groceries, Restaurants), and a click there opens the rows. **All** goes back.

Not every click has rows to show: *Other* stands for all values outside the top ones, and *Saved* is a difference. Formula values open the rows of their group, not only the ones their `WHERE` picks.

## Annotations

In edit mode, **Annotations** marks events on every chart by date: a day ("Salary raise" — a line in its month, with the label on top) or, with an end date, a period ("Vacation" — a shaded span). Pick a colour per annotation; the tooltip of that period names it. A chart hides them under **Style → Show annotations**. They are saved with the dashboard.

## Values At The End Of Each Period

For a balance or net worth over time, check **Value at the end of each period** under Group by. Each month (or week, quarter, …) of the range then shows the value at its last day, computed over all rows with `BUCKET_END()`:

- balance: `SUM(amount WHERE booked_on <= BUCKET_END())`
- net worth from accounts with a depot: `SUM(opening_balance + SUM(transactions.amount WHERE transactions.booked_on <= BUCKET_END()) + SUM(holdings.shares * LOOKUP_ASOF("prices", "ticker", holdings.ticker, "date", BUCKET_END(), "price") WHERE holdings.bought_on <= BUCKET_END()))`

Switching it on turns the widget's values into such formulas.

## Variables

In edit mode, **Variables** holds named values for the whole dashboard, e.g.:

| Name | From | Formula |
|---|---|---|
| `spent` | Girokonto, date range on Buchungstag | `-SUM(betrag_eur WHERE betrag_eur < 0)` |
| `months` | other variables | `3` |
| `burn_rate` | other variables | `spent / months` |
| `cash` | Girokonto | `SUM(betrag_eur)` |
| `runway` | other variables | `cash / burn_rate` |

Each shows its current value (or what is wrong) as you type. A key figure can show a variable (**Data from** → Dashboard variable), and widget formulas can use variables by name: `SUM(betrag_eur) / months`. Variables are saved with the dashboard.

## Amounts

Amounts (currency columns) are shown in euros in your browser's language, for example `−1.150,00 €`, in tables, key figures and chart axes; negative amounts are red. The Data Explorer shows them the same way.
