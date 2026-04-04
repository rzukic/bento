# API Reference

> REST API served by the ASP.NET Core backend at `/api/v1/`.
> All requests go through Caddy — clients always use relative URLs.

---

## Authentication

Two methods are accepted on all protected endpoints:

| Method | Header | Value |
|--------|--------|-------|
| JWT Bearer | `Authorization` | `Bearer <token>` |
| API Key | `X-API-Key` | `<key>` |

Obtain a JWT via `POST /api/v1/auth/login`. API keys are managed in the UI under Settings → API Keys.

Unauthenticated requests return `401 Unauthorized`.

---

## Conventions

- All request and response bodies are JSON.
- Dates are ISO 8601 strings (`2026-04-03T12:00:00Z`).
- IDs are UUIDs.
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
| `POST` | `/api/v1/auth/login` | Authenticate and receive a JWT |
| `POST` | `/api/v1/auth/refresh` | Refresh an expiring JWT |
| `POST` | `/api/v1/auth/logout` | Invalidate current session |

> **Not yet implemented** — planned for Phase 1.

---

### Admin

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/api/v1/admin/health` | Backend readiness and database pool health |

Current response shape:

```json
{
  "status": "healthy",
  "database": "sqlite",
  "systemPool": "open",
  "dataPool": "open"
}
```

If a backend dependency is unavailable, the same endpoint returns `503 Service Unavailable` with `status: "unhealthy"` and an `error` message.

The response also includes the `X-Request-Id` header so callers can correlate failures with backend logs.

---

### Tables

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/api/v1/tables` | List all tables |
| `POST` | `/api/v1/tables` | Create a new table |
| `GET` | `/api/v1/tables/:tableId` | Get table schema |
| `PATCH` | `/api/v1/tables/:tableId` | Update table metadata |
| `DELETE` | `/api/v1/tables/:tableId` | Delete a table |
| `GET` | `/api/v1/tables/:tableId/rows` | List rows (paginated) |
| `POST` | `/api/v1/tables/:tableId/rows` | Insert a row |
| `PATCH` | `/api/v1/tables/:tableId/rows/:rowId` | Update a row |
| `DELETE` | `/api/v1/tables/:tableId/rows/:rowId` | Delete a row |

> **Not yet implemented** — planned for Phase 1.

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
