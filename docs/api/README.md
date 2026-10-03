# API Reference

> REST API served by the ASP.NET Core backend at `/api/v1/`.
> Phase 1 packaged deployments expose the backend directly on the configured host port; a future proxy/frontend layer may sit in front of the same route contract later.

---

## Authentication

Step 1.4 auth is now active for the implemented backend surface.

Current implemented Phase 1 auth method:

| Method | Header | Value |
|--------|--------|-------|
| JWT Bearer | `Authorization` | `Bearer <token>` |

Current Step 1.4 auth contract:
- `GET /api/v1/auth` returns the active auth mode, JWT/cookie runtime settings, whether bootstrap self-registration is currently open, and the list of auth features still deferred
- `refresh_cookie.require_secure` reports whether `Secure` is forced by `AUTH_REFRESH_COOKIE_SECURE=true`; when it is `false` (the default in every environment since Step 2.7) the cookie follows the request scheme — `Secure` on HTTPS, including TLS terminated at a trusted proxy, plain on HTTP
- `POST /api/v1/auth/register` allows bootstrap self-registration until the first user exists, then requires an authenticated admin
- `POST /api/v1/auth/login` issues a bearer access token and refresh cookie
- `POST /api/v1/auth/refresh` rotates the refresh session and returns a new access token
- rate limits per client IP and route: login, register and the setup `POST`s allow `auth_rate_limit_per_minute` requests (default 5); refresh and logout allow `session_rate_limit_per_minute` (default 120). Both values are reported by `GET /api/v1/auth`; a limited request returns `429 TOO_MANY_REQUESTS` with `details.retryAfterSeconds`. The SPA retries a rate-limited refresh instead of treating it as signed out
- `POST /api/v1/auth/logout` revokes the refresh session named by the cookie (if any) and clears the cookie; always `204`, so a stale or missing cookie never blocks signing out. The short-lived access token stays valid until it expires
- `/api/v1/*` routes outside `/api/v1/auth/*` require bearer auth in `standard` mode and run with an admin bypass in `open` mode
- API key auth remains deferred to a later phase

---

## Conventions

- All request and response bodies are JSON.
- Dates are ISO 8601 strings (`2026-04-03T12:00:00Z`).
- ID formats are endpoint-specific. Do not assume UUIDs globally yet.
- Every response includes `X-Request-Id` for log correlation.
- Errors follow a consistent shape:

```json
{
  "error": {
    "code": "NOT_FOUND",
    "message": "No table with ID abc-123 exists.",
    "details": {
      "tableId": "abc-123",
      "requestId": "req_123"
    }
  }
}
```

Known backend error codes:
- `VALIDATION_FAILED`
- `NOT_FOUND`
- `UNAUTHORIZED`
- `FORBIDDEN`
- `CONFLICT`
- `UNPROCESSABLE_ENTITY`
- `TOO_MANY_REQUESTS`
- `INTERNAL_ERROR`

Endpoint-specific codes currently used by implemented routes:
- `CONFIRMATION_REQUIRED` (`PUT /api/v1/admin/schema` when destructive changes need explicit confirmation)
- `NO_MIGRATIONS` (`POST /api/v1/admin/migrations/rollback` when there is nothing to roll back)

In development, unexpected `500` responses may also include debug-only fields such as `exceptionType` and `stackTrace` inside `error.details`. Those fields are not returned in production.

Cross-origin and request-size behavior:
- same-origin is allowed by default
- configured origins from `ALLOWED_ORIGINS` receive CORS headers
- disallowed cross-origin requests return `403 FORBIDDEN`
- oversized JSON requests return `413` with `VALIDATION_FAILED`

---

## Endpoints

> This reference will be expanded as each phase is implemented. Stub sections are listed below.

### Auth

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/api/v1/auth` | Return the active auth-mode/runtime contract and current Step 1.4 deferrals |
| `POST` | `/api/v1/auth/register` | Create the bootstrap admin or an admin-managed user |
| `POST` | `/api/v1/auth/login` | Authenticate and receive a JWT |
| `POST` | `/api/v1/auth/logout` | Revoke the refresh session and clear the cookie |
| `POST` | `/api/v1/auth/refresh` | Refresh an expiring JWT |
| `POST` | `/api/v1/auth/logout` | Invalidate current session |

`GET /api/v1/auth`, `POST /register`, `POST /login`, and `POST /refresh` are implemented in Step 1.4. `POST /logout` and API key auth remain future work.

---

### Setup

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/api/v1/setup/status` | Public first-run status used by the `/setup` wizard |
| `POST` | `/api/v1/setup/sample-data` | Load the demo workspace into an empty installation (admin only; Step 2.7, TASK-145) |

