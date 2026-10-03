# Architecture

> System topology, service boundaries, and data flow for the Bento platform.
> For design decisions and rationale, see [`wiki/plan/tech-decisions.md`](../../wiki/plan/tech-decisions.md).

---

## Service Topology

```
Browser / Client
      │
      ▼
┌─────────────┐
│    Caddy    │  :80 / :443  (bento/proxy)
│  (proxy)   │
└──────┬──────┘
       │
       ├─ /api/v1/* ──► ┌──────────────┐
       ├─ /ws ──────────►  ASP.NET Core │  :5000  (bento/backend)
       │                └──────┬───────┘
       │                       │
       │               ┌───────┴────────┐
       │               │                │
       │          System Pool      Data Pool
       │         (bento_system)  (bento_data)
       │               │                │
       │          ┌────▼────────────────▼────┐
       │          │   Postgres / SQLite DB    │
       │          └───────────────────────────┘
       │
       └─ /* ──────────► ┌──────────────┐
                         │    nginx     │  :80  (bento/frontend)
                         │ (static SPA) │
                         └──────────────┘
```

## Containers

| Container | Image | Role |
|-----------|-------|------|
| `proxy` | `bento/proxy` | Caddy — TLS termination, routing |
| `backend` | `bento/backend` | ASP.NET Core API server (WebSocket planned later) |
| `frontend` | `bento/frontend` | nginx serving Vite/React static build |

The frontend container only serves static files. All API calls from the browser go to `/api/v1/*` — Caddy routes these to the backend. The frontend never needs to know the backend's address.

> **Current implementation note** — this diagram is the long-term target topology. Phase 1 currently ships a backend-only packaged deployment (no Caddy or frontend container yet), while keeping the backend route contract compatible with that later proxy shape.

---

## Database Layout

### Postgres

```
bento (database)
├── _system (schema)     ← privileged, bento_system role only
│   ├── schema_versions  ← Model JSON + version history
│   ├── migrations       ← migration log
│   ├── users            ← platform user accounts
│   ├── api_keys         ← API key hashes
│   ├── dashboards       ← dashboard JSON definitions
│   ├── credentials      ← encrypted third-party credentials
│   ├── jobs             ← scheduled job definitions
│   ├── functions        ← (Phase 3 — custom JS function definitions)
│   └── forms            ← (Phase 4 — form definitions)
└── public (schema)      ← user data, bento_data role
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
| `SystemPool` | `bento_system` | `_system.*` only | Platform operations — auth, migrations, config |
| `DataPool` | `bento_data` | `public.*` only | All user-generated queries |

The `DataPool` will also run user SQL through the **DDL blocklist** before execution once custom SQL/data-query endpoints exist.

> **Current implementation note** — the DDL blocklist exists as a backend building block after Step 1.5, but the custom SQL/query endpoints that would exercise it are still future work.

---

## Request Flow

### Standard API Request

```
Browser → Caddy → Backend (SystemPool or DataPool) → DB → response
```

### User SQL Query

```
Browser → Caddy → Backend → DDL Blocklist → DataPool (bento_data) → public schema only
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
