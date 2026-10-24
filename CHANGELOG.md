# Changelog

> High-level record of what changed and when.
> Each entry is a short overview — click the working-step link for full detail.
>
> **Format**: newest entries at the top.
> **AI workers**: see [how-to-changelog.md](ai-workers/how-to-changelog.md) before writing here.

---

## Unreleased

_Changes that are complete but not yet part of a versioned release._

## Phase 5 — Integration

### 2026-10-24 — Claude Installs Templates

> **Phase**: Phase 5 — Integration
> **Working step**: [Step 5.15 — Templates On The Server](working-steps/5.15-templates-on-the-server.md)

The templates now live on the server — Claude can install them like the app does.

**Highlights:**
- **"Set up Projects & hours"** from Claude: tables, example rows, dashboards and forms, with a prefix when a name is taken.
- **One recipe, one install** for the app and MCP — the same checks and permissions as by hand.

### 2026-10-24 — MCP Across Workspaces

> **Phase**: Phase 5 — Integration
> **Working step**: [Step 5.13 — One MCP Connection, Several Workspaces](working-steps/5.13-mcp-workspaces.md)

One Claude connection for all your workspaces.

**Highlights:**
- **All workspaces, also new ones** — on the consent page and in a connection's settings.
- **Every tool takes a workspace**; `list_workspaces` shows which ones and your role there.
- **Create workspaces** from Claude (installation admins, new permission); deleting stays in the app.

### 2026-10-23 — Rules On Rows

> **Phase**: Phase 5 — Integration
> **Working step**: [Step 5.12 — Row Events](working-steps/5.12-row-events.md)

Hear about rows as they come: a new sign-up through a form, an urgent order, a status that changed, something deleted.

**Highlights:**
- **Settings → Notifications → Rows are added, changed or deleted** — in a table, only rows where a formula holds, only when certain columns change, only from forms, imports, the API …
- **One message per batch**: an import of 300 rows is one message; a rule speaks at most 20 times an hour.
- **What happened**: the row's values, changes as "open → done", who wrote it, a link to the row.
- **Conditions react to changes** within a minute, not only after imports.

### 2026-10-22 — Any API Into A Table

> **Phase**: Phase 5 — Integration
> **Working step**: [Step 5.11 — Any API Into A Table: Functions As Import Sources](working-steps/5.11-functions-as-import-sources.md)

A saved import can now read from a function: your JavaScript calls any API and returns the rows.

**Highlights:**
- **Import → From a function**: run it, map its rows, save it to run daily or every few hours — with keys, updates and the run log as for a URL.
- **Only what is new**: the function gets the time of the last import that worked.
- **Templates**: "Rows from a JSON API" and "Daily weather (Open-Meteo)".
- **Safe**: functions that read credentials stay with admins; a function in use cannot be deleted.

### 2026-10-22 — Templates

> **Phase**: Phase 5 — Integration
> **Working step**: [Step 5.10 — Templates, And An AI That Knows No Use Case](working-steps/5.10-templates.md)

Ready-made setups for more than money, and an AI that only hears about finance where finance is.

**Highlights:**
- **Four templates**: Projects & hours, Habits & health, Appointments and Wealth & cash flow — tables, a dashboard, forms and example rows in one click, on the home page of an empty workspace and under Dashboards.
- **The AI knows your templates**: MCP clients, *Explain this* and *Describe* learn a workspace's template tables, and the finance conventions only where finance is installed. Existing finance setups are recognised.
- **Neutral examples** in the guide, the AI instructions and the app's hints.
- **Fixes**: row tables show linked records by name; a target's "current" week or month is today's.

### 2026-10-21 — Saved Queries As Tables

> **Phase**: Phase 5 — Integration
> **Working step**: [Step 5.8 — Saved Queries As Tables](working-steps/5.8-saved-queries-as-tables.md)

A saved query's result — monthly spending, hours per project — as a table you can use everywhere.

**Highlights:**
- **Data → New table from a query**: `monthly_spending` from the "Monthly spending" query.
- **Like any table**: the source of other queries and widgets, in formulas and variables, over the API, MCP and share links.
- **Always current**: filled again when the data changed, before anyone reads it; *Refresh now* fills it at once.
- **Read-only**: change the query, and the table follows.

### 2026-10-21 — Public Forms

> **Phase**: Phase 5 — Integration
> **Working step**: [Step 5.6 — Share Links](working-steps/5.6-share-links.md) (part B)

A form anyone can fill in without an account — a booking request on your site, a sign-up, a feedback box.

**Highlights:**
- **Share** on a form: a link (and iframe code) that adds rows and shows nothing of the table.
- **Only what the form asks**: its fields, its link fields' choices by name, hidden values set by Bento.
- **No floods**: a few entries per visitor an hour, a daily cap, a trap for robots.
- **A thank-you text** after each entry; nothing remembered on a shared computer.

### 2026-10-21 — Share Dashboards With A Link

> **Phase**: Phase 5 — Integration
> **Working step**: [Step 5.6 — Share Links](working-steps/5.6-share-links.md) (part A)

A dashboard for people without an account — the tax advisor, family, a page of your own.

**Highlights:**
- **Share** on a dashboard: a link that expires when you want, revoked with a click, opens counted.
- **Read only and nothing more**: visitors pick the period and the interval; Bento answers only what the dashboard's widgets show — no other tables, groupings or dashboards.
- **Fixed filters**: share only "food", whatever the visitor tries.
- **Embed** a dashboard in another site, or forbid it.

### 2026-10-21 — Workspaces: Members, Roles, Users

> **Phase**: Phase 5 — Integration
> **Working step**: [Step 5.5 — Workspaces](working-steps/5.5-workspaces.md) (part B)

Who sees which workspace, and what they may do there.

**Highlights:**
- **Members with a role per workspace**: someone can edit in "Finances", only look in "Health" and not see "Calendar" at all. Installation admins run everything.
- **Users in Settings**: add people with a password and their workspaces, make someone admin, reset a password.
- **Keys and Claude per workspace**: an API key or an MCP connection works only in the workspaces it was made for; Bento's sign-in page asks which.
- **Nothing to redo**: everyone, every key and every connection from before stays in "Default", as it was.
- **Deleting users**: their dashboards, forms and views become yours; their keys and connections stop.
- **Keys for some tables**: a sync script's key that may only touch `transactions`.

### 2026-10-21 — Workspaces: Separate Areas In One Installation

> **Phase**: Phase 5 — Integration
> **Working step**: [Step 5.5 — Workspaces](working-steps/5.5-workspaces.md)

One Bento, several separate areas — finances, calendar & operations, health — each with its own tables, dashboards and settings.

**Highlights:**
- **Own tables per workspace**: a Postgres schema or SQLite file each, so the same table name can live in two workspaces; dashboards, forms, queries, views, functions, imports, rules, reports, apps and settings belong to one.
- **Switch in the sidebar**; every workspace has its own address (`/w/finances/…`). Admins create, rename and delete them in Settings.
- **API and Claude per workspace**: `/api/v1/w/<name>/…` or a header; Claude at `/mcp/<name>` sees only that workspace's tables.
- **Nothing to redo**: everything so far is the workspace "Default", with the same addresses, keys, MCP connections and calendar devices.

### 2026-10-21 — Apps: Your Tables As Calendars On Your Devices

> **Phase**: Phase 5 — Integration
> **Working step**: [Step 5.4 — Apps](working-steps/5.4-apps.md)

Bento opens to other programs. The first app is the calendar: any table becomes a calendar on your iPhone, Mac, Android or in Thunderbird, over CalDAV — the events stay rows in Bento.

**Highlights:**
- **Calendars from tables**: title, start (date for all-day, date-time for timed), end, location, notes, filters, colour — in the workspace's time zone.
- **One password per device**, shown once, with its last use; take one back and only that device stops.
- **Read and write with CalDAV** on iPhone, Mac, Android (DAVx⁵) and Thunderbird: events made, moved or deleted there become rows; alarms and attendees are kept with them.
- **Claude knows** which tables are calendars, so what it adds shows up on your phone.

### 2026-10-11 — Targets Instead Of Budgets, Flows For Any Data

> **Phase**: Phase 5 — Integration (making the core use-case agnostic)

Two widgets only fit money: budgets (spending per category against a monthly limit) and the Sankey's income → expenses. Now they fit any data, and money keeps working as before.

**Highlights:**
- **Targets**: any value per group — hours per project, orders per region, spending per category — against each group's target, per week, month, quarter or year; a goal to reach (green) or a limit to stay under (amber, red).
- **Target rules**: told once per period when a group reaches its target or limit, with the period it is about.
- **Sankey**: between levels by default (one level: the total into its parts); **in → out by sign** with your own names for the centre, the rest and the shortfall.
- **Nothing to redo**: budget widgets, budget rules and money Sankeys become targets, target rules and in → out flows with the money names — dashboards look as they did.

### 2026-10-11 — Your Calendar: Time Zone, Week Start, Fiscal Year

> **Phase**: Phase 5 — Integration (making the core use-case agnostic)

Bento ran on Europe/Berlin and on Monday weeks and January years, wherever you are. Now the workspace says.

**Highlights:**
- **Settings → Workspace**: the time zone, the day weeks start on, the month the year starts in (a fiscal year from April: Q1 2026/27).
- **Everywhere**: `TODAY()` in formulas, the dashboards' date ranges, weekly, quarterly and yearly charts, rule periods, report periods and times, imports and price updates.
- **New date ranges**: this or last week, last month, this or last quarter; rules can watch this quarter.
- **Nothing moves by itself**: an existing workspace keeps Europe/Berlin; a new one starts with the server's time zone.

### 2026-10-11 — Reports: Your Numbers On A Schedule

> **Phase**: Phase 5 — Integration
> **Working step**: [Step 5.3 — Notification Rules And Reports](working-steps/5.3-notification-rules.md)

Every day, week, month, quarter or year, Bento sends the numbers you choose — for any data, not only money.

**Highlights:**
- **Blocks you put together**: a value from any table, a top list (the biggest five of a group), the key figures of any dashboard, the rules that hold, your own text.
- **Against the period before** or a year earlier: "+3 (+33 %) vs. Week 40", green or red as you say what is good.
- **Optional AI sentences** at the top about what stands out, from the report's numbers only, with your instructions.
- **Preview** while editing, **Send now** for a saved one; email, Slack and webhooks (with the plain numbers for scripts).

### 2026-10-11 — Rules: Bento Tells You When Something Needs You

> **Phase**: Phase 5 — Integration
> **Working step**: [Step 5.3 — Notification Rules And Reports](working-steps/5.3-notification-rules.md)

Bento now speaks on its own, over the channels from Settings → Notifications — once when something starts to need you, not every hour.

**Highlights:**
- **A value crosses a line**: a formula over a table, for this month or this week, compared with a number — e.g. spending this month above 2000.
- **A budget runs over**: from a dashboard's budget widget, at 100 % or earlier; each category once a month.
- **Something fails**: a scheduled import or a price update, with the reason.
- **Check now** and **Send test** for every rule; amounts in the workspace's format, links into Bento.

