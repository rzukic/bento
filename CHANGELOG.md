# Changelog

> High-level record of what changed and when.
> Each entry is a short overview — click the working-step link for full detail.
>
> **Format**: newest entries at the top.
> **AI workers**: see [how-to-changelog.md](ai-workers/how-to-changelog.md) before writing here.

---

## Unreleased

_Changes that are complete but not yet part of a versioned release._

## Phase 2 — Data Management

### 2026-04-12 — Complete Step 2.1 frontend shell foundation

> **Phase**: Phase 2 — Data Management
> **Working step**: [Step 2.1 — Frontend Core & SPA Shell](working-steps/2.1-frontend-core-and-spa-shell.md)

Completed the first browser-facing frontend foundation so Repere now has a real SPA shell on top of the shipped backend contract. The Step 2.1 closeout leaves the repo with authenticated navigation, a shared typed client/store layer, and the first reusable theme/component baseline for the upcoming Data Explorer and Schema Designer work.

**Highlights:**
- The frontend shell now uses the real Phase 1 auth flow, including login, silent refresh bootstrap, protected routes, and schema snapshot loading against the live backend
- Added a reusable primitive layer for buttons, fields, panels, alerts, empty states, spinners, and confirm dialogs, plus practical light/dark/system theme switching
- Closed TASK-062 through TASK-066 and verified the frontend loop with `npm run typecheck`, `npm run test`, and `npm run build`

## Phase 1 — Foundation

### 2026-04-10 — Close the remaining Step 1.6 packaging audit follow-ups

> **Phase**: Phase 1 — Foundation
> **Working step**: [Step 1.6 — Docker Packaging & MVP 1 Validation](working-steps/1.6-docker-packaging.md)

Closed the last two Step 1.6 audit tasks by restoring the Postgres dual-pool privilege boundary and tightening the packaged image/scripts around health probing and build-context hygiene. The final validation pass now includes live packaged SQLite and Postgres demos plus the Postgres-tagged `.NET` slice against a real containerized database.

**Highlights:**
- Postgres tenant DDL now runs through the privileged/system pool with `public` first in `search_path`, while `repere_data` no longer has `CREATE` on `public`
- Fresh packaged Postgres databases now auto-grant table and sequence DML access from `repere_system` to `repere_data`, preserving CRUD after the ownership shift
- The runtime image now uses a built-in `--healthcheck` self-probe instead of installing `curl`, the smoke/demo scripts no longer source arbitrary env files, and packaged SQLite/Postgres validation both passed live
### 2026-04-10 — Harden packaged Postgres secrets and host exposure defaults

> **Phase**: Phase 1 — Foundation
> **Working step**: [Step 1.6 — Docker Packaging & MVP 1 Validation](working-steps/1.6-docker-packaging.md)

Closed the third packaging-audit follow-up by removing the packaged Postgres stack’s dangerous defaults. The Postgres override no longer publishes 5432 to the host automatically, application role passwords are no longer baked into source control, and the backend now fails fast if the packaged Postgres secrets are left on the shipped placeholder or the old committed dev values.

**Highlights:**
- `docker-compose.postgres.yml` now requires explicit Postgres secrets, while `docker-compose.postgres.expose.yml` is the only path that publishes Postgres to the host
- the Postgres init script now reads role passwords from environment instead of hard-coding `repere_system_dev` / `repere_data_dev`
- the smoke/demo scripts and deployment workflow now pass explicit Postgres secrets, and the backend startup guard rejects the known insecure defaults

### 2026-04-10 — Trim the public health probe to a minimal contract

> **Phase**: Phase 1 — Foundation
> **Working step**: [Step 1.6 — Docker Packaging & MVP 1 Validation](working-steps/1.6-docker-packaging.md)

Closed the second packaging-audit follow-up by splitting the health contract into a safe anonymous readiness probe and an admin-only detailed diagnostics route. Anonymous callers now get only the information a probe needs, while operators still have a versioned route for runtime details and raw failure messages when they are authenticated.

**Highlights:**
- `GET /api/v1/admin/health` now returns only status, database pool state, version, and a generic `error: "unhealthy"` on failure
- `GET /api/v1/admin/health/details` preserves runtime, uptime, and detailed error information for admin callers
- Added HTTP regressions that prove the anonymous probe does not leak `config_path`, `data_dir`, or raw exception text in either healthy or unhealthy paths

### 2026-04-10 — Harden persisted packaged JWT secret permissions

> **Phase**: Phase 1 — Foundation
> **Working step**: [Step 1.6 — Docker Packaging & MVP 1 Validation](working-steps/1.6-docker-packaging.md)

Closed the first follow-up issue from the Step 1.6 packaging audit by tightening how the packaged runtime persists auto-generated JWT signing secrets. New packaged deployments now create the runtime config with owner-only POSIX permissions, while older over-permissive files are detected and surfaced through a one-time startup warning instead of failing the app.

**Highlights:**
- `PersistentRuntimeConfigBootstrap` now creates `DATA_DIR/config.json` with `0600` on first write and repairs owned runtime data directories to `0700` on POSIX hosts
- Existing over-permissive runtime config files are detected and logged once per process so operators get a migration signal without a breaking startup change
- Added POSIX-gated regression coverage for secure file modes and the warning path, then verified the non-Postgres test suite still passes

### 2026-04-09 — Add Postgres-backed .NET test coverage