`GET /api/v1/setup/status` is anonymous in every auth mode; it is a cheap read the SPA calls on every route load, so it is not rate-limited (the `POST` setup routes share the auth rate limit). It never contains secrets, file paths, or JWT details. Response:

```json
{
  "initialized": false,
  "auth_mode": "standard",
  "auth_required": true,
  "registration_bootstrap_open": true,
  "database_provider": "sqlite",
  "schema_version": 0,
  "sample_data_available": true,
  "version": "0.1.0"
}
```

- `initialized` is `true` once the first (bootstrap) admin exists; in `open` mode it is always `true` because no account is needed
- `auth_mode` and `database_provider` are deployment configuration and shown read-only by the wizard
- `sample_data_available` is `true` only while the schema is still at version 0

`POST /api/v1/setup/sample-data` (admin role) loads the embedded demo workspace: the `customers` / `orders` / `products` schema with two relations plus demo rows (6 customers, 10 orders across all statuses, 5 products). It runs through the normal schema apply and CRUD paths, so it produces schema version 1 and one migration history entry. It is a one-time convenience, not an import feature: once any schema exists (version > 0) it answers `409 CONFLICT` with `error.details.schemaVersion` and changes nothing. Response:

```json
{
  "schema_version": 1,
  "tables_created": ["customers", "orders", "products"],
  "rows_inserted": { "customers": 6, "orders": 10, "products": 5 }
}
```

The fixture files live in `code/scripts/fixtures/` and are embedded into the backend at build time, so the packaged smoke scripts and the wizard share one source.

---