### 2026-10-11 — Bento On The Phone

> **Phase**: Phase 5 — Integration (polish between steps)

The phone layout was hard to use: a strip of every page across the top, cut off at the edge, and a tap on a chart jumped straight to its rows.

**Highlights:**
- **A tab bar at the thumb**: Home, Dashboards, Add (your quick-entry form, or a choice of them), Data and Menu. The menu slides in from the left with every page, the theme and sign-out.
- **Charts**: a tap shows the values at that point, like hovering; a second tap or **Show rows: August 2026 →** opens the rows. Sliding a finger sideways moves along the chart, up and down still scrolls.
- **Dashboards**: key figures two to a row and compact; charts across, no taller than a phone shows comfortably; the controls in one row, History and Edit as icons.
- **Smaller fixes**:
  - the form's Save bar no longer lets fields show beneath it;
  - the data pagination fits the width;
  - the home tiles are compact;
  - editing a dashboard wraps its tools;
  - neutral greys in the import.

### 2026-10-11 — Email, Slack And Webhooks For Notifications

> **Phase**: Phase 5 — Integration
> **Working step**: [Step 5.2 — Notification Channels](working-steps/5.2-notification-channels.md)

Bento can now reach you: admins set up channels under Settings → Notifications and send a test to check them. What Bento says, and when, follows with rules and the monthly review.

**Highlights:**
- **Email** over SMTP (STARTTLS, TLS or a local relay), **Slack** (also Mattermost and Discord), and **webhooks** for n8n or Home Assistant, signed with HMAC-SHA256.
- Passwords and webhook addresses are stored encrypted and never shown again.
- A log per channel says what was sent and why something failed.

### 2026-10-11 — Bank Exports Arrive On Their Own

> **Phase**: Phase 5 — Integration
> **Working step**: [Step 5.1 — Import Extensions](working-steps/5.1-import-extensions.md)

Excel and JSON import like CSV, a booking corrected at the bank is updated instead of skipped, and an export at a URL can be fetched and imported every day without you.

**Highlights:**
- **Excel and JSON**: the right sheet and header are found, dates and formulas read as Excel shows them; JSON lists are found and nested values flattened.
- **Update rows that changed**: the preview shows old → new; only what changed is written, a category set by hand stays.
- **From a URL**: fetched by the server (https, public addresses only), with keys from the vault as `{{name}}` in a header.
- **Saved imports**: daily at a time or every few hours, categorised by your rules, with a run log; failures show under Settings → Problems.

## Phase 4 — Intelligence

### 2026-10-11 — Daily Values Over Years, All Time From The Start