> **Phase**: Phase 1 — Foundation
> **Working step**: [Step 1.6 — Docker Packaging & MVP 1 Validation](working-steps/1.6-docker-packaging.md)

Added a true Postgres-backed `.NET` test lane on top of the packaged deployment work so backend behavior can now be exercised against a real PostgreSQL database from inside the normal test project, not only through black-box compose checks. The new slice stays intentionally narrow and provider-aware, which keeps the fast SQLite job quick while adding trustworthy Postgres regression coverage in CI.

**Highlights:**
- Added isolated Postgres test-database infrastructure and a provider-aware `ApiIntegrationTestHost.StartPostgresAsync` path for backend integration tests
- Added a dedicated Postgres-tagged integration slice covering health, auth bootstrap/login, schema apply, CRUD, relation reads, and migration history
- Extended `.github/workflows/test.yml` with a separate `test-postgres` service-container job while keeping packaged deployment validation in its own workflow

### 2026-04-09 — Complete Step 1.6 Docker packaging and deployment validation

> **Phase**: Phase 1 — Foundation
> **Working step**: [Step 1.6 — Docker Packaging & MVP 1 Validation](working-steps/1.6-docker-packaging.md)

Completed the Step 1.6 packaged MVP 1 story so the backend now ships with production compose entrypoints for SQLite and Postgres, reusable local smoke/demo scripts, and a dedicated GitHub Actions deployment workflow that validates both packaged variants. The live packaged Postgres run also surfaced two real deployment bugs, which were fixed before the step was closed.

**Highlights:**
- Added packaged deployment validation scripts for startup/readiness (`compose-smoke.sh`) and the full MVP 1 golden path (`packaged-mvp1-demo.sh`)
- Verified the packaged SQLite and Postgres flows locally end to end, then wired both into a dedicated `deployment-validation.yml` workflow with separate jobs and artifact capture
- Fixed packaged Postgres bootstrap issues by granting `CREATE` on `public` to `repere_data` and generating identity-backed integer primary keys for Postgres tables
### 2026-04-08 — Complete Step 1.5 auto CRUD API and docs sync

> **Phase**: Phase 1 — Foundation
> **Working step**: [Step 1.5 — Auto CRUD API](working-steps/1.5-auto-crud-api.md)

Completed the Step 1.5 MVP 1 data surface so schema-defined tables now expose a live CRUD API with filtering, sorting, pagination, relation traversal, search, and schema-aware validation, backed by admin schema-management endpoints. The follow-up hardening pass closed the audit findings around sparse fieldsets plus includes, relation sub-resource query semantics, PUT edge cases, id-only inserts, and then synced the docs and project state to the finished behavior.

**Highlights:**
- Auto-generated `/api/v1/data/*` CRUD routes are now live for the current Model JSON, including relation sub-resources and include-based traversal
- Admin schema operations (`GET/PUT /api/v1/admin/schema`, migration history, rollback, health) are documented and aligned with the shipped backend contract
- Closed TASK-035 through TASK-045, marked Step 1.5 complete, and verified the final regression pass at `346/346`

### 2026-04-05 — Fix refresh cookie defaults for local auth

> **Phase**: Phase 1 — Foundation
> **Working step**: [Step 1.4 — Basic auth (JWT, login, middleware)](working-steps/1.4-basic-auth.md)

Aligned the Step 1.4 refresh-cookie default with the documented development vs production contract so browser-based refresh works on local HTTP without losing the ability to force secure cookies explicitly. The auth docs and regression suite now cover both the local-development and production-style behaviors.

**Highlights:**
- Refresh-cookie `Secure` now defaults from the host environment when `AUTH_REFRESH_COOKIE_SECURE` is unset
- Added HTTP-level regressions that prove refresh works in development and is withheld on plain HTTP in production-style hosts
- Closed TASK-032 and updated the auth/backend docs to document the effective cookie policy

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

### 2026-04-04 — Build the schema engine and migration pipeline

> **Phase**: Phase 1 — Foundation
> **Working step**: [Step 1.2 — Schema engine (Model JSON & migrations)](working-steps/1.2-schema-engine.md)

Built the schema engine that turns Model JSON into a validated, versioned database contract with provider-aware migrations for SQLite and Postgres. This completed the foundation needed for later admin schema operations and auto-generated CRUD work.

**Highlights:**
- Added typed Model JSON parsing, round-trip serialization, stable validation errors, and schema-version persistence
- Added schema diffing, relation resolution, deterministic junction-table generation, and provider-specific migration planning
- Added schema apply/rollback orchestration, startup auto-migrate support, and broad regression coverage for validation, diffing, migrations, and relation metadata

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

### 2026-04-11 — Ship v0.1.0 MVP 1: Data API

> **Version**: [v0.1.0 — MVP 1: Data API](releases/v0.1.0.md)
> **Phase**: Phase 1 — Foundation

Published the first versioned release note for the completed MVP 1 milestone. This release captures the finished Phase 1 backend/API foundation: schema engine, auth, auto-generated CRUD API, Docker packaging, packaged validation flows, and the initial Postgres-backed regression lane.

**Highlights:**
- MVP 1 is now recorded as the first shippable Repere milestone with a dedicated semver release page
- The release bundles completed Steps 1.1 through 1.6 into a backend-only Dockerized Data API with SQLite and Postgres deployment paths
- Git tagging and artifact publication remain the final operational release-cut steps outside the changelog itself

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