### Admin

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/api/v1/admin/health` | Backend readiness and database pool health |
| `GET` | `/api/v1/admin/health/details` | Detailed backend health and runtime state for admin callers |
| `GET` | `/api/v1/admin/schema` | Return the current enriched schema snapshot for runtime navigation |
| `GET` | `/api/v1/admin/schema/model` | Return the current canonical editable Model JSON |
| `POST` | `/api/v1/admin/schema/preview` | Validate and preview a schema migration without applying it |
| `PUT` | `/api/v1/admin/schema` | Apply a new Model JSON and run the generated migration |
| `GET` | `/api/v1/admin/migrations` | List migration history |
| `POST` | `/api/v1/admin/migrations/rollback` | Roll back the most recent migration |

Current admin contract:
- `GET /api/v1/admin/health` is public in both `standard` and `open` mode so container and orchestrator readiness probes can use the canonical versioned health route without a bootstrap token
- `GET /api/v1/admin/health/details` requires an admin caller in `standard` mode and uses the open-mode admin bypass in `open` mode
- the schema and migration admin endpoints require an authenticated admin in `standard` auth mode and run with the open-mode admin bypass in `open` mode
- `GET /api/v1/admin/schema` returns an enriched schema snapshot with `application/json`; relation objects include runtime-only resolver metadata such as `resolvedFromAccessor`, `resolvedToAccessor`, and `resolvedJunctionTableName` for Data Explorer navigation
- `GET /api/v1/admin/schema/model` returns canonical editable Model JSON with `application/json`; runtime-only `resolved*` relation metadata is omitted and this is the source used by the JSON workbench and export flow
- both `GET` schema endpoints are readable by every authenticated role (`viewer`, `editor`, `admin`, `api`) so the browser UI can resolve tables and relations for non-admin users; `POST /schema/preview`, `PUT /schema`, `GET /migrations`, and `POST /migrations/rollback` remain `admin`-only
- `POST /api/v1/admin/schema/preview` validates and normalizes the submitted Model JSON, diffs it against the current stored schema, and returns preview metadata without running SQL, changing schema history, or reloading the live schema cache
- `PUT /api/v1/admin/schema` validates the submitted Model JSON, diffs it against the current schema, applies the generated migration, and reloads the live schema cache so `/api/v1/data/*` routes reflect the new schema immediately
- destructive schema changes must be retried with `?confirm_destructive=true`; otherwise the endpoint returns `409 CONFLICT` with `CONFIRMATION_REQUIRED`
- `GET /api/v1/admin/migrations` returns migration history in reverse chronological order
- `POST /api/v1/admin/migrations/rollback` rolls back the most recent migration and reloads the schema cache; if no migration exists it returns `400` with `NO_MIGRATIONS`

Schema preview shape (`POST /api/v1/admin/schema/preview`):

```json
{
  "isValid": true,
  "hasChanges": true,
  "isDestructive": true,
  "requiresConfirmation": true,
  "currentVersion": 1,
  "errors": [],
  "warnings": [],
  "changes": [
    {
      "changeType": "AddColumn",
      "table": "widgets",
      "column": "color",
      "isDestructive": false
    },
    {
      "changeType": "AlterColumnType",
      "table": "widgets",
      "column": "quantity",
      "oldValue": "text",
      "newValue": "integer",
      "coercionKind": "Lossy",
      "castExamples": [
        { "sourceValue": "2.7", "castValue": 2, "isValid": true }
      ],
      "isDestructive": true
    }
  ],
  "normalizedModel": {
    "version": 0,
    "tables": [
      {
        "name": "widgets",
        "columns": [
          { "name": "id", "type": "integer", "primaryKey": true, "nullable": false, "unique": true },
          { "name": "name", "type": "text", "nullable": false },
          { "name": "color", "type": "text" }
        ],
        "indexes": []
      }
    ],
    "relations": []
  },
  "plan": {
    "sqlUp": [
      "ALTER TABLE \"widgets\" ADD COLUMN \"color\" TEXT;"
    ],
    "sqlDown": [
      "ALTER TABLE \"widgets\" DROP COLUMN \"color\";"
    ],
    "isDestructive": true,
    "requiresConfirmation": true
  }
}
```

Preview contract notes:
- preview responses use `200 OK` for both valid and validation-failed drafts; callers should inspect `isValid` plus the `errors` array
- `errors` and `warnings` use `{ code, message, path }`, where `path` points into the submitted Model JSON
- `changes` contain the diff metadata the workbench can group into table / column / relation / index summaries; entries may include `previousTable`, `previousColumn`, `relation`, `index`, `oldValue`, `newValue`, and `coercionKind`
- lossy `AlterColumnType` changes may include up to 5 deterministic `castExamples` from live data, shaped as `{ sourceValue, castValue, isValid, error? }`
- `normalizedModel` is the validator-normalized Model JSON draft that preview/apply operate on
- `plan` is omitted when validation fails or migration planning cannot be generated safely; when present, `sqlUp` / `sqlDown` show the generated migration statements

Schema apply shape (`PUT /api/v1/admin/schema`):

```json
{
  "applied": true,
  "version": 2,
  "changes": [
    {
      "changeType": "AddColumn",
      "table": "widgets",
      "column": "color",
      "isDestructive": false
    }
  ],
  "warnings": []
}
```

Apply contract notes:
- invalid Model JSON or schema validation failures return `400` with `VALIDATION_FAILED`; validation details use the same `{ code, message, path }` shape as preview
- destructive changes return `409` with `CONFIRMATION_REQUIRED` until the caller retries the same draft with `?confirm_destructive=true`
- successful applies reload the live schema cache before returning, so generated `/api/v1/data/*` route availability reflects the applied model immediately

Public probe shape (`GET /api/v1/admin/health`):

```json
{
  "status": "healthy",
  "database": {
    "system_pool": "connected",
    "data_pool": "connected"
  },
  "version": "1.0.0"
}
```

Contract notes:
- `database.system_pool` and `database.data_pool` report `connected` or `unavailable`
- `version` comes from the backend assembly version metadata
- anonymous unhealthy responses keep the same minimal structure and replace detailed exception text with `error: "unhealthy"`

Detailed admin shape (`GET /api/v1/admin/health/details`):

```json
{
  "status": "healthy",
  "database": {
    "system_pool": "connected",
    "data_pool": "connected"
  },
  "runtime": {
    "database_provider": "sqlite",
    "auth_mode": "standard",
    "log_level": "Information",
    "data_dir": "/data",
    "config_path": "/data/config.json",
    "jwt_configured": true
  },
  "uptime": "3d 14h 22m",
  "version": "1.0.0"
}
```

- `runtime.database_provider` reports the effective backend database provider (`sqlite` or `postgres`)
- `runtime.auth_mode`, `runtime.log_level`, `runtime.data_dir`, and `runtime.config_path` expose packaged runtime state for operators
- `runtime.jwt_configured` reports whether a signing secret is available, whether it came from env or from the persisted runtime config
- `uptime` is the current backend process runtime formatted as `Xd Xh Xm`
- admin unhealthy responses keep the detailed structure and include the underlying `error` message for diagnosis

The response also includes the `X-Request-Id` header so callers can correlate failures with backend logs.
`GET /api/v1/admin/health` is the canonical backend health endpoint for deployment checks and container health probes.

---

### Schema

| Method | Path | Description |
|--------|------|-------------|
| — | `/api/v1/schema/*` | Reserved route area for future non-admin schema endpoints |

> Schema management for Phase 1 lives under `/api/v1/admin/schema`. The standalone `/api/v1/schema/*` area is still reserved for future work.

---

### Data

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/api/v1/data/<table>` | List rows for a schema-defined table |
| `GET` | `/api/v1/data/<table>/:id` | Fetch a single row by numeric ID |
| `POST` | `/api/v1/data/<table>` | Create a row |
| `PUT` | `/api/v1/data/<table>/:id` | Replace all writable columns on a row |
| `PATCH` | `/api/v1/data/<table>/:id` | Update only provided writable columns |
| `DELETE` | `/api/v1/data/<table>/:id` | Delete a row |
| `GET` | `/api/v1/data/<table>/:id/<relation>` | Fetch related rows through a relation accessor |

Current Step 1.5 data contract:
- tables and relation accessors come from the current Model JSON; route availability updates immediately after `PUT /api/v1/admin/schema`
- all `/api/v1/data/*` endpoints require auth in `standard` mode and run with the open-mode admin bypass in `open` mode
- read endpoints (`GET`) accept every authenticated role (`viewer`, `editor`, `admin`, `api`); write endpoints (`POST`, `PUT`, `PATCH`, `DELETE`) require `editor` or `admin`, or the `api` role, and return `403` with `error.details.requiredRole` / `error.details.actualRole` otherwise
- `GET /api/v1/data/<table>` returns `{ data, meta }`, where `meta` includes `page`, `per_page`, `total`, and `total_pages`
- `POST /api/v1/data/<table>` returns `201 Created` with `{ data }`
- `PUT` is full replacement for writable columns; omitted optional writable columns may become `null`, while omitted non-nullable writable columns fail validation
- `PATCH` updates only the provided writable columns
- writes are validated against the current Model JSON: required columns, types, enums, unique constraints, foreign keys, unknown columns, and computed/read-only columns
- foreign-key validation failures return `422 UNPROCESSABLE_ENTITY`

List query parameters for `GET /api/v1/data/<table>`:

| Param | Description |
|-------|-------------|
| `filter[col]` | Exact match |
| `filter[col][op]` | Operators: `eq`, `neq`, `gt`, `gte`, `lt`, `lte`, `like`, `in`, `null`, `notnull` |
| `sort` | Column name, prefix `-` for descending, comma-separated for multi-sort |
| `include` | Comma-separated relation accessors, with dot notation for nested includes |
| `fields` | Sparse fieldset of physical column names on the root table |
| `page`, `per_page` | Pagination (default `25`, max `1000`) |
| `search` | Case-insensitive text search across text columns |

Relation sub-resource contract for `GET /api/v1/data/<table>/:id/<relation>`:
- relation names use relation accessor metadata, not raw relation IDs
- supports `filter`, `sort`, `page`, and `per_page` against the related table
- returns the same `{ data, meta }` envelope with real pagination metadata for the related query
- does not currently support nested `include`, `fields`, or `search` on the sub-resource endpoint

---

### Query

| Method | Path | Description |
|--------|------|-------------|
| — | `/api/v1/query/*` | Reserved route area for future query/custom SQL endpoints |

> **Not yet implemented** — Step 1.3 reserves the `query` prefix only. Concrete query endpoints are future work.

---

### Formulas

| Method | Path | Description |
|--------|------|-------------|
| `POST` | `/api/v1/tables/:tableId/columns/:colId/formula` | Set formula for a column |
| `DELETE` | `/api/v1/tables/:tableId/columns/:colId/formula` | Remove formula |
| `GET` | `/api/v1/tables/:tableId/columns/:colId/formula/status` | Check materialization status |

> **Not yet implemented** — planned for Phase 2.

---

### Dashboards

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/api/v1/dashboards` | List dashboards |
| `POST` | `/api/v1/dashboards` | Create a dashboard |
| `GET` | `/api/v1/dashboards/:dashboardId` | Get dashboard config |
| `PATCH` | `/api/v1/dashboards/:dashboardId` | Update dashboard |
| `DELETE` | `/api/v1/dashboards/:dashboardId` | Delete dashboard |

> **Not yet implemented** — planned for Phase 3.

---

### WebSocket

Connected at `/ws`. Uses SignalR protocol.

| Event (server → client) | Payload | Description |
|--------------------------|---------|-------------|
| `column.materialized` | `{ tableId, columnId }` | Tier 2/3 column finished computing |
| `import.complete` | `{ tableId, rowCount }` | Bulk import finished |

> **Not yet implemented** — planned for Phase 1 (connection) / Phase 2 (formula events).
