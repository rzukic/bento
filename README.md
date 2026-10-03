# DataForge (Repere)

> Self-hosted, open-source, all-in-one data management platform.
> Relational DB backbone. Built for lean companies. Easy to start, deep when needed.

Define your schema as JSON, deploy, and get a full REST API with auth, filtering, relations, and dashboards — no code required.

---

## Repository Structure

```
.
├── wiki/                   ← Source of truth: requirements, design, plan
├── docs/                   ← Implementation docs: architecture, API, backend
├── code/                   ← Source code (ASP.NET Core backend + tests)
├── project-management/     ← Task board, changelog, working steps, releases
└── CODEOWNERS
```

---

## Wiki (Design & Planning)

The **[wiki/](wiki/README.md)** is the source of truth for what the platform is and what it should do.

| Section | What's there |
|---------|-------------|
| [Architecture Overview](wiki/README.md) | Full system diagram, 13 parts index |
| [Project Plan & Build Order](wiki/plan/README.md) | 6 phases, step-by-step build order, acceptance criteria |
| [Milestones & MVPs](wiki/plan/milestones.md) | MVP 1–4 definitions and demo scenarios |
| [Technology Decisions](wiki/plan/tech-decisions.md) | Rationale for every major tech choice |
| [Dependencies](wiki/plan/dependencies.md) | Part-to-part dependency graph |
| [Glossary](wiki/glossary.md) | 51 terms defined across the platform |
| [Use Cases](wiki/usecases/) | BI Platform, Tech Analytics, Wealth Management |

### The 13 Parts

| # | Part | Description |
|---|------|-------------|
| 1 | [Core Database & Schema](wiki/parts/01-core-database/README.md) | Dual connection pools, query builder, migrations |
| 2 | [Backend API Server](wiki/parts/02-backend-api/README.md) | HTTP routing, middleware, error handling |
| 3 | [Auth & Security](wiki/parts/03-auth-security/README.md) | JWT, roles, vault mode, DDL blocklist |
| 4 | [Data Modeling](wiki/parts/04-data-modeling/README.md) | JSON/visual schema design |
| 5 | [Formula Engine](wiki/parts/05-formula-engine/README.md) | Excel-like formulas, JS sandbox, API calls |
| 6 | [Auto-Generated API](wiki/parts/06-auto-api/README.md) | CRUD endpoints, custom SQL, management API |
| 7 | [Frontend Core](wiki/parts/07-frontend-core/README.md) | SPA shell, routing, component library |
| 8 | [Data Explorer](wiki/parts/08-data-explorer/README.md) | Table viewer, inline editing |
| 9 | [Dashboard System](wiki/parts/09-dashboard-system/README.md) | Charts, KPIs, Sankey, embeddable views |
| 10 | [Forms System](wiki/parts/10-forms-system/README.md) | Schema-driven form generation |
| 11 | [Integration Engine](wiki/parts/11-integration-engine/README.md) | Notifications, counters, data import |
| 12 | [AI Assistant](wiki/parts/12-ai-assistant/README.md) | LLM workflows, change review |
| 13 | [Deployment & Packaging](wiki/parts/13-deployment/README.md) | Docker, setup wizard, config |

---

## Docs (Implementation)

The **[docs/](docs/README.md)** describe how the code actually works — architecture, backend internals, API reference.

| Section | What's there |
|---------|-------------|
| [User Guides](docs/user/README.md) | Setup wizard, browser workflows, and API usage |
| [Architecture](docs/architecture/README.md) | System topology, service boundaries, data flow |
| [Backend](docs/backend/README.md) | ASP.NET Core structure, DB access, runtime config |
| [API Reference](docs/api/README.md) | REST endpoints, auth contract, error codes |
| [Frontend](docs/frontend/README.md) | Vite/React structure (planned) |
| [Components](docs/components/README.md) | Shared UI component library (planned) |
| [AI Guidelines](docs/ai-guidelines.md) | Standards for keeping docs accurate |

---

## Code

The **[code/](code/)** directory contains the .NET backend, the React frontend, and the test suites.

```
code/
├── src/backend/            ← ASP.NET Core API (Repere.Api)
│   ├── Controllers/        ← HTTP controllers (admin, auth, data, views, preferences)
│   ├── Db/                 ← Database layer (pools, query builder, schema engine, migrations)
│   ├── Middleware/          ← Request pipeline (auth, CORS, logging, errors, rate limit)
│   ├── Services/            ← Business logic (auth, CRUD, includes, validation, health)
│   ├── Configuration/       ← Typed runtime settings
│   └── Program.cs           ← Entry point
├── src/frontend/           ← Vite + React SPA (pnpm workspace)
│   └── src/
│       ├── features/       ← Data Explorer, Schema Workbench (JSON + Visual ERD)
│       ├── stores/         ← Zustand state
│       ├── api/            ← Typed API client
│       └── test/           ← Vitest + Testing Library suites
└── tests/                  ← xUnit test suite (SQLite by default, Postgres slice via env)
```

