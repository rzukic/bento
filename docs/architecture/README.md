# Architecture

> System topology, service boundaries, and data flow for the Repère platform.
> For design decisions and rationale, see [`wiki/plan/tech-decisions.md`](../../wiki/plan/tech-decisions.md).

---

## Service Topology

```
Browser / Client
      │
      ▼
┌─────────────┐
│    Caddy    │  :80 / :443  (repere/proxy)
│  (proxy)   │
└──────┬──────┘
       │
       ├─ /api/v1/* ──► ┌──────────────┐
       ├─ /ws ──────────►  ASP.NET Core │  :5000  (repere/backend)
       │                └──────┬───────┘
       │                       │
       │               ┌───────┴────────┐
       │               │                │
       │          System Pool      Data Pool
       │         (repere_system)  (repere_data)
       │               │                │
       │          ┌────▼────────────────▼────┐
       │          │   Postgres / SQLite DB    │
       │          └───────────────────────────┘
       │
       └─ /* ──────────► ┌──────────────┐
                         │    nginx     │  :80  (repere/frontend)
                         │ (static SPA) │
                         └──────────────┘
```

## Containers

| Container | Image | Role |
|-----------|-------|------|
| `proxy` | `repere/proxy` | Caddy — TLS termination, routing |
| `backend` | `repere/backend` | ASP.NET Core API server (WebSocket planned later) |
| `frontend` | `repere/frontend` | nginx serving Vite/React static build |

The frontend container only serves static files. All API calls from the browser go to `/api/v1/*` — Caddy routes these to the backend. The frontend never needs to know the backend's address.

> **Current implementation note** — the backend HTTP server foundation exists in Step 1.3. WebSocket transport is still planned for a later phase.

---

## Database Layout

### Postgres

```
repere (database)
├── _system (schema)     ← privileged, repere_system role only
│   ├── schema_versions  ← Model JSON + version history
│   ├── migrations       ← migration log
│   ├── users            ← platform user accounts
│   ├── api_keys         ← API key hashes
│   ├── dashboards       ← dashboard JSON definitions
│   ├── credentials      ← encrypted third-party credentials
│   ├── jobs             ← scheduled job definitions
│   ├── functions        ← (Phase 3 — custom JS function definitions)
│   └── forms            ← (Phase 4 — form definitions)
└── public (schema)      ← user data, repere_data role
    ├── [user tables]
    └── [user tables]
```

### SQLite

```
system.db     ← equivalent of _system schema
data.db       ← equivalent of public schema
```

---

## Connection Pools

| Pool | Role | Schema access | Used for |
|------|------|--------------|---------|
| `SystemPool` | `repere_system` | `_system.*` only | Platform operations — auth, migrations, config |
| `DataPool` | `repere_data` | `public.*` only | All user-generated queries |

The `DataPool` will also run user SQL through the **DDL blocklist** before execution once custom SQL/data-query endpoints exist.

> **Not yet implemented** — the DDL blocklist is planned for Step 1.5.

---

## Request Flow

### Standard API Request

```
Browser → Caddy → Backend (SystemPool or DataPool) → DB → response
```

### User SQL Query

```
Browser → Caddy → Backend → DDL Blocklist → DataPool (repere_data) → public schema only
```

> **Future flow** — this path describes the intended custom SQL/query pipeline once those endpoints and the DDL blocklist are implemented.

### WebSocket (Live Updates)

```
Browser → Caddy (/ws) → Backend (SignalR / WebSocket hub) → push on dirty flag resolution
```

> **Not yet implemented** — planned for a later phase when live updates are needed.

---

## Environment Configuration

> Full env var reference: see deployment docs in [`wiki/parts/13-deployment/`](../../wiki/parts/13-deployment/README.md).

The frontend has **zero environment variables** — the API URL is always relative (`/api/v1/`). Caddy handles routing.

The backend receives:
- `DATABASE_URL` — SQLite convenience URL for the shared data root
- `DATABASE_SYSTEM_CONNECTION` — privileged/system pool connection string
- `DATABASE_DATA_CONNECTION` — restricted/data pool connection string
- `JWT_SECRET` — signing key for JWT tokens
- `DOMAIN` — passed to Caddy for TLS (default: `localhost`)
