# API Reference

> REST API served by the ASP.NET Core backend at `/api/v1/`.
> All requests go through Caddy — clients always use relative URLs.

---

## Authentication

Authentication is partially defined but not yet enforced.

Planned Phase 1 auth methods:

| Method | Header | Value |
|--------|--------|-------|
| JWT Bearer | `Authorization` | `Bearer <token>` |
| API Key | `X-API-Key` | `<key>` |

Current Step 1.4 foundation contract:
- `GET /api/v1/auth` returns the active auth mode, JWT/cookie runtime settings, and the list of auth features still deferred
- login, token issuance, auth middleware, and API key validation are still planned follow-on work within Step 1.4 / later phases

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
- `INTERNAL_ERROR`

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
| `POST` | `/api/v1/auth/login` | Authenticate and receive a JWT |
| `POST` | `/api/v1/auth/refresh` | Refresh an expiring JWT |
| `POST` | `/api/v1/auth/logout` | Invalidate current session |

`GET /api/v1/auth` is implemented as the Step 1.4 foundation surface. The interactive auth endpoints above remain planned for Phase 1 follow-on tasks.

---

### Admin

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/api/v1/admin/health` | Backend readiness and database pool health |

Current response shape:

```json
{
  "status": "healthy",
  "database": {
    "system_pool": "connected",
    "data_pool": "connected"
  },
  "uptime": "3d 14h 22m",
  "version": "1.0.0"
}
```

Contract notes:
- `database.system_pool` and `database.data_pool` report `connected` or `unavailable`
- `uptime` is the current backend process runtime formatted as `Xd Xh Xm`
- `version` comes from the backend assembly version metadata

If a backend dependency is unavailable, the same endpoint returns `503 Service Unavailable` with `status: "unhealthy"` and the same structural fields plus an `error` message.

The response also includes the `X-Request-Id` header so callers can correlate failures with backend logs.
This is the canonical backend health endpoint for deployment checks and container health probes.

---

### Schema

| Method | Path | Description |
|--------|------|-------------|
| — | `/api/v1/schema/*` | Reserved route area for future schema-management endpoints |

> **Not yet implemented** — Step 1.3 reserves the `schema` prefix only. Concrete schema-management endpoints are future work.

---

### Data

| Method | Path | Description |
|--------|------|-------------|
| — | `/api/v1/data/*` | Reserved route area for future auto-generated CRUD/data endpoints |

> **Not yet implemented** — Step 1.3 reserves the `data` prefix only. Auto CRUD endpoints are planned for Step 1.5.

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
