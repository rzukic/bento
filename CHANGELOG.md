# Changelog

> High-level record of what changed and when.
> Each entry is a short overview — click the working-step link for full detail.
>
> **Format**: newest entries at the top.
> **AI workers**: see [how-to-changelog.md](ai-workers/how-to-changelog.md) before writing here.

---

## Unreleased

_Changes that are complete but not yet part of a versioned release._

## Phase 1 — Foundation

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

_No versions shipped yet. First release will appear here._

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
