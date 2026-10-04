# Dashboards

Dashboards show key figures and charts from your tables for any date range. Everyone who can read data can make dashboards of their own and share them; only the owner changes or deletes a dashboard.

## Start From Your Data

Open **Dashboards** in the sidebar. For every table with a date and an amount (a bank account, say), Bento offers **Monthly cash flow**:

- **Balance** — the running total of all amounts, starting from everything before the range, with the change against the month before
- **Net per month** and **Spent in the range**
- **Balance over time** (area) and **Net per month** (bars)
- **Spending by** the table's category column, and **Top spending** by payee

Or type a name and choose **Create** for an empty dashboard.

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

## Click Through To The Rows

Outside edit mode, click a bar, a point, a donut slice, a Sankey node or flow, or a key figure's number: the **Data Explorer** opens on the widget's table with the rows behind it — the widget's conditions, the dashboard's range and what you clicked (the month, the category; for a cash-flow Sankey also income or expenses). Running totals and values at the end of each period list every row up to that period's end. Use the browser's back button to return.

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
