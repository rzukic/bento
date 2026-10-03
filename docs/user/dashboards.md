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

The range at the top (this month, last 3/6/12 months, this year, last year, all time, or a custom from–to) applies to every widget that has a date: the one it is grouped by, or the one chosen in its settings. The range is part of the address (`?range=last-12-months`), so a bookmark keeps it. Charts by month show every month of the range, also those without bookings; a running total stays level through them.

Widgets refresh when you come back to the tab and with the refresh button; in edit mode you can let the dashboard refresh every 30 seconds, minute or 5 minutes.

## Edit A Dashboard

Choose **Edit**:

- **Add widget**: key figure, line, area or bar chart, donut, table or text
- Drag a widget by its title; resize it by its lower right corner; the others make room
- The gear opens the widget's settings:
  - **Data from**: a table or a saved query
  - **Group by**: the axis or rows (dates per day, week, month, quarter or year) and an optional split into one series per value (e.g. spending per category per month)
  - **Values**: sum, count, average, smallest, largest or distinct values of a column; for sums and counts over dates a **running total**, optionally **including earlier rows** (for balances)
  - **Only rows where**: conditions such as amount < 0
  - **Order** and **Limit** (e.g. the top 10)
  - **Date range filters**: which date column the dashboard range applies to, or none
  - **Display**: amounts, numbers, whole numbers or percent; stacked bars and areas; for key figures the comparison with the period before
- **Shared** makes the dashboard visible to everyone; **Save** keeps the layout and the current date range as the dashboard's default

## Amounts

Amounts (currency columns) are shown in euros in your browser's language, for example `−1.150,00 €`, in tables, key figures and chart axes; negative amounts are red. The Data Explorer shows them the same way.
