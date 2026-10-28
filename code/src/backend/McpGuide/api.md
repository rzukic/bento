# The REST API (for call_api)

Every tool is a call to `/api/v1`. `call_api` makes any other call with this connection's rights: `{ "method": "GET", "path": "/api/v1/…", "body": {…} }`. Responses are `{ "data": … }`; errors `{ "error": { "code", "message", "details" } }`.

| Area | Endpoints |
|---|---|
| Rows | `GET/POST /data/{table}`, `GET/PUT/PATCH/DELETE /data/{table}/{id}`; list params `filter[col]=v`, `filter[col][op]=v`, `search`, `sort=-col`, `fields=a,b`, `include=link`, `page`, `per_page` (≤ 100); related rows `GET /data/{table}/{id}/{relation}`; many rows at once `POST /data/{table}/bulk` `{rows, upsertOn?}` (≤ 1000, all or none; also the tool `upsert_rows`) |
| Query | `POST /query` (topic `queries`), `POST /query/variables` `{variables: [{name, source?, formula, filter?}]}` |
| Saved queries | `GET/POST /queries`, `GET/PUT/DELETE /queries/{id}`, `POST /queries/{id}/run`; as a table: `POST /queries/{id}/table` `{name?}`, `GET /query-tables/{table}`, `POST /query-tables/{table}/refresh` |
| Workspaces | `GET /workspaces` (those you can use, with your role); `POST /workspaces` `{name, slug?}`, `PUT /workspaces/{id}` `{name}` with `workspaces.manage`. Another workspace's endpoints: `/api/v1/w/{slug}/…`, or the `workspace` argument of any tool |
| Templates | `GET /templates` (installed here, with their tables), `PUT /templates/{id}` `{title, tables: {name in the template: name here}}`, `DELETE /templates/{id}` (admins) |
| Formulas | `POST /formulas/validate` `{table, formula, resultType?}`, `POST /formulas/preview` `{table, formula, limit?}`, `GET /formulas/functions` |
| Schema | `GET /admin/schema/model`; operations: `POST /admin/schema/patch/preview`, `PATCH /admin/schema?confirm_destructive=true` `{operations}`; whole model: `POST /admin/schema/preview?compact=true`, `PUT /admin/schema?confirm_destructive=true`; `GET /admin/migrations` (admin) |
| Dashboards | `GET/POST /dashboards`, `GET/PUT/DELETE /dashboards/{id}` (`{name, isShared, dashboard}`) |
| Forms | `GET/POST /forms`, `GET/PUT/DELETE /forms/{id}` (`{name, isShared, form}`) |
| Functions | `GET /functions`, `GET /functions/templates`, `GET /functions/{name}`, `GET /functions/{name}/versions`, `POST /functions`, `PUT/DELETE /functions/{name}`, `POST /functions/test` |
| Price sources | `GET /price-sources`, `GET /price-sources/{id}`, `GET /price-sources/{id}/runs`, `POST /price-sources/refresh` `{tables}`, `POST/PUT/DELETE /price-sources/{id}`, `POST /price-sources/{id}/run?kind=latest|history|daily` |
| Import | CSV, Excel (`.xlsx`) or JSON: `POST /import/analyze` `{content: base64, fileName}`, `POST /import/{table}/preview`, `POST /import/{table}` `{content, settings: {format?, sheet?, jsonPath?, …}, mapping, keyColumns, updateChanged?}` (`updateChanged`: rows with a known key and other values are updated, status `changed`), profiles under `/import/{table}/profiles`; `POST /import/fetch` `{url, headers}` fetches a file from https (`{{name}}` = vault credential, admins); `POST /import/function` `{function}` runs a function and returns its rows as a file (topic `functions`); saved imports that run on a schedule under `/import/{table}/sources` (`config: {url | function, headers, settings, mapping, keyColumns, updateChanged, categoryColumn, schedule: {kind: manual|daily|hours, at, every}}`), `POST …/sources/{id}/run`, `GET …/sources/{id}/runs` |
| Explorer views | `GET /views/{table}`, `POST /views`, `PUT/DELETE /views/{id}` |
| Problems | `GET /admin/problems?limit=` — slow queries (with the query and SQL), timeouts and server errors, newest first (admin) |
| Description | `GET /openapi.json` — every endpoint of this installation with fields |
| Rules and reports | topic `automation` — `/admin/notifications/rules` (permission `automation.write`) |
| Calendar apps, share links, settings | topic `apps` — `/admin/apps` (`apps.write`), `…/shares` (`shares.write`), `/admin/settings` (`settings.write`) |

Users, API keys, credentials, notification channels, device passwords and connected apps are not reachable for connected apps, nor for API keys.
