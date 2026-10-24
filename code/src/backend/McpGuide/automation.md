# Rules and reports (permission automation.write)

Bento speaks on its own over the installation's channels (e-mail, Slack, webhook). Rules and reports belong to a workspace; read them with `call_api` `GET /api/v1/admin/notifications/rules`. The channels to speak on, by name only: `GET /api/v1/admin/notifications/rules/channels` → `[{id, name, kind}]` (channels themselves are set up in the app, Settings → Notifications).

Create: `POST /api/v1/admin/notifications/rules` `{name, kind, config, channels: [ids], enabled?}`; change: `PUT …/rules/{id}` (the kind stays); delete: `DELETE …/rules/{id}` (ask first). `POST …/rules/{id}/check` checks now; `POST …/rules/{id}/test` sends a test message — tell the user before, it reaches real people.

## Kinds

- **condition** — a number crosses a line, checked hourly (and soon after data changes) or daily:
  `{ "source": "time_entries", "formula": "SUM(hours)", "period": "this-week", "dateColumn": "date", "op": "<", "threshold": 20, "every": "day", "at": "17:00" }`
  `period`: `today this-week this-month this-quarter this-year` or leave out for all rows; `op`: `> >= < <= = <>`; `every`: `hour` (default) or `day` with `at` (HH:mm). It speaks once when it starts to hold, again only after it cleared.
- **target** — groups of a dashboard's target widget reach a share of their target or limit: `{ "dashboardId": 4, "widgetId": "plan", "percent": 100 }`.
- **event** — something happened:
  - `{ "event": "import.failed" }`, `{ "event": "price.failed" }`;
  - rows: `{ "event": "row.created" | "row.updated" | "row.deleted", "source": "orders", "formula"?: "status = \"urgent\"", "columns"?: ["status"], "origins"?: ["form"] }` — `formula` is a row condition (not for deleted rows), `columns` only for `row.updated` (one of them changed), `origins` from `app api mcp form import calendar` (none: all). Rows written within a few seconds come as one message; at most 20 messages an hour per rule.
- **report** — a message on a schedule:
  `{ "every": "week", "day": 1, "at": "08:00", "range": "last", "compare": "previous", "intro"?: "…", "ai"?: false, "blocks": [ … ] }`
  `every`: `day week month quarter year`; `day`: weekday 1 (Monday)–7 for weekly, day of the month 1–28 otherwise; `range`: `last` (the period just over) or `current`; `compare`: `previous`, `year` or `none`. Blocks:
  - `{ "type": "value", "label": "Hours", "source": "time_entries", "formula": "SUM(hours)", "dateColumn": "date", "format"?: "number", "higherIsBetter"?: true }`
  - `{ "type": "top", "label": "By project", "source": "time_entries", "formula": "SUM(hours)", "dateColumn": "date", "groupBy": "project_id", "limit": 5, "order"?: "desc" }`
  - `{ "type": "dashboard", "dashboardId": 4, "widgets"?: ["hours", "value"] }` — its key figures
  - `{ "type": "rules" }` — the rules that hold now; `{ "type": "text", "text": "…" }`
  `POST …/rules/preview` `{name, config}` shows a report as it would go out today, without saving.