> **Phase**: Phase 4 — Intelligence
> **Release**: [v0.4.0](releases/v0.4.0.md)
> **Working step**: [Step 4.10 — Speed And Beta Feedback](working-steps/4.10-speed-and-beta-feedback.md#speed)

A daily chart over three years said "At most 1000 periods". With the new indexes it no longer needs that limit. And "All time" now really means all time.

**Highlights:**
- Values at period ends take up to 5000 periods: daily over 13 years.
- On SQLite, more than 500 periods no longer fail with an unexpected error.
- A series over time is no longer cut at 1000 rows, which silently dropped its newest days.
- **All time** on depot value, net worth and returns begins with the first trade or booking instead of a year ago.

### 2026-10-10 — Fast Weekly Charts, Results Kept In The Browser

> **Phase**: Phase 4 — Intelligence
> **Release**: [v0.4.0](releases/v0.4.0.md)
> **Working step**: [Step 4.10 — Speed And Beta Feedback](working-steps/4.10-speed-and-beta-feedback.md#speed)

A weekly portfolio chart over three years took over 30 seconds and then failed. It now takes a fraction of a second, and dashboards you have seen show at once.

**Highlights:**
- Bento adds the indexes its formulas need, e.g. for `LOOKUP_ASOF` and related rows: the weekly depot chart went from 30+ s to 0.14 s.
- Results stay in the browser: switching M ↔ Q or going back to a dashboard shows them at once, and nothing is asked twice within 30 s or before data changes.
- A query you switch away from stops in the database too; a timeout says so instead of "unexpected error".
- Admins find slow queries, timeouts and errors under **Settings → Problems**.

### 2026-10-10 — The Beta Feedback, Worked Through

> **Phase**: Phase 4 — Intelligence
> **Release**: [v0.4.0](releases/v0.4.0.md)
> **Working step**: [Step 4.10 — Speed And Beta Feedback](working-steps/4.10-speed-and-beta-feedback.md#package-1--quick-fixes)

21 of 23 points from a beta user who built a finance workspace over MCP and the API.

**Highlights:**
- **Formulas**:
  - `IN`, parentheses after `WHERE`, `LAST`/`FIRST`, `WEEKDAY`, `NPER`, `ROLLING_SUM`/`ROLLING_AVG`, `MIN`/`MAX` on text.
  - JS functions get 1 s and 20 arguments.
- **Dashboards**:
  - Forecasts into the future, a widget's own range.
  - Gaps instead of 0, the value axis near the data.
  - Format, +/− and colour per table column.
  - Assumptions viewers change with a slider.
  - A table row that opens another dashboard.
- **API and MCP**:
  - Schema changes as small operations with compact previews.
  - Up to 1000 rows per call with upsert.
  - API keys with chosen permissions (also schema and functions).
  - Widget options checked and documented.
- **Number format** per workspace and per browser (e.g. `€ 1.250,00` for Austria), with the workspace's currency.

### 2026-10-10 — Polish: Sidebar, Notices, Tables, Loading

> **Phase**: Phase 4 — Intelligence
> **Release**: [v0.4.0](releases/v0.4.0.md)
> **Working step**: [Step 4.10 — Speed And Beta Feedback](working-steps/4.10-speed-and-beta-feedback.md#polish)

**Highlights:**
- Hovering the collapsed sidebar no longer shifts icons, mascot or account.
- Tables scroll with a sticky header.
- The interval switch shows a spinner while it loads.
- Formula widgets wait for the dashboard's variables instead of showing "Column … not found".

### 2026-10-09 — The Interval Switch On Tables, And Per Widget

> **Phase**: Phase 4 — Intelligence
> **Release**: [v0.4.0](releases/v0.4.0.md)
> **Working step**: [Step 4.5 — Budgets & Viewer Controls](working-steps/4.5-budgets.md)

**Highlights:**
- **M Q Y** also on summarized tables by date. Their periods read "Q3 2026", under a column named after the interval.
- Per widget under **Style → Viewers can switch the interval**: hide the switch, or offer only some intervals.

### 2026-10-09 — Describe A Widget Or A Formula, Undo Any Dashboard Change

> **Phase**: Phase 4 — Intelligence
> **Release**: [v0.4.0](releases/v0.4.0.md)
> **Working step**: [Step 4.9 — Describe Instead Of Click](working-steps/4.9-ai-describe.md)

Build by saying what you want, and take back any change, also those made by Claude or ChatGPT.

**Highlights:**
- **✨ in the widget editor**: describe the widget ("expenses by main category per quarter, without rent"). Bento's AI builds it and Bento runs its query before it reaches the editor. Adjust it like any widget, or **Undo**.
- **✨ under every formula field**: describe what it should compute. The AI writes the formula, Bento checks it, and the field previews it.
- **History on every dashboard**: each save keeps the state before it, with who changed it and through which app. **Restore** goes back, and a restore can be undone too. Deleted dashboards can be brought back.
- **Changes by AI apps** on the AI page: what Claude, ChatGPT or a script changed, each with **Undo**.

### 2026-10-09 — Bento In ChatGPT And Codex

> **Phase**: Phase 4 — Intelligence
> **Release**: [v0.4.0](releases/v0.4.0.md)
> **Working step**: [Step 4.6 — MCP Server](working-steps/4.6-mcp-server.md#follow-up-2026-10-09-chatgpt-and-codex)

**Highlights:**
- **ChatGPT** (developer-mode connector) and **Codex** (app, CLI and IDE: `codex mcp add bento --url …`) connect to Bento the way Claude does: sign in with your Bento account, tick what they may do, and use your own subscription.
- Connections → **AI apps** (formerly *Claude*) has the steps for all of them.
- The sign-in page no longer pre-ticks *Delete rows* or *Tables and columns*, even when an app asks for everything.

### 2026-10-09 — Open A Connected App: Change Its Permissions, See Its Calls

> **Phase**: Phase 4 — Intelligence
> **Release**: [v0.4.0](releases/v0.4.0.md)
> **Working step**: [Step 4.6 — MCP Server](working-steps/4.6-mcp-server.md#follow-up-2026-10-09-a-connected-app-opens-in-a-dialog)

**Highlights:**
- Click an app under **Connections → Claude → Connected apps**: tick or untick what it may do and save. It applies to the app's next call, with no need to connect again.
- The same dialog lists **every call that app made**; open a call to see its full arguments and the error.

### 2026-10-09 — Bank Bookings Arrive Categorised

> **Phase**: Phase 4 — Intelligence
> **Release**: [v0.4.0](releases/v0.4.0.md)
> **Working step**: [Step 4.8 — Categorise On Import](working-steps/4.8-ai-categorise.md)

The monthly chore of picking a category for every booking now mostly does itself.

**Highlights:**
- The **import review fills the category**: first your **rules** ("payee contains REWE" → Groceries), then what earlier rows of the **same payee** got, and with **✨ Suggest the rest** Bento's AI for what is left, with how sure it is.
- Each row shows where its value came from, and you can change it. **Your corrections and the AI's sure choices become rules**, so next month those payees need no AI.
- **Categorise** in a table's toolbar does the same for rows already in the table.
- Works for any link or choice column. Rules are listed per table and can be deleted.

### 2026-10-09 — Explain Any Number With Bento's AI

> **Phase**: Phase 4 — Intelligence
> **Release**: [v0.4.0](releases/v0.4.0.md)
> **Working step**: [Step 4.7 — AI Basis & "Explain This"](working-steps/4.7-ai-explain.md)

Ask a dashboard what a number is made of and what changed.

**Highlights:**
- **✨ Explain** on every widget: the AI says what the number is made of, what changed compared with the period before, and which rows drive it, in your language. You can ask follow-up questions.
- It runs **as you, read-only**, on the same tools and guide as MCP. It looks up what it needs and shows the lookups and the cost under each answer.
- The **AI page** lets admins set the Anthropic key (kept in the credential vault), the model (Opus 5.5, Sonnet 5.5, Haiku 5.5), a **monthly limit in US$** and an on switch. Everyone sees every call with its tokens and cost.
- The rest of Phase 4's AI is re-planned around where you work: categorising bank bookings on import (4.8), and describing a widget or formula instead of clicking it together, with undo (4.9).

### 2026-10-09 — Claude Connects To Bento (MCP), With Your Bento Account

> **Phase**: Phase 4 — Intelligence
> **Release**: [v0.4.0](releases/v0.4.0.md)
> **Working step**: [Step 4.6 — MCP Server](working-steps/4.6-mcp-server.md)

Ask your own Claude about your data — in the app, on the web, on the phone or in Claude Code — and let it add a transaction.

**Highlights:**
- Bento is an **MCP server** at `/mcp`: schema, rows, aggregations, saved queries, dashboards and forms to read; with write access, add and change rows (never delete, never the schema)
- **Sign in with your Bento account**: Claude opens Bento's sign-in, you choose *Read only* or *Read and write*; API keys work too
- **Connections → Claude**: the address and the steps per client, connected apps with *Disconnect*, and every call Claude made
- `PUBLIC_URL` for installations behind a proxy; Coolify sets it
- **Claude Desktop extension**: Connections → Claude → *Download bento.mcpb*, with your address built in; it signs in through the browser like the connector
- **Everything over MCP, permission by permission**: Claude can also build dashboards and forms, change the schema (after a preview) and write functions — the sign-in page lists what it may do (add rows, delete rows, dashboards, forms, tables and columns, functions), and Bento checks every call against it
- **A guide for Claude** (`bento_guide`): how Bento works and the exact formats of schema, formulas, queries, dashboards, forms, the finance template and functions

### 2026-10-08 — Budgets, Quarters For Viewers, Subcategories In Donuts

> **Phase**: Phase 4 — Intelligence
> **Release**: [v0.4.0](releases/v0.4.0.md)
> **Working step**: [Step 4.5 — Budgets & Viewer Controls](working-steps/4.5-budgets.md)

Daily use of the cash-flow dashboard: how the month's budgets stand, any chart by quarter or year, and what is inside "Food".

**Highlights:**
- A **Budgets** widget: monthly limits from a column of the categories, spent this month (or in the whole range), bars that turn amber from 80 % and red when over; a row opens its bookings
- **M Q Y** on charts by date for everyone, kept in the address
- Donuts **drill into** a second column: a slice shows its subcategories inside the donut
- Wealth template: main categories, monthly budgets, *Budgets this month* and spending by main category on the cash-flow dashboard

### 2026-10-07 — Quick Entry Forms: Add A Transaction On The Phone

> **Phase**: Phase 4 — Intelligence
> **Release**: [v0.4.0](releases/v0.4.0.md)
> **Working step**: [Step 4.4 — Quick Entry Forms](working-steps/4.4-quick-entry-forms.md)

A form adds a row to a table in a few taps — built for the phone, checked like any other row.

**Highlights:**
- Forms from any table, with a light builder: order, sections, labels, help, hidden fields, defaults (today, now, last used, fixed), a −/+ switch for amounts, live preview
- Phone first: accounts and categories as buttons, Today/Yesterday, the number pad with −/+, recent payees suggested; after saving it starts over for the next entry
- **Quick add** in the sidebar (first on the phone) and buttons on dashboards that show the table; the dashboard reloads after saving
- The Wealth template adds **Add transaction**
- Forms follow table and column renames; `/api/v1/forms` for Form JSON

### 2026-10-07 — Realised Gains And A Fairer Time-Weighted Return

> **Phase**: Phase 4 — Intelligence
> **Release**: [v0.4.0](releases/v0.4.0.md)
> **Working step**: [Step 4.3b — Returns Refinement](working-steps/4.3b-returns-refinement.md)

The Wealth dashboard shows what sales gained, and its time-weighted return no longer depends on the day of the month you buy.

**Highlights:**
- Functions over a group of rows: `CUSTOM_ROWS("name", PARTITION(…), ORDER(…), …)` in computed columns — the function gets a group's rows at once, in order, for running values
- Templates for realised gains by **average cost** and by **FIFO** (realised gain, change in cost, shares held, average cost per trade)
- Wealth template: *Realised gain* and *Cost (±)* per trade, a *Realised gains in the range* key figure, positions with cost and realised gain, and an unrealised gain that stays right after sales
- `TWR(value, FLOWS(…), …)` weighs money by the days it was invested (Modified Dietz)
- How to measure a depot with its own cash (clearing account), so dividends count as return

### 2026-10-06 — Returns: XIRR, Time-Weighted, Benchmarks

> **Phase**: Phase 4 — Intelligence
> **Release**: [v0.4.0](releases/v0.4.0.md)
> **Working step**: [Step 4.3 — Returns & Financial Functions](working-steps/4.3-returns.md)

Dashboards compute what a depot returns, the same on SQLite and Postgres and as Excel does.

**Highlights:**
- `XIRR` and `IRR` of dated cash flows, gathered with `FLOWS(…)` from several tables (trades, dividends) plus today's value
- `TWR(value, flow, 100)` at period ends from the start of the range; `REBASE(measure, 100)` for a benchmark
- `PMT`, `FV`, `PV`, `NPV` in every formula
- A second value axis in charts, with its own format (e.g. value left, return right in percent)
- The Wealth template shows the return a year, the time-weighted return and depot vs. benchmark (an ISIN in the variable *benchmark*)

### 2026-10-06 — Live Prices From An API Of Your Choice

> **Phase**: Phase 4 — Intelligence
> **Release**: [v0.4.0](releases/v0.4.0.md)
> **Working step**: [Step 4.2 — Live Prices](working-steps/4.2-live-prices.md)

Prices come from an API you choose and stay as history in your own table. A dashboard fetches what is stale when it opens and says "Prices as of 14:32".

**Highlights:**
- Functions may call listed hosts with `fetch` (https only, never the server's own network), with API keys from the new credential vault (`credentials.name`, stored encrypted, never shown again)
- **Price sources**:
  - The function, your prices table (e.g. `finance_prices`), where your securities come from, and each one's symbol, with suggestions from a lookup function.
  - Runs: a daily run for closing prices and new securities' history, refresh on dashboard open within a freshness window, a daily request budget, and a log of runs.
- Templates instead of built-in providers: Yahoo Finance (no key) and Xetra symbols for ISINs (OpenFIGI)
- The Functions page gets tabs for price sources and credentials

### 2026-10-05 — Your Own Functions In JavaScript

> **Phase**: Phase 4 — Intelligence
> **Release**: [v0.4.0](releases/v0.4.0.md)
> **Working step**: [Step 4.1 — Custom JS Functions](working-steps/4.1-custom-functions.md)

Write a function once in JavaScript under **Functions** and call it from any formula with `CUSTOM("name", …)`.

**Highlights:**
- Editor with typed arguments, result type, code highlighting, a test run with `console.log`, versions, and where the function is used
- Computed columns store `CUSTOM` values and recalculate them in the background when rows, the function or the schema change; an edited row answers with its new value; stored values filter, sort and sum like other columns
- In dashboards the arguments are measures (`CUSTOM("score", SUM(amount))`) and the function runs on each result; variables too
- Sandboxed (Jint): no files, network or `eval`; 100 ms, 16 MB and a recursion limit per call; failures leave the value empty and are listed
- API: `/api/v1/functions` (list, get, versions, status; admins create, change, test and delete)

---

## Phase 3 — Visualization

### 2026-10-05 — Setup: A Bento Box Of Sushi

> **Phase**: Phase 3 — Visualization
> **Release**: [v0.3.1](releases/v0.3.1.md)

The setup's welcome and done steps show a pixel-art bento box with eight sushi mascots, celebrating once the workspace is ready. A buddy mascot comments on the steps in between and keeps its eyes shut while a password is typed. Motion respects reduced-motion settings; the box shrinks on narrow screens.

### 2026-10-04 — The Collapsed Sidebar Opens On Hover

> **Phase**: Phase 3 — Visualization
> **Release**: [v0.3.0](releases/v0.3.0.md)

A collapsed sidebar opens over the page while the pointer is on it (after a short pause, so passing by does not open it) or the keyboard focus is in it; leaving or Escape closes it. The page beside it does not move, and the collapse stays as chosen. Desktop only; the narrow layout is unchanged. While it is open this way, **Keep sidebar open** sits where the collapse button is when the sidebar is open, and keeps it open without sliding.

### 2026-10-04 — Import: The Same Value For Every Row

> **Phase**: Phase 3 — Visualization
> **Release**: [v0.3.0](releases/v0.3.0.md)
> **Working step**: [Step 3.9 — Wealth & Cash Flow Template](working-steps/3.9-finance-template.md) (follow-up from the v0.3.0 demo run)

A bank export belongs to one account, but has no account column. The import's column choice now offers **Same value for every row…** — a record for a link (the account), or typed text — and a profile keeps it. API: a mapping entry takes `value` instead of `source`.

### 2026-10-04 — Wealth & Cash Flow Template

> **Phase**: Phase 3 — Visualization
> **Release**: [v0.3.0](releases/v0.3.0.md)
> **Working step**: [Step 3.9 — Wealth & Cash Flow Template](working-steps/3.9-finance-template.md)

One click sets up personal finance: accounts, categories, transactions, depot trades, prices and dividends, a cash-flow and a wealth dashboard.

**Highlights:**
- Net worth at each month end (cash + depot × price), depot value, unrealised gain, allocation, positions, dividends
- Cash flow with an account filter; optional example rows; existing tables untouched (a prefix when names are taken)
- Fix: the cash-flow starter splits by a column named like a category before other links (it used the template's account)

### 2026-10-04 — Dashboard Filter Bar

> **Phase**: Phase 3 — Visualization
> **Release**: [v0.3.0](releases/v0.3.0.md)
> **Working step**: [Step 3.8 — Dashboard Filter Bar](working-steps/3.8-filter-bar.md)

Menus above the widgets — an account, a category — narrow the whole dashboard.

**Highlights:**
- Filters on a column, values from the data (record labels), several at once, reset; the choice is in the address
- Applied to every widget, variable and drill-down whose table has the column

### 2026-10-04 — Gaps From The Finanzfluss Copilot Comparison

> **Phase**: Phase 3 — Visualization
> **Release**: [v0.3.0](releases/v0.3.0.md)
> **Working step**: [Step 3.7b — Copilot Gaps](working-steps/3.7b-copilot-gaps.md)

A comparison with the Finanzfluss Copilot dashboard ([report](../docs/research/finanzfluss-copilot-comparison.md)); the easy gaps nothing planned are built, the rest is in the plan's backlog.

**Highlights:**
- Ranges: last 7 days, last 3 years, last 5 years
- A colour per value (green income, red expenses), donut totals and shares, Sankey labels in %
- Backlog: dashboard filter bar, finance template, budget widget, viewer interval switch, second axis, time-series functions for returns

### 2026-10-04 — Sankey & Drill-Down

> **Phase**: Phase 3 — Visualization
> **Release**: [v0.3.0](releases/v0.3.0.md)
> **Working step**: [Step 3.7 — Sankey & Drill-Down](working-steps/3.7-sankey-drilldown.md)

See where the money goes, and click any chart to see the rows behind it.

**Highlights:**
- **Sankey** widget: income → expenses by category with what is saved, or flows between up to three levels (category → subcategory → payee); a **Money flow** Sankey in the generated cash-flow dashboard
- **Drill-down**: a bar, point, slice, Sankey flow or key figure opens the Data Explorer with the widget's conditions, the dashboard range and the clicked values — running totals list every row up to the period's end
- Query API: `rank: "abs"` ranks top values by amount, income and expenses alike

### 2026-10-04 — Chart Extras: Comparisons, Moving Averages, Percentiles

> **Phase**: Phase 3 — Visualization
> **Release**: [v0.3.0](releases/v0.3.0.md)
> **Working step**: [Step 3.6c — Chart Extras](working-steps/3.6c-chart-extras.md)

Charts by date can now compare with last year, smooth a line or show its change, and every value can be a median or a percentile.

**Highlights:**
- **Compare with** the period before or the same period a year earlier (dashed lines, lighter bars)
- **Show as** the change from the period before or a moving average
- **Median** and **percentiles** as quick functions, in the query API (`median`, `percentile`) and in formulas (`MEDIAN`, `PERCENTILE`) — the same numbers on SQLite and Postgres
- A **total line** over stacked charts (the net of income and expenses), **reference lines** at a number or a variable, **colour by sign**
- **Annotations**: events on every chart by date — a day as a line ("Salary raise"), a period as a shaded span ("Vacation") — named in the tooltip
- New dashboard variables start on the dashboard's table and date column; the formula editor's accessible name follows renames; a chart that cannot be drawn no longer takes the page down

### 2026-10-04 — A Widget Editor Like Kibana Lens

> **Phase**: Phase 3 — Visualization
> **Release**: [v0.3.0](releases/v0.3.0.md)
> **Working step**: [Step 3.6b — Widget Editor Like Kibana Lens](working-steps/3.6b-widget-editor.md)

Widgets are now built by dragging fields onto a large live chart instead of filling in a form.

**Highlights:**
- Fields on the left, the chart with suggestions in the middle, chart-specific slots (axis, breakdown, slices, value, rows) with quick functions or formulas on the right
- Top values with "Other" (also in the query API), date intervals, options that no longer apply are dropped
- Key figures for the whole range or the latest period, with secondary value, trend line and goal; tables summarized with a total row or as individual rows
- Drawn in the app's design language (hairlines, pills, mono labels, display type), neutral in the light theme too
- Chart tooltips list the series in the order they sit at that date (stacks top to bottom), mark the hovered line or area, show the stack's total and the period in full ("September 2026"), and stay inside the chart
- Legends stay on one line and page (‹ 1/2 ›) instead of wrapping into the axis; long names are cut and shown in full on hover; "Other" comes last; a breakdown starts with its top 5 values (an axis keeps 10)

### 2026-10-04 — Dashboard Formulas: Savings Rate, Net Worth, Variables

> **Phase**: Phase 3 — Visualization
> **Release**: [v0.3.0](releases/v0.3.0.md)
> **Working step**: [Step 3.6 — Formulas In Widgets & Time Series](working-steps/3.6-formula-measures.md)

Widget values and dashboard variables can be formulas over many rows, and charts can show values at the end of each period.

**Highlights:**
- Formula values: `SUM(amount WHERE amount > 0)`, a savings rate per month with its trend against last month, `RUNNING_SUM(…)`
- "Value at the end of each period": balances and net worth per month with `BUCKET_END()` and `LOOKUP_ASOF` for prices at that time
- Dashboard variables such as `burn_rate` and `runway`, shown in key figures and usable in widget formulas
- `POST /api/v1/query` measures with `formula`, `asOf` and `variables`; `POST /api/v1/query/variables`

### 2026-10-04 — Computed Columns With Live Values

> **Phase**: Phase 3 — Visualization
> **Release**: [v0.3.0](releases/v0.3.0.md)
> **Working step**: [Step 3.5 — Computed Columns](working-steps/3.5-computed-columns.md)

Computed columns now have values everywhere a table is read — computed by the database on every query, never stored.

**Highlights:**
- `= opening_balance + SUM(transactions.amount)`: sums, counts, averages, minimum and maximum over related rows (1:N and N:M), with conditions; values of linked records (`account.name`)
- Computed columns can use other computed columns; circular references are rejected with their path
- Filter, sort, select, export and chart computed columns like stored ones, formatted by their result type
- The formula editor checks against the whole draft and suggests links and the linked table's columns

### 2026-10-04 — Formulas: Checked As You Type, Same On SQLite And Postgres

> **Phase**: Phase 3 — Visualization
> **Release**: [v0.3.0](releases/v0.3.0.md)
> **Working step**: [Step 3.4 — Formula Engine (Core)](working-steps/3.4-formula-engine.md)

Spreadsheet-style formulas such as `= IF(amount < 0, "expense", "income")`, parsed and type-checked on the server and compiled to SQL for both databases.

**Highlights:**
- 34 functions (logic, math, text, dates) with Excel and programmer operators; clear errors with positions and "did you mean"
- A conformance suite of 66 formulas gives identical values on SQLite and Postgres (float noise, umlauts, month ends, UTC date-times)
- `POST /api/v1/formulas/validate` and `/preview`, `GET /api/v1/formulas/functions`
- Formula editor for computed columns in the schema designer: suggestions, underlined errors, result type and a preview of the values

### 2026-10-04 — Connections: API Explorer And API Keys

> **Phase**: Phase 3 — Visualization (API keys pulled forward from Phase 5)
> **Release**: [v0.3.0](releases/v0.3.0.md)
> **Working step**: [Step 3.3b — Connections](working-steps/3.3b-connections.md)

A workspace section for using Bento from scripts and tools: the installation's API, generated from its schema and testable in place, and API keys.

**Highlights:**
- `GET /api/v1/openapi.json`: OpenAPI 3.0 with CRUD per table (fields, required, examples), query, saved queries, dashboards, import, schema, auth; which key scope may call what
- Swagger-style explorer: grouped endpoints, parameters and fields, "Send" as yourself or with a key, response and curl, OpenAPI download
- API keys: read or read-write, optional expiry, shown once and stored as SHA-256, last use, revocation; never above their user, never on admin routes

### 2026-10-04 — Build Dashboards From Your Tables

> **Phase**: Phase 3 — Visualization
> **Release**: [v0.3.0](releases/v0.3.0.md)
> **Working step**: [Step 3.3 — Dashboards & Charts](working-steps/3.3-dashboards.md)

Dashboards with key figures, line, area, bar and donut charts, tables and text, on a 12-column grid that follows one date range.

**Highlights:**
- "Monthly cash flow" starter for tables with a date and an amount: balance, net, spending by category, top payees
- Edit mode with drag, resize and a settings panel per widget (table or saved query, groups, values incl. running totals, conditions, order)
- Date range in the URL, every month of the range on the axis, refresh on return and by interval; private or shared dashboards
- Amounts as currency in the browser's language, also in the Data Explorer grid

### 2026-10-03 — Aggregate Data With The Query API

> **Phase**: Phase 3 — Visualization
> **Release**: [v0.3.0](releases/v0.3.0.md)
> **Working step**: [Step 3.2 — Query API & Saved Queries](working-steps/3.2-query-api.md)

The data layer for dashboards: sums, counts and averages of any table, grouped by columns, linked records or date buckets, with running totals — identical on SQLite and Postgres.

**Highlights:**
- `POST /api/v1/query`: list-endpoint filters, groups (day/week/month/quarter/year buckets, linked records with their names), measures, sort, limit
- Running totals per group, optionally starting from the rows before the date range (balances)
- Saved queries, private or shared, with extra filters at run time; they follow table and column renames
- Fixes Postgres number and yes/no filters on the list endpoint (they were sent as text and failed)

### 2026-10-03 — Create Tables From CSV Files

> **Phase**: Phase 3 — Visualization
> **Release**: [v0.3.0](releases/v0.3.0.md)
> **Working step**: [Step 3.1b — New Table From CSV](working-steps/3.1b-table-from-csv.md)

Drop the first bank export and get the table for it: Bento proposes columns, types and a category table, creates them through the schema checks and continues into the import.

**Highlights:**
- Clean column names from headers (display names keep the original), detected types, amounts as currency, required where nothing is empty
- Category-like columns as a new table of values or a link to an existing table; filled with the distinct values before the import
- Hand-off into the import page with mapping and key columns set and the rows already checked

### 2026-10-03 — Import Bank Exports From CSV

> **Phase**: Phase 3 — Visualization
> **Release**: [v0.3.0](releases/v0.3.0.md)
> **Working step**: [Step 3.1 — CSV Import](working-steps/3.1-csv-import.md)

The first step of the finance-driven Phase 3: CSV files, bank exports included, import into a table, and re-importing an overlapping export adds nothing twice.

**Highlights:**
- Detects encoding, separator, header below account lines, decimal comma and date format; suggests the mapping, also from German bank headers
- Duplicate skip by key columns (counted per key), references by record name, per-row problems with file line, one transaction
- Mapping profiles per table; Import page reachable from the table toolbar
- Fixes Postgres `date` columns: creating such a row through the API returned 500

---

## Phase 2 — Data Management

### 2026-10-03 — Rename the Product to Bento

> **Phase**: Phase 2 — Data Management
> **Release**: [v0.2.1](releases/v0.2.1.md)

The working name "Repère" is replaced by Bento, visibly and in every technical identifier.

**Highlights:**
- UI, page title and docs say Bento; a pixel sushi mascot (a different kind on every load) is the logo and favicon
- `Bento.Api` / `Bento.Tests` projects and namespaces, `Bento.sln`, images `bento/frontend|backend|proxy`, data volume `bento-data`
- Postgres defaults `bento` / `bento_admin` / `bento_system` / `bento_data`; role names configurable via `BENTO_SYSTEM_ROLE` / `BENTO_DATA_ROLE`; env vars `BENTO_SYSTEM_PASSWORD` / `BENTO_DATA_PASSWORD` (formerly `REPERE_*`)
- JWT issuer/audience `bento-api` / `bento-clients`, refresh cookie `bento_refresh`
- Upgrade path: `code/scripts/migrate-volume-from-repere.sh` copies the old data volume; the old refresh cookie is accepted once and replaced; browser settings move from `repere.*` to `bento.*` keys on first load

### 2026-10-03 — Finish Step 2.8: Explorer, Record Editor and Schema Designer Follow-ups

> **Phase**: Phase 2 — Data Management
> **Working step**: [Step 2.8 — UI Language Rollout](working-steps/2.8-ui-language-rollout.md#follow-up-passes-2026-10-02--2026-10-03)

A browser review pass over every surface after the restyle.

**Highlights:**
- Data Explorer: no developer copy, consistent editing without layout shift, portal popovers, floating scrollbars, one list of tables on `/data`, rearrangeable columns, search across referenced records
- Record editor: N:M links as a list for the side column (link, open, remove, edit the link's fields); record labels name records everywhere
- Kanban: full-height drop zones, no faded card after a move, configuration panel in the menu style
- Schema designer: canvas as the stage, inspector on selection, Preview on request, leave guard, red Apply for data loss, toasts
- Backend: every N:M junction table is part of the model and has CRUD routes; search matches referenced records; SQLite default timestamps come back as UTC

### 2026-10-02 — Roll the UI Language Out to the Whole App

> **Phase**: Phase 2 — Data Management
> **Working step**: [Step 2.8 — UI Language Rollout](working-steps/2.8-ui-language-rollout.md)

Closed TASK-154 through TASK-160. The language chosen on the design canvas for the setup wizard now covers the shell, login, Home, the Data Explorer, kanban, the record editor and the schema workbench, in dark and light.

**Highlights:**
- Tokens and primitives: hairlines derived from the ink colour, aurora gradient, display face, pill buttons with an ink-filled `primary` and a blue `accent` reserved for one action per screen, chips, underline fields, hairline panels and dialogs
- Shell: confined light pool and grain, translucent sidebar with the blue position dot, status badges from the setup status, shared segmented theme switch
- Login as the wizard's sibling (shared `Atmosphere` sky and glyph); Home with a greeting hero, outlined-numeral stat cards with real row counts and an empty-workspace entry to the sample data
- Data Explorer and schema workbench chrome restyled as CSS overrides on top of the tokens; grid internals and the id column untouched; record editor becomes a centred dialog
- Contrast pass: quiet text tones raised to ≥ 4.5:1 in both themes; reduced motion respected everywhere

### 2026-10-02 — Stop Rate-Limiting Session Refresh at the Login Limit

> **Phase**: Phase 2 — Data Management
> **Working step**: [Step 2.7 — Setup Wizard & Frontend Packaging](working-steps/2.7-setup-wizard-and-frontend-packaging.md) (follow-up)

Closed TASK-161. Five page loads within a minute no longer sign a user out: refresh and logout have their own per-client limit (default 120/min, `AUTH_SESSION_RATE_LIMIT_PER_MINUTE`) while login, register and setup keep the strict 5/min, and the SPA retries a rate-limited refresh instead of treating it as signed out.

### 2026-10-02 — Ship the Setup Wizard and the Packaged Browser Stack

> **Phase**: Phase 2 — Data Management
> **Working step**: [Step 2.7 — Setup Wizard & Frontend Packaging](working-steps/2.7-setup-wizard-and-frontend-packaging.md)

Closed TASK-144 through TASK-153. `docker compose up` now starts the browser product: a Caddy proxy, the static frontend and the backend, with a first-run setup wizard that creates the administrator and can load a demo workspace. Phase 2 and MVP 2 "Spreadsheet+" are complete; the v0.2.0 release record is prepared.

**Highlights:**
- Public `GET /api/v1/setup/status` and admin-only, one-time `POST /api/v1/setup/sample-data` (embedded demo schema and rows applied through the normal schema and CRUD paths)
- `/setup` wizard (Welcome → Environment → Admin account → Sample data → Done) with a `SetupGate` that redirects uninitialised installations from every route and bootstrap-race handling
- `repere/frontend` (nginx, 14.7 MB) and `repere/proxy` (Caddy, plain-HTTP local mode or automatic HTTPS for `DOMAIN`) images; full-stack `docker-compose.yml` with only the proxy publishing ports, backend-only shape moved to `docker-compose.backend-only.yml`
- Backend proxy awareness: `TRUSTED_PROXIES` forwarded headers, per-client rate limiting with bucket eviction, refresh cookie following the request scheme; `POST /auth/logout` revoking the refresh session
- Packaged MVP 2 smoke through the proxy for SQLite and Postgres in CI plus an executed manual browser checklist; the validation found and fixed a forced-`Secure` cookie, Postgres saved-view binding and non-revoking sign-out
- Docs, Part 7/13, plan re-baseline and the v0.2.0 release record synced

### 2026-10-02 — Harden Step 2.6 Before Merge

> **Phase**: Phase 2 — Data Management
> **Working step**: [Step 2.6 — Schema Designer Visual ERD](working-steps/2.6-schema-designer-visual-erd.md)

Closed TASK-143. The Step 2.6 slice is committed and on PR #18, data routes enforce roles, non-admin users can use the UI, the frontend is covered by CI, and the Postgres and SQLite migration paths lost four correctness bugs found by the pre-merge review.

**Highlights:**
- `/api/v1/data/*` writes require `editor`/`admin`/`api`; schema reads work for every authenticated role while schema mutations stay admin-only
- Frontend typecheck, lint and tests run in CI; ESLint configured; repository freed of tracked build artefacts and presentations
- Postgres: disable-unique on table-created columns, non-PK uuid binding, rollback ordering; SQLite: table rebuilds with existing child rows
- Data Explorer toolbar, inline editor, grid icons and ERD relation routing fixed after a full browser walkthrough

### 2026-05-27 — Ship the Visual Schema Designer

> **Phase**: Phase 2 — Data Management
> **Working step**: [Step 2.6 — Schema Designer Visual ERD](working-steps/2.6-schema-designer-visual-erd.md)

Closed TASK-127 through TASK-138 and completed the Step 2.6 docs/verification pass. Admins can now create and edit schema tables visually, wire relations, persist layout metadata, switch back to canonical JSON, preview backend migration impact, and apply reviewed changes from the browser.

**Highlights:**
- Added the Visual mode ERD canvas on `/model` with table cards, selection, pan/zoom/fit controls, drag-to-reposition layout, color metadata, orthogonal relation lines, and relation-line selection
- Added Visual inspector flows for table/column editing, table create/duplicate/delete/rename, guided `1:N`, self-referential `1:N`, and visible `N:M` relation creation
- Kept Visual and JSON modes on one canonical Model JSON draft, including invalid-JSON blocking, import/export round-trips, layout metadata preservation, and backend diagnostics mapped back to visual targets where possible
- Reused the Step 2.5 preview/apply safety boundary for Visual submissions, including destructive confirmation, no-op previews, failed-apply draft retention, and live schema refresh after apply
- Synced Part 4, user docs, frontend architecture notes, task board, working-step index, and final verification records for Step 2.6 closeout

### 2026-05-27 — Complete Visual Designer Regression Coverage

> **Phase**: Phase 2 — Data Management
> **Working step**: [Step 2.6 — Schema Designer Visual ERD](working-steps/2.6-schema-designer-visual-erd.md)

Closed TASK-137. The visual schema designer now has cross-cutting regression coverage for its draft helpers, JSON/Visual sync boundary, preview/apply review flow, and full create-from-empty workflow.

**Highlights:**
- Added a full Visual mode create -> preview -> apply -> refresh regression that builds two tables, columns, and a `1:N` relation from an empty schema
- Verified the applied visual draft refreshes the JSON editor and frontend schema store from the backend response
- Kept destructive review, no-op previews, backend validation diagnostics, failed-apply draft retention, layout metadata, and invalid-draft coverage in the focused Step 2.6 suite
- Stabilized an unrelated date-picker test so the full frontend verification suite remains deterministic across calendar months

### 2026-05-27 — Wire Visual Preview, Apply, and Diagnostics

> **Phase**: Phase 2 — Data Management
> **Working step**: [Step 2.6 — Schema Designer Visual ERD](working-steps/2.6-schema-designer-visual-erd.md)

Closed TASK-136. Visual schema edits now use the same backend-backed preview, destructive-review, apply, diagnostics, and failed-apply retention flow as JSON mode.

**Highlights:**
- Added Visual-mode preview diagnostics that map backend issue paths to table, column, relation, or canvas targets
- Selecting a visual diagnostic now focuses the closest canvas/inspector object while the existing JSON diagnostic path remains available in the Preview panel
- Extended apply regression coverage so successful apply and failed validation both work while the user is in Visual mode
- Kept destructive confirmation, lossy casts, and no-op preview handling on the shared Step 2.5 review boundary

### 2026-05-27 — Persist Visual Schema Layout Metadata

> **Phase**: Phase 2 — Data Management
> **Working step**: [Step 2.6 — Schema Designer Visual ERD](working-steps/2.6-schema-designer-visual-erd.md)

Closed TASK-135. Visual schema table positions and colors now persist through the shared Model JSON draft and layout-only apply path without requiring a database migration or schema version bump.

**Highlights:**
- Table dragging writes durable `layout` coordinates into the canonical draft and relation routes recompute around visible table cards after layout changes
- Layout colors remain editable from Visual mode, visible on the ERD card border, and preserved through JSON formatting/import/export
- Layout-only previews render a clear no-migration state, while backend apply stores metadata-only changes in place
- Restored the Visual column inspector's reference-target selector so reference columns can be completed from the same editing surface

### 2026-04-21 — Complete the JSON Schema Workbench

> **Phase**: Phase 2 — Data Management
> **Working step**: [Step 2.5 — Schema Designer JSON Workbench](working-steps/2.5-schema-designer-json-workbench.md)

Closed TASK-111 through TASK-121 and completed the Step 2.5 validation/docs-sync pass. Administrators now have a browser JSON schema workbench that loads the live Model JSON, validates drafts, previews migrations with destructive warnings, round-trips files, and applies reviewed changes safely.

**Highlights:**
- Added `POST /api/v1/admin/schema/preview` plus shared backend preview/apply planning so validation, diff, generated SQL, destructive flags, and lossy cast examples come from the real migration path
- Replaced `/model` with an admin-only workbench containing a syntax-highlighted JSON editor, import/export, diagnostics mapped back to source paths, grouped preview summaries, and a safe destructive-confirmation apply modal
- Added a read-only Schema Map beside the editor so valid drafts and backend-normalized previews show tables, columns, relations, and preview-change markers without competing with the Step 2.6 visual designer
- Synced API docs, Part 4 wiki, and project-management records after backend and frontend Step 2.5 regressions passed

### 2026-04-19 — Ship the Full-Screen Record Editor and Sync Step 2.4

> **Phase**: Phase 2 — Data Management
> **Working step**: [Step 2.4 — Data Explorer Editing & Row Operations](working-steps/2.4-data-explorer-editing-and-row-ops.md)

Closed TASK-098 through TASK-102 and completed the Step 2.4 closeout/docs-sync pass. The explorer now has a real full-screen record editor with FK drill-through, relation previews, and a `Go to table` escape hatch, while the Step 2.2 focused-record banner flow is superseded by the modal path.

**Highlights:**
- Added `RecordEditor` as a full-screen editing surface with shared type-aware field editors, per-field validation, loading/error states, and row-expand / Space / kanban / FK open triggers
- Added an in-modal FK navigation stack with back/breadcrumb UI, clickable FK chips, per-entry loading/error isolation, and a persistent `Go to table` action that navigates using the current top-of-stack record
- Added schema-derived relation footer sections with independent loading/error states, up to 5 preview rows, click-to-push drill-ins, and filtered `View all` navigation into the related table
- Added route-query parsing so filtered navigation like relation-section `View all` lands on a normal shareable explorer URL instead of hidden runtime state
- Synced Part 8 and the Step 2.4 working-step record to the shipped behavior, then closed the remaining validation loop with packaged SQLite/Postgres smoke runs plus the live Postgres-tagged backend slice

### 2026-04-18 — Export Explorer Views to CSV and JSON

> **Phase**: Phase 2 — Data Management
> **Working step**: [Step 2.4 — Data Explorer Editing & Row Operations](working-steps/2.4-data-explorer-editing-and-row-ops.md)

Closed TASK-097 by adding a real export path for the current explorer view instead of leaving data trapped in the browser. Users can now export the active filtered/sorted/hidden-column view as CSV or JSON from the main toolbar, with inline progress, cancel, and a safety cap for very large result sets.

**Highlights:**
- Added pure export helpers under `features/data-explorer/export/` for visible-column ordering, CSV/JSON serialization, filename generation, and FK display-value handling
- Added `useExportCurrentView()` to page through the active query with `per_page=1000`, resolve referenced display labels, download via `Blob`, and stop at `100,000` rows with a user-visible truncation notice
- `TableDetailPage` now surfaces an `Export` dropdown beside the other explorer controls and shows inline progress/cancel state while the export is running
- Added serializer unit tests plus an explorer regression proving export follows the active query across multiple pages; full frontend suite green and `npm run typecheck` clean

### 2026-04-18 — Add Undo for Recent Deletes

> **Phase**: Phase 2 — Data Management
> **Working step**: [Step 2.4 — Data Explorer Editing & Row Operations](working-steps/2.4-data-explorer-editing-and-row-ops.md)

Closed TASK-096 by adding a lightweight session-only undo path for recent row deletes. The explorer now treats delete as a recoverable action for a short window: users get an immediate undo toast, Cmd/Ctrl+Z support, and restored rows are recreated through the normal POST path with new primary keys.

**Highlights:**
- Added a capped per-table undo stack in `explorerStore` that records successful single-row and bulk deletes, with the oldest entries dropped after 10 actions
- Undo now recreates deleted rows by POSTing the captured writable payload back to the table and reinserting the recreated rows near their original positions
- `TableDetailPage` now shows a 10-second undo toast, supports Cmd/Ctrl+Z outside editable inputs, and surfaces undo completion/failure through the shared alert primitive
- Added regressions for single-delete undo, bulk undo via keyboard, stack capacity overflow, and undo failure consumption; frontend suite and typecheck green

### 2026-04-18 — Close Bulk Selection and Bulk Delete

> **Phase**: Phase 2 — Data Management
> **Working step**: [Step 2.4 — Data Explorer Editing & Row Operations](working-steps/2.4-data-explorer-editing-and-row-ops.md)

Closed TASK-095 after aligning the task spec to the shipped PK-column selection model instead of a separate checkbox-only column. Bulk delete now finishes the last missing behavior: selected rows are deleted in bounded concurrent batches and the page surfaces a compact multi-row success/partial-failure summary after the operation completes.

**Highlights:**
- Kept the existing selection UX in the primary-key column, including page-scoped select-all, shift-range selection, drag selection, and right-click row selection
- Multi-row delete now runs in small concurrent batches rather than a purely sequential loop, while still reusing the existing optimistic delete + rollback path in `deleteRow`
- Added explorer regressions for bulk-delete success and partial failure, plus a summary banner like `Successfully deleted N/M rows`

### 2026-04-18 — Polish Kanban Cards and FK Labels

> **Phase**: Phase 2 — Data Management
> **Working step**: [Step 2.4 — Data Explorer Editing & Row Operations](working-steps/2.4-data-explorer-editing-and-row-ops.md)

Shipped a small follow-up pass on the new explorer row-ops work so the kanban board feels more deliberate and relation cards match the table grid’s FK display behavior. Kanban cards now resolve the referenced table’s display column and show that human-friendly label instead of a raw foreign-key id.

**Highlights:**
- `KanbanView` now uses the shared FK label-resolution hook already used by the table grid, including the non-clickable fallback path
- Added a targeted regression test proving `orders.customer_id = 7` renders as the referenced customer name on kanban cards
- Tightened the kanban board surfaces with modest lane/card spacing, radius, hover, and drag-handle polish without changing the overall interaction model

### 2026-04-18 — Add Row Creation, Delete, and Duplicate Operations

> **Phase**: Phase 2 — Data Management
> **Working step**: [Step 2.4 — Data Explorer Editing & Row Operations](working-steps/2.4-data-explorer-editing-and-row-ops.md)

Closed TASK-094 and turned the explorer into a real row-entry surface instead of a cell-only editor. Users can now add blank rows, commit the first cell through `POST`, duplicate existing rows, and delete selected rows with confirmation plus optimistic rollback on failure.

**Highlights:**
- Added `createRow` / `deleteRow` API helpers plus new explorer-store actions: `addBlankRow`, `deleteRow`, and `duplicateRow`
- New rows now use a local temp-id lifecycle with `pendingNewRowIds`: toolbar `Add row`, bottom `+` row affordance, first-save `POST`, and automatic local cleanup when an unsaved draft row is abandoned
- Deleting rows now confirms in the UI, removes optimistically, restores on failure, and surfaces the failure inline as a row-level error banner; duplication posts a schema-filtered payload and inserts the saved copy immediately after the source row
- Added right-click row selection, `Ctrl`/`Cmd` shortcuts for add/delete, 4 focused store tests in `rowOperations.test.ts`, 2 grid shortcut tests, full frontend suite green at 150/150, and `npm run typecheck` clean

### 2026-04-18 — Cell PATCH Save with Optimistic Rollback

> **Phase**: Phase 2 — Data Management
> **Working step**: [Step 2.4 — Data Explorer Editing & Row Operations](working-steps/2.4-data-explorer-editing-and-row-ops.md)

Closed the inline-edit loop (TASK-093). Committing a cell now writes the new value optimistically, fires `PATCH /api/v1/data/:table/:id` with the changed field, and reconciles the row from the response — or rolls back to the previous value if the backend refuses.

**Highlights:**
- New `patchRow(table, id, partial)` API helper; new `commitCellEdit` store action wraps the optimistic-apply → PATCH → reconcile/rollback flow
- Field-level 4xx validation errors map onto `cellErrors[rowId][columnName]` (red underline persists on the cell); 5xx / network failures surface a generic message on the edited cell
- No-op commits skip the API entirely; per-cell pending markers tracked via `pendingCellSaves` for future per-cell spinner UI
- 4 new unit tests cover success / no-op / field-validation rollback / 5xx rollback; full suite green at 144/144; typecheck clean

### 2026-04-17 — Wire Inline Edit Mode into the Data Grid

> **Phase**: Phase 2 — Data Management
> **Working step**: [Step 2.4 — Data Explorer Editing & Row Operations](working-steps/2.4-data-explorer-editing-and-row-ops.md)

Wired the type-aware editors into the renamed `DataGrid` (was `ReadOnlyDataGrid`) so users can enter edit mode on a cell, navigate between cells with the keyboard, and see per-cell error indicators (TASK-092). The save round-trip stays in TASK-093 — this slice is the in-grid affordance only.

**Highlights:**
- Click a writable cell, or press Enter / F2 with focus on it, to enter edit mode; the matching editor is picked from `column.type` (relation/references → `ReferenceCellEditor`)
- Tab / Shift+Tab move the edit caret across editable (non-PK) cells, wrapping at row boundaries; Escape cancels without committing
- Store gains `editingCell`, `cellErrors`, plus `startCellEdit` / `cancelCellEdit` / `cellEditCommitted` / `setCellError` actions — `cellEditCommitted` is the seam TASK-093 will plug PATCH into
- Cells with a pending error get a `data-grid__column--has-error` hook for styling
- 8 new unit tests for the grid wiring; full frontend suite green at 140/140; typecheck clean

### 2026-04-17 — Add Reference Cell Editor (FK Chooser)

> **Phase**: Phase 2 — Data Management
> **Working step**: [Step 2.4 — Data Explorer Editing & Row Operations](working-steps/2.4-data-explorer-editing-and-row-ops.md)

Shipped the `ReferenceCellEditor` for FK columns (TASK-091): a searchable combobox that fetches candidates from the referenced table's list endpoint with debounced server-side search and commits the chosen row's primary-key id.

**Highlights:**
- Schema-driven: reads referenced table + PK from `column.references`; resolves a display column heuristically (first non-PK string column → fallback)
- 250ms debounce + capped 20-row page; keyboard nav (Up/Down/Enter), nullable clear via Backspace, Escape cancels
- Reuses the existing `fetchTableRows` API client; no parallel lookup endpoint
- Helpers (`getReferenceTableName`, `getReferencePrimaryKey`, `getDisplayColumnForTable`) live alongside the editor for reuse by the full-screen record editor (TASK-098+)
- 4 new unit tests; full frontend suite green at 132/132; typecheck clean

### 2026-04-17 — Start Step 2.4 with Type-Aware Cell Editor Primitives

> **Phase**: Phase 2 — Data Management
> **Working step**: [Step 2.4 — Data Explorer Editing & Row Operations](working-steps/2.4-data-explorer-editing-and-row-ops.md)

Landed the foundational cell-editor primitives that the inline grid editor (TASK-092) and full-screen record editor (TASK-098) will share. Nine pure-presentation components live under `code/src/frontend/src/features/data-explorer/components/editors/`, all conforming to a single `CellEditorProps<T>` contract.

**Highlights:**
- Editors for `text`, `integer`, `float`, `currency`, `boolean`, `date`, `datetime`, `enum`, `json`; reference editor deferred to TASK-091
- Shared keyboard contract: Enter/blur commit, Escape cancels, Backspace on empty value commits `null` for nullable columns; uniform error-state rendering via `EditorShell`
- Editors are presentation-only — no `explorerStore` or API client imports — so the same components plug into both the grid and the full-screen editor unchanged
- 21 new unit tests in `cellEditors.test.tsx`; full frontend suite green at 128/128; typecheck clean

### 2026-04-17 — Fix Explorer Auto-Save Cross-Table Scoping

> **Phase**: Phase 2 — Data Management
> **Working step**: [Step 2.3 — Views System](working-steps/2.3-views-system.md)

Fixed a Step 2.3 follow-up data-integrity bug (TASK-103) where the saved-view auto-save debounce could persist edits to whichever table was active when the timer fired, instead of the table/view that scheduled the save. The debounce now captures `{ tableId, viewId }` at schedule time and the persist path guards on active-view match and view ownership.

**Highlights:**
- `scheduleAutoSave` captures the intended table/view target; fire-time persists via the new `updateViewConfigFor(tableId, viewId)` action
- Cross-table navigation within the 500ms window no longer drops or mis-targets writes
- Three regression tests cover cross-table navigation, `switchView` cancellation, and foreign-owned-view guarding; all 107 frontend tests green

### 2026-04-16 — Complete Step 2.3 Views System (saved views + kanban)

> **Phase**: Phase 2 — Data Management
> **Working step**: [Step 2.3 — Views System](working-steps/2.3-views-system.md)

Shipped a full saved-views system and kanban view type on top of the Data Explorer, closing out all thirteen Step 2.3 tasks (TASK-075, TASK-077 through TASK-089). The explorer now supports named, shareable, persistent views per table, with a kanban board as the second built-in view type.

**Highlights:**
- **Saved views** — per-table view selector bar; save current query state (search, sort, filters, hidden columns, view type) as a named view; rename, delete, toggle shared, change type, and duplicate on views you own; auto-save mode; backend-backed per-user default-view preferences; dirty-state detection with "Save changes" / "Save to new view" actions
- **Views API** — `system_views` backend table + REST API (`GET /api/v1/views/{tableId}`, `POST`, `PUT /{id}`, `DELETE /{id}`); workspace-wide sharing via `is_shared`; 27 new backend integration tests
- **View-type plugin architecture** — `ViewRenderer` component routes rendering by `type`; unrecognized types fall back to the grid with a console warning; adding a new view type requires only registering it in `VIEW_TYPE_REGISTRY`
- **Kanban view** — groups rows into lanes by any `enum` column in schema-declared order; drag-and-drop between lanes fires `PATCH /api/v1/data/:table/:id` with optimistic update and rollback on failure; configurable primary (bold) + up to 3 secondary card fields; hidden-lane support; "Configure" button in the toolbar replaces "Columns" for kanban views
- **Frontend regression suite** — 16 new tests in `views.test.tsx` covering view selector behaviour, ViewRenderer fallback, kanban lane/card rendering, drag PATCH, and PATCH-failure rollback; all 102 frontend tests green; `npm run typecheck` and `npm run build` clean; 383 backend tests pass

### 2026-04-13 — Refine the Step 2.2 explorer into an Airtable-style workspace

> **Phase**: Phase 2 — Data Management
> **Working step**: [Step 2.2 — Data Explorer Read Path](working-steps/2.2-data-explorer-read-path.md)

Refined the shipped Step 2.2 Data Explorer so the per-table experience now feels much closer to a serious day-to-day data workspace rather than an early technical browser. The table view now centers around selection, floating foldout tools, and user-controlled column visibility while staying on the same backend query contract and read-path scope.

**Highlights:**
- reshaped the per-table explorer into a full-width Airtable-style workspace with compact table tabs, slimmer grid rows, floating foldout controls, and improved header/context-menu layering
- replaced single-record focus as the main table interaction with multi-row selection, select-all, drag/range selection, selection-driven related-record actions, and row context-menu actions
- added first-pass column visibility controls through a shared `Columns` foldout plus `Hide column` in the column header menu, then synced the frontend/wiki/PM docs to the current UX
### 2026-04-13 — Complete Step 2.2 Data Explorer read path

> **Phase**: Phase 2 — Data Management
> **Working step**: [Step 2.2 — Data Explorer Read Path](working-steps/2.2-data-explorer-read-path.md)

Closed the first real browser data-exploration slice so Repere now has a usable end-user data browser on top of the shipped Phase 1 CRUD API. The step now ends with a schema-driven table explorer, live read-only grid, typed query controls, focus mode, and relation-aware browsing, plus synced docs that reflect the actual shipped contract instead of the original placeholder plan.

**Highlights:**
- `/data` and `/data/:tableId` now form a real schema-driven Data Explorer with table discovery, read rendering, search, multi-sort, typed filters, and pagination
- the explorer supports focused-record browsing from the grid, reference jumps into linked rows, and related-record drilldowns derived from the current visible result set
- closed TASK-067 through TASK-072, synced the frontend and Part 8 docs, and kept the frontend validation loop green with `npm run typecheck`, `npm run test`, and `npm run build`

### 2026-04-12 — Add relation-aware Data Explorer navigation

> **Phase**: Phase 2 — Data Management
> **Working step**: [Step 2.2 — Data Explorer Read Path](working-steps/2.2-data-explorer-read-path.md)

Added the first relation-aware browsing layer to the Data Explorer so users can now move through linked records instead of treating each table as an isolated grid. The new flow keeps the same explorer query model and controls while switching between base table browsing, focused referenced rows, and backend relation sub-resource drill-ins.

**Highlights:**
- Reference cells in the grid now open the referenced table in a focused-row context with clear UI explaining where the user came from
- Focused rows expose schema-derived related-record actions that use the existing `/api/v1/data/{table}/{id}/{relationAccessor}` backend surface
- Extended the explorer store/data client to support relation-backed views and verified the browser flow with new relation-navigation regressions plus a clean frontend build

### 2026-04-12 — Complete Step 2.1 frontend shell foundation

> **Phase**: Phase 2 — Data Management
> **Working step**: [Step 2.1 — Frontend Core & SPA Shell](working-steps/2.1-frontend-core-and-spa-shell.md)

Completed the first browser-facing frontend foundation so Repere now has a real SPA shell on top of the shipped backend contract. The Step 2.1 closeout leaves the repo with authenticated navigation, a shared typed client/store layer, and the first reusable theme/component baseline for the upcoming Data Explorer and Schema Designer work.

**Highlights:**
- The frontend shell now uses the real Phase 1 auth flow, including login, silent refresh bootstrap, protected routes, and schema snapshot loading against the live backend
- Added a reusable primitive layer for buttons, fields, panels, alerts, empty states, spinners, and confirm dialogs, plus practical light/dark/system theme switching
- Closed TASK-062 through TASK-066 and verified the frontend loop with `npm run typecheck`, `npm run test`, and `npm run build`

## Phase 1 — Foundation

### 2026-04-10 — Close the remaining Step 1.6 packaging audit follow-ups

> **Phase**: Phase 1 — Foundation
> **Working step**: [Step 1.6 — Docker Packaging & MVP 1 Validation](working-steps/1.6-docker-packaging.md)

Closed the last two Step 1.6 audit tasks by restoring the Postgres dual-pool privilege boundary and tightening the packaged image/scripts around health probing and build-context hygiene. The final validation pass now includes live packaged SQLite and Postgres demos plus the Postgres-tagged `.NET` slice against a real containerized database.

**Highlights:**
- Postgres tenant DDL now runs through the privileged/system pool with `public` first in `search_path`, while `repere_data` no longer has `CREATE` on `public`
- Fresh packaged Postgres databases now auto-grant table and sequence DML access from `repere_system` to `repere_data`, preserving CRUD after the ownership shift
- The runtime image now uses a built-in `--healthcheck` self-probe instead of installing `curl`, the smoke/demo scripts no longer source arbitrary env files, and packaged SQLite/Postgres validation both passed live
### 2026-04-10 — Harden packaged Postgres secrets and host exposure defaults

> **Phase**: Phase 1 — Foundation
> **Working step**: [Step 1.6 — Docker Packaging & MVP 1 Validation](working-steps/1.6-docker-packaging.md)

Closed the third packaging-audit follow-up by removing the packaged Postgres stack’s dangerous defaults. The Postgres override no longer publishes 5432 to the host automatically, application role passwords are no longer baked into source control, and the backend now fails fast if the packaged Postgres secrets are left on the shipped placeholder or the old committed dev values.

**Highlights:**
- `docker-compose.postgres.yml` now requires explicit Postgres secrets, while `docker-compose.postgres.expose.yml` is the only path that publishes Postgres to the host
- the Postgres init script now reads role passwords from environment instead of hard-coding `repere_system_dev` / `repere_data_dev`
- the smoke/demo scripts and deployment workflow now pass explicit Postgres secrets, and the backend startup guard rejects the known insecure defaults

### 2026-04-10 — Trim the public health probe to a minimal contract

> **Phase**: Phase 1 — Foundation
> **Working step**: [Step 1.6 — Docker Packaging & MVP 1 Validation](working-steps/1.6-docker-packaging.md)

Closed the second packaging-audit follow-up by splitting the health contract into a safe anonymous readiness probe and an admin-only detailed diagnostics route. Anonymous callers now get only the information a probe needs, while operators still have a versioned route for runtime details and raw failure messages when they are authenticated.

**Highlights:**
- `GET /api/v1/admin/health` now returns only status, database pool state, version, and a generic `error: "unhealthy"` on failure
- `GET /api/v1/admin/health/details` preserves runtime, uptime, and detailed error information for admin callers
- Added HTTP regressions that prove the anonymous probe does not leak `config_path`, `data_dir`, or raw exception text in either healthy or unhealthy paths

### 2026-04-10 — Harden persisted packaged JWT secret permissions

> **Phase**: Phase 1 — Foundation
> **Working step**: [Step 1.6 — Docker Packaging & MVP 1 Validation](working-steps/1.6-docker-packaging.md)

Closed the first follow-up issue from the Step 1.6 packaging audit by tightening how the packaged runtime persists auto-generated JWT signing secrets. New packaged deployments now create the runtime config with owner-only POSIX permissions, while older over-permissive files are detected and surfaced through a one-time startup warning instead of failing the app.

**Highlights:**
- `PersistentRuntimeConfigBootstrap` now creates `DATA_DIR/config.json` with `0600` on first write and repairs owned runtime data directories to `0700` on POSIX hosts
- Existing over-permissive runtime config files are detected and logged once per process so operators get a migration signal without a breaking startup change
- Added POSIX-gated regression coverage for secure file modes and the warning path, then verified the non-Postgres test suite still passes

### 2026-04-09 — Add Postgres-backed .NET test coverage

> **Phase**: Phase 1 — Foundation
> **Working step**: [Step 1.6 — Docker Packaging & MVP 1 Validation](working-steps/1.6-docker-packaging.md)

Added a true Postgres-backed `.NET` test lane on top of the packaged deployment work so backend behavior can now be exercised against a real PostgreSQL database from inside the normal test project, not only through black-box compose checks. The new slice stays intentionally narrow and provider-aware, which keeps the fast SQLite job quick while adding trustworthy Postgres regression coverage in CI.

**Highlights:**
- Added isolated Postgres test-database infrastructure and a provider-aware `ApiIntegrationTestHost.StartPostgresAsync` path for backend integration tests
- Added a dedicated Postgres-tagged integration slice covering health, auth bootstrap/login, schema apply, CRUD, relation reads, and migration history
- Extended `.github/workflows/test.yml` with a separate `test-postgres` service-container job while keeping packaged deployment validation in its own workflow

### 2026-04-09 — Complete Step 1.6 Docker packaging and deployment validation

> **Phase**: Phase 1 — Foundation
> **Working step**: [Step 1.6 — Docker Packaging & MVP 1 Validation](working-steps/1.6-docker-packaging.md)

Completed the Step 1.6 packaged MVP 1 story so the backend now ships with production compose entrypoints for SQLite and Postgres, reusable local smoke/demo scripts, and a dedicated GitHub Actions deployment workflow that validates both packaged variants. The live packaged Postgres run also surfaced two real deployment bugs, which were fixed before the step was closed.

**Highlights:**
- Added packaged deployment validation scripts for startup/readiness (`compose-smoke.sh`) and the full MVP 1 golden path (`packaged-mvp1-demo.sh`)
- Verified the packaged SQLite and Postgres flows locally end to end, then wired both into a dedicated `deployment-validation.yml` workflow with separate jobs and artifact capture
- Fixed packaged Postgres bootstrap issues by granting `CREATE` on `public` to `repere_data` and generating identity-backed integer primary keys for Postgres tables
### 2026-04-08 — Complete Step 1.5 auto CRUD API and docs sync

> **Phase**: Phase 1 — Foundation
> **Working step**: [Step 1.5 — Auto CRUD API](working-steps/1.5-auto-crud-api.md)

Completed the Step 1.5 MVP 1 data surface so schema-defined tables now expose a live CRUD API with filtering, sorting, pagination, relation traversal, search, and schema-aware validation, backed by admin schema-management endpoints. The follow-up hardening pass closed the audit findings around sparse fieldsets plus includes, relation sub-resource query semantics, PUT edge cases, id-only inserts, and then synced the docs and project state to the finished behavior.

**Highlights:**
- Auto-generated `/api/v1/data/*` CRUD routes are now live for the current Model JSON, including relation sub-resources and include-based traversal
- Admin schema operations (`GET/PUT /api/v1/admin/schema`, migration history, rollback, health) are documented and aligned with the shipped backend contract
- Closed TASK-035 through TASK-045, marked Step 1.5 complete, and verified the final regression pass at `346/346`

### 2026-04-05 — Fix refresh cookie defaults for local auth

> **Phase**: Phase 1 — Foundation
> **Working step**: [Step 1.4 — Basic auth (JWT, login, middleware)](working-steps/1.4-basic-auth.md)

Aligned the Step 1.4 refresh-cookie default with the documented development vs production contract so browser-based refresh works on local HTTP without losing the ability to force secure cookies explicitly. The auth docs and regression suite now cover both the local-development and production-style behaviors.

**Highlights:**
- Refresh-cookie `Secure` now defaults from the host environment when `AUTH_REFRESH_COOKIE_SECURE` is unset
- Added HTTP-level regressions that prove refresh works in development and is withheld on plain HTTP in production-style hosts
- Closed TASK-032 and updated the auth/backend docs to document the effective cookie policy

### 2026-04-05 — Close Step 1.4 auth regression coverage

> **Phase**: Phase 1 — Foundation
> **Working step**: [Step 1.4 — Basic auth (JWT, login, middleware)](working-steps/1.4-basic-auth.md)

Closed the final open Step 1.4 task by expanding the shared HTTP auth integration suite around full request-pipeline behavior instead of isolated service checks. The auth step now has explicit regression coverage for successful admin access, token failure modes, and end-to-end login/refresh contract behavior in both standard and open auth modes.

**Highlights:**
- Added protected-route success coverage for authenticated admin requests using the shared API host test harness
- Added negative auth regressions for invalid credentials plus malformed and expired bearer tokens
- Closed TASK-031 and marked Step 1.4 complete with the backend auth suite staying green

### 2026-04-05 — Harden auth endpoints and cookie/session boundaries

> **Phase**: Phase 1 — Foundation
> **Working step**: [Step 1.4 — Basic auth (JWT, login, middleware)](working-steps/1.4-basic-auth.md)

Completed the Step 1.4 hardening slice so auth endpoints now enforce baseline abuse protection and reject malformed requests using the standard API error contract. The auth docs also now make the browser-only security deferrals explicit instead of leaving them implicit.

**Highlights:**
- Added per-route auth throttling for `register`, `login`, and `refresh` using the configured per-minute limit
- Standardized auth payload validation so malformed or incomplete JSON fails before deeper auth/session logic runs
- Verified refresh-cookie behavior against configurable `HttpOnly`, `Secure`, and `SameSite` flags and documented CSRF/CSP deferrals

### 2026-04-05 — Establish Step 1.4 auth foundation contract

> **Phase**: Phase 1 — Foundation
> **Working step**: [Step 1.4 — Basic auth (JWT, login, middleware)](working-steps/1.4-basic-auth.md)

Locked down the Step 1.4 auth foundation so later slices build against a concrete runtime and route contract instead of placeholders. The backend now exposes a typed `/api/v1/auth` contract endpoint, validates the Phase 1 auth-mode boundary, and documents which security/auth features remain deferred.

**Highlights:**
- Added typed auth runtime settings for mode, JWT metadata, refresh-cookie policy, and auth rate limits
- Registered a concrete `/api/v1/auth` endpoint that reports the current Step 1.4 auth contract and deferrals
- Closed Task 26 and updated the Step 1.4 working step/wiki docs to separate foundation work from later login/middleware implementation

### 2026-04-05 — Complete backend API server foundation and align docs with implemented routes

> **Phase**: Phase 1 — Foundation
> **Working step**: [Step 1.3 — Backend API Server (Routing, Middleware, Error Handling)](working-steps/1.3-backend-api-server.md)

Completed the Step 1.3 backend server foundation on `dev` and cleaned the docs so implemented behavior is clearly separated from planned Phase 1+ features. The API/docs now reflect the real current contract: middleware, runtime config, request tracing, and `/api/v1/admin/health` are real; auth, CRUD, query, and WebSocket pieces are still future work.

**Highlights:**
- Backend host composition, middleware stack, and reserved versioned route areas are implemented in Step 1.3
- `/api/v1/admin/health` is the canonical health endpoint with request-id and operational metadata
- API/backend/architecture docs now explicitly mark auth, CRUD, query, DDL blocklist, and WebSocket behavior as not yet implemented where applicable

### 2026-04-04 — Build the schema engine and migration pipeline

> **Phase**: Phase 1 — Foundation
> **Working step**: [Step 1.2 — Schema engine (Model JSON & migrations)](working-steps/1.2-schema-engine.md)

Built the schema engine that turns Model JSON into a validated, versioned database contract with provider-aware migrations for SQLite and Postgres. This completed the foundation needed for later admin schema operations and auto-generated CRUD work.

**Highlights:**
- Added typed Model JSON parsing, round-trip serialization, stable validation errors, and schema-version persistence
- Added schema diffing, relation resolution, deterministic junction-table generation, and provider-specific migration planning
- Added schema apply/rollback orchestration, startup auto-migrate support, and broad regression coverage for validation, diffing, migrations, and relation metadata

### 2026-04-03 — Core database connection and query builder

> **Phase**: Phase 1 — Foundation
> **Working step**: [Step 1.1 — Core database connection & query builder](working-steps/1.1-core-database-connection-and-query-builder.md)

Starting the foundational database layer — .NET project scaffolding, dual connection pool architecture (system + data pools), query builder abstraction with SqlKata + Dapper, column type system, and namespace isolation with system table bootstrap.

**Highlights:**
- Project scaffolding with ASP.NET Core and all required NuGet dependencies
- Dual connection pool architecture for security isolation (R1.8)
- Query builder wrapping SqlKata + Dapper for Postgres and SQLite
- Column type system with 12 types and cross-dialect mappings
- System table bootstrap with idempotent first-run setup

---

## Released

### 2026-10-11 — Ship v0.4.0: Live Cockpit

> **Version**: [v0.4.0 — Live Cockpit (Phase 4 Checkpoint)](releases/v0.4.0.md)
> **Phase**: Phase 4 — Intelligence

The Finance Cockpit becomes live and personal: prices from an API of your choice, returns (XIRR, time-weighted, benchmarks), your own JavaScript functions, quick entry forms, budgets, and your own AI working with your data over MCP or in the app, with every dashboard change undoable.

**Highlights:**
- Live prices with history; XIRR, TWR and realised gains; custom JS functions and stored values
- Quick entry forms, budgets, the interval switch for viewers
- MCP for Claude, ChatGPT and Codex with Bento sign-in and per-area permissions; explain, categorise and describe in the app
- Dashboard history with restore; speed (indexes, browser cache, cancellation, problem log) and the first beta feedback worked through

### 2026-10-05 — Ship v0.3.1: Setup Mascots

> **Version**: [v0.3.1 — Setup Mascots](releases/v0.3.1.md)
> **Phase**: Phase 3 — Visualization

A patch release that closes MVP 3: the setup wizard greets you with a bento box of sushi mascots. Features are otherwise unchanged from v0.3.0.

### 2026-10-04 — Ship v0.3.0 MVP 3: Finance Cockpit

> **Version**: [v0.3.0 — MVP 3: Finance Cockpit](releases/v0.3.0.md)
> **Phase**: Phase 3 — Visualization

Your own data as live dashboards: import bank and broker CSV exports, aggregate them, and see cash flow, spending and net worth over time, with formulas for computed columns and calculated values.

**Highlights:**
- CSV import of bank exports as they come; a query API with saved queries
- Dashboards with a Lens-style editor: key figures, charts, donuts, tables and Sankey flows; comparisons, moving averages, reference lines, annotations; a filter bar and click-through to the rows
- Excel-like formulas for computed columns and widget values, the same on SQLite and Postgres (`SUM … WHERE`, `RUNNING_SUM`, `BUCKET_END()`, `LOOKUP_ASOF`, `MEDIAN`, `PERCENTILE`)
- A one-click wealth & cash flow template; API explorer and API keys

### 2026-10-03 — Ship v0.2.1: Bento

> **Version**: [v0.2.1 — Bento](releases/v0.2.1.md)
> **Phase**: Phase 2 — Data Management

A patch release that renames the product from the placeholder "Repère" to Bento, in the UI and in every technical identifier, with an upgrade path for installs from v0.2.0 and earlier.

**Highlights:**
- Bento name, page title and pixel sushi mascot (logo and favicon)
- `bento/*` images, `bento-data` volume, `BENTO_*` env vars, `bento_refresh` cookie, `bento.*` browser keys
- `code/scripts/migrate-volume-from-repere.sh` moves the data volume; sessions and browser settings carry over

### 2026-10-03 — Ship v0.2.0 MVP 2: Spreadsheet+

> **Version**: [v0.2.0 — MVP 2: Spreadsheet+](releases/v0.2.0.md)
> **Phase**: Phase 2 — Data Management

The browser product: one `docker compose up` starts a proxy, the web app and the backend, with a setup wizard, the Data Explorer, saved views and kanban boards, and the schema designer.

**Highlights:**
- Packaged full stack (Caddy proxy, static frontend, backend) with a first-run setup wizard
- Spreadsheet-like Data Explorer with inline editing, record editor, saved views, kanban, export
- Schema designer with visual ERD, JSON workbench and migration preview
- One visual language across the app (Step 2.8)

### 2026-04-11 — Ship v0.1.0 MVP 1: Data API

> **Version**: [v0.1.0 — MVP 1: Data API](releases/v0.1.0.md)
> **Phase**: Phase 1 — Foundation

Published the first versioned release note for the completed MVP 1 milestone. This release captures the finished Phase 1 backend/API foundation: schema engine, auth, auto-generated CRUD API, Docker packaging, packaged validation flows, and the initial Postgres-backed regression lane.

**Highlights:**
- MVP 1 is now recorded as the first shippable Repere milestone with a dedicated semver release page
- The release bundles completed Steps 1.1 through 1.6 into a backend-only Dockerized Data API with SQLite and Postgres deployment paths
- Git tagging and artifact publication remain the final operational release-cut steps outside the changelog itself

---

<!--
ENTRY TEMPLATE — copy this block for each new entry, fill it in, delete this comment block.

## [YYYY-MM-DD] Short title of what changed

**Type**: `feature` | `fix` | `refactor` | `docs` | `chore`
**Phase**: Phase N — Name

Brief 2–3 sentence overview of what was built or changed. Focus on the *what* and *why*,
not the implementation detail. The linked working-step page has the full detail.

**Working step**: [YYYY-MM-DD — description](working-steps/YYYY-MM-DD-description.md)
**Tasks closed**: [TASK-001](tasks/TASK-001-description.md), [TASK-002](tasks/TASK-002-description.md)

---
-->
