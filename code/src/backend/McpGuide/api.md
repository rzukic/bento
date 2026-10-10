# The REST API (for call_api)

Every tool is a call to `/api/v1`. `call_api` makes any other call with this connection's rights: `{ "method": "GET", "path": "/api/v1/…", "body": {…} }`. Responses are `{ "data": … }`; errors `{ "error": { "code", "message", "details" } }`.

| Area | Endpoints |
|---|---|
| Rows | `GET/POST /data/{table}`, `GET/PUT/PATCH/DELETE /data/{table}/{id}`; list params `filter[col]=v`, `filter[col][op]=v`, `search`, `sort=-col`, `fields=a,b`, `include=link`, `page`, `per_page` (≤ 100); related rows `GET /data/{table}/{id}/{relation}` |
| Query | `POST /query` (topic `queries`), `POST /query/variables` `{variables: [{name, source?, formula, filter?}]}` |
| Saved queries | `GET/POST /queries`, `GET/PUT/DELETE /queries/{id}`, `POST /queries/{id}/run` |
| Formulas | `POST /formulas/validate` `{table, formula, resultType?}`, `POST /formulas/preview` `{table, formula, limit?}`, `GET /formulas/functions` |
| Schema | `GET /admin/schema/model`, `POST /admin/schema/preview`, `PUT /admin/schema?confirm_destructive=true`, `GET /admin/migrations` (admin) |
| Dashboards | `GET/POST /dashboards`, `GET/PUT/DELETE /dashboards/{id}` (`{name, isShared, dashboard}`) |
| Forms | `GET/POST /forms`, `GET/PUT/DELETE /forms/{id}` (`{name, isShared, form}`) |
| Functions | `GET /functions`, `GET /functions/templates`, `GET /functions/{name}`, `GET /functions/{name}/versions`, `POST /functions`, `PUT/DELETE /functions/{name}`, `POST /functions/test` |
| Price sources | `GET /price-sources`, `GET /price-sources/{id}`, `GET /price-sources/{id}/runs`, `POST /price-sources/refresh` `{tables}`, `POST/PUT/DELETE /price-sources/{id}`, `POST /price-sources/{id}/run?kind=latest|history|daily` |
| Import | `POST /import/analyze` `{content: base64}`, `POST /import/{table}/preview`, `POST /import/{table}` `{content, settings, mapping, keyColumns}`, profiles under `/import/{table}/profiles` |
| Explorer views | `GET /views/{table}`, `POST /views`, `PUT/DELETE /views/{id}` |
| Problems | `GET /admin/problems?limit=` — slow queries (with the query and SQL), timeouts and server errors, newest first (admin) |
| Description | `GET /openapi.json` — every endpoint of this installation with fields |

Users, API keys, credentials and connected apps are not reachable for connected apps.
