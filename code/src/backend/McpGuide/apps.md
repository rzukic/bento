# Calendar apps, share links and workspace settings

## Calendar apps (permission apps.write)

Tables can show up as calendars on the user's phone or computer (CalDAV: iPhone, Mac, Android with DAVx⁵, Thunderbird). The rows stay in Bento; what you add with `create_row` appears on the device.

- Read: `call_api` `GET /api/v1/admin/apps`.
- Create: `POST /api/v1/admin/apps` `{ "name": "Termine", "kind": "calendar", "config": { "calendars": [ … ] } }`; change: `PUT /api/v1/admin/apps/{id}` (the whole app); delete: `DELETE …/{id}` (ask first).
- A calendar: `{ "name": "Appointments", "table": "appointments", "title": "title", "start": "starts_at", "end"?: "ends_at", "allDay"?: "<yes/no column>", "description"?: "note", "location"?: "location", "recurrence"?: "<RRULE text column>", "duration"?: 60, "color"?: "#3B82F6", "filters"?: [ { "column": "status", "op": "neq", "value": "cancelled" } ] }` — `start` is a date (all-day events) or date-time column; `filters` ops `eq neq gt gte lt lte like in null notnull`.
- **Devices**: each device signs in with its own password, which is shown once. You never make it: the answer to creating or changing an app carries `setupLink`, the app's page in Bento opened at "Add device". Give the user that link; they name the device there and get the password and the address to enter.

## Share links (permission shares.write)

A link makes a dashboard (read-only) or a form (to add rows) public — for anyone who has it. Only on the user's explicit wish.

- Read: `GET /api/v1/dashboards/{id}/shares`, `GET /api/v1/forms/{id}/shares`.
- Create: `POST /api/v1/dashboards/{id}/shares` `{ "name": "Family", "expiresInDays"?: 30, "filters"?: { "column": [values] }, "embed"?: false }`; a form: `POST /api/v1/forms/{id}/shares` `{ "name", "expiresInDays"?, "perHour"?: 10, "thanks"?: "…" }`. The answer has the link (`url`) — shown once.
- Revoke: `DELETE /api/v1/shares/{id}`.

## Workspace settings (permission settings.write)

`GET /api/v1/admin/settings`; `PUT /api/v1/admin/settings` `{ "locale": "de-AT" | null, "currency": "EUR", "timeZone"?: "Europe/Vienna", "weekStart"?: "monday" | "sunday" | "saturday", "yearStart"?: 1–12 }` — how numbers and dates are written, which time zone "today" is in, when weeks and (fiscal) years start. For everyone in the workspace: ask first.

## Categorisation rules (permission data.write)

`GET /api/v1/categorise/{table}/rules`; `POST /api/v1/categorise/{table}/rules` `{ "rules": [ { "column": "category_id", "matchColumn": "payee", "pattern": "REWE", "value": "3" } ] }`; `DELETE …/rules/{id}`. New rows of imports get the value when the pattern is found.