**Tech stack**: ASP.NET Core 8, SqlKata + Dapper, PostgreSQL / SQLite, bcrypt, JWT · React 19, Vite, TypeScript, Zustand, CodeMirror

**CI**: every pull request runs the backend suite on SQLite and Postgres plus frontend typecheck, lint, and tests (see [.github/workflows/test.yml](.github/workflows/test.yml)).

---

## Project Management

The **[project-management/](project-management/README.md)** directory tracks all work.

| Section | What's there |
|---------|-------------|
| [Task Board](project-management/tasks/README.md) | Backlog, in-progress, and done tasks |
| [Changelog](project-management/CHANGELOG.md) | What changed and when |
| [Working Steps](project-management/working-steps/) | Detailed implementation logs per step |
| [Code Reviews](project-management/code-reviews/) | Audit findings for completed steps |
| [Releases](project-management/releases/README.md) | Versioned release notes |
| [AI Worker Instructions](project-management/ai-workers/README.md) | How AI agents maintain this system |

### Current Progress

**Phase 1 — Foundation** (steps 1.1–1.6)

| Step | Description | Status |
|------|-------------|--------|
| [1.1](project-management/working-steps/1.1-core-database-connection-and-query-builder.md) | Core database connection & query builder | Done |
| [1.2](project-management/working-steps/1.2-schema-engine.md) | Schema engine (Model JSON & migrations) | Done |
| [1.3](project-management/working-steps/1.3-backend-api-server.md) | Backend API server (routing, middleware, errors) | Done |
| [1.4](project-management/working-steps/1.4-basic-auth.md) | Basic auth (JWT, login, middleware) | Done |
| [1.5](project-management/working-steps/1.5-auto-crud-api.md) | Auto CRUD API (endpoints from Model JSON) | Done |
| [1.6](project-management/working-steps/1.6-docker-packaging.md) | Docker packaging & MVP 1 validation | Done |

**MVP 1 "Data API"**: Define a schema as JSON, start the container, get a full REST API with auth. Reached after Step 1.6 (released as [v0.1.0](project-management/releases/v0.1.0.md)).

**Phase 2 — Data Management** (steps 2.1–2.7)

| Step | Description | Status |
|------|-------------|--------|
| [2.1](project-management/working-steps/2.1-frontend-core-and-spa-shell.md) | Frontend core & SPA shell | Done |
| [2.2](project-management/working-steps/2.2-data-explorer-read-path.md) | Data Explorer read path | Done |
| [2.3](project-management/working-steps/2.3-views-system.md) | Views system (saved views, kanban) | Done |
| [2.4](project-management/working-steps/2.4-data-explorer-editing-and-row-ops.md) | Data Explorer editing & row operations | Done |
| [2.5](project-management/working-steps/2.5-schema-designer-json-workbench.md) | Schema Designer — JSON workbench | Done |
| [2.6](project-management/working-steps/2.6-schema-designer-visual-erd.md) | Schema Designer — visual ERD | Done |
| [2.7](project-management/working-steps/2.7-setup-wizard-and-frontend-packaging.md) | Setup wizard & frontend/proxy packaging | Done |

**MVP 2 "Spreadsheet+"**: Browser UI with setup wizard, schema designer, data grid, saved views and inline editing, packaged as proxy + frontend + backend. Reached with Step 2.7 (release record [v0.2.0](project-management/releases/v0.2.0.md) prepared).

For a non-technical path through the current product, start with [docs/user/README.md](docs/user/README.md).

---

## Quick Start

Full stack (browser UI behind the Caddy proxy, backend not published):

```bash
docker compose -f code/docker-compose.yml up --build
```

Then open `http://localhost` — a fresh installation starts in the setup wizard, which creates the admin account and can load a demo workspace.

For Postgres add the override:

```bash
docker compose -f code/docker-compose.yml -f code/docker-compose.postgres.yml up --build
```

### API only (MVP 1 shape)

```bash
docker compose -f code/docker-compose.backend-only.yml up --build
```

This publishes just the backend on port 5000. Register the bootstrap admin, define a schema via `PUT /api/v1/admin/schema`, and use the auto-generated CRUD endpoints. The Postgres override works on this base too.

## Development (frontend + backend)

```bash
dotnet run --project code/src/backend/Repere.Api.csproj
```

```bash
cd code/src/frontend && pnpm install && pnpm run dev
```

The Vite dev server on port 5173 proxies `/api/v1` to the backend on port 5000. See [docs/frontend/README.md](docs/frontend/README.md) for typecheck, lint, and test commands.
